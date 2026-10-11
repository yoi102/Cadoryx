using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.Agent;
using Cadoryx.AI.LmStudio;
using Cadoryx.Commands;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("IsolatedNativeDeadlines")]
public sealed class SketchOffsetJoinTests
{
    [Theory][InlineData(false,"Offset selected sketch regions with round joins")][InlineData(true,"Offset selected sketch regions with round joins")]
    [InlineData(false,"草图孔岛区域圆角偏移")][InlineData(true,"草图孔岛区域圆角偏移")]
    public void OffsetSchemasRemainAvailableWithinOriginalContextBudget(bool aggressive,string prompt)
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);
        var context=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User(prompt)],tools.SelectTools(prompt,aggressive),8192,aggressive);
        Assert.Equal(12,context.Tools.Count);Assert.True(context.EstimatedPromptTokens<=8192-context.MaxOutputTokens-(aggressive?1280:640));
        var schema=context.Tools.Single(t=>t.Name=="cad_edit").Parameters;
        foreach(var presented in new[]{schema,ToolSchemaPresentation.Create(schema)})
        {
            var properties=presented.GetProperty("properties");Assert.False(presented.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(new[]{"Miter","Round","Bevel"},properties.GetProperty("join").GetProperty("enum").EnumerateArray().Select(v=>v.GetString()));
            Assert.Equal(256,properties.GetProperty("seeds").GetProperty("maxItems").GetInt32());
        }
        var longContext=AgentRequestContextBuilder.Build("CAD assistant",
            [AiChatMessage.User(new string('x',20000)),AiChatMessage.User(prompt)],tools.SelectTools(prompt,aggressive),8192,aggressive);
        Assert.Equal(prompt,longContext.Messages[^1].Content);
        Assert.Contains(longContext.Tools,t=>t.Name=="cad_edit");
        Assert.Contains(longContext.Tools,t=>t.Name=="cad_inspect");
        Assert.True(longContext.EstimatedPromptTokens<=8192-longContext.MaxOutputTokens-(aggressive?1280:640));
    }
    [Theory][InlineData(SketchOffsetJoin.Miter)][InlineData(SketchOffsetJoin.Round)][InlineData(SketchOffsetJoin.Bevel)]
    public async Task CreatedCircleDriverUpdatesOffsetBodyDrawingAndIndependentPart(SketchOffsetJoin join)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Independent offset authors",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(40,30));var outer=d.Value.Lines[0].Id;
            d.AddCircle(new(20,15),5);var hole=d.Value.Circles[0].Id;var offset=d.OffsetRegions(new[]{outer,hole},1,join);var root=offset.Roots.Single();
            var circle=d.Value.Circles.Single(c=>c.Id==root.Children.Single().CircleId);var center=d.Value.Points.Single(p=>p.Id==circle.Center);var radius=new RadiusConstraint(SketchConstraintId.New(),circle.Id,circle.Radius);
            d.Change(s=>s with{Constraints=s.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),center.Id,center.Position)).Add(radius)});
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var accepted=session.Snapshot.Sketches[d.Value.Id];
            var reference=new SketchProfileReference(accepted.Id,accepted.Revision,root.PolygonLineIds){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
            await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,accepted.Plane)),"Offset material",part,sketchSource:reference));
            var sheet=TechnicalDrawingSheet.A4Landscape("Offset driver");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,80),1));
            ParameterBook Drive(CadSketch sketch,double value)
            {
                var p=new CadParameter(Guid.NewGuid(),"offset_hole_radius",ParameterDimension.Length,value.ToString("R",CultureInfo.InvariantCulture)+" mm");
                return new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.Sketch(sketch.Id,radius.Id))]);
            }
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(Drive(accepted,3)));var driven=session.Snapshot;
            double expected=(Rectangle(40,30,1,join)-9*Math.PI)*3;var feature=driven.Features.Values.Single();
            Assert.InRange(Math.Abs(feature.Result.VolumeMm3-expected),0,expected*1e-8);Assert.Equal(5,driven.Sketches[accepted.Id].Circles.Single(c=>c.Id==hole).Radius);
            var view=driven.DrawingSheets[sheet.Id].Views.Single();Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);Assert.Equal(feature.Result.Revision,view.Sources.Single().Revision);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(driven,session.Snapshot);
            var path=new OccurrencePath(driven.Id,[]);var slot=ComponentSlotId.New();await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(path,part,slot,"Clone",RigidTransform3d.Translate(60,0,0)));
            var independent=AssemblyOccurrenceCommands.MakePartIndependent(path.Append(slot));await session.ExecuteAsync(independent);
            var source=session.Snapshot.Sketches[accepted.Id];var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==independent.ResultPartId);
            var baseline=session.Snapshot;
            await session.ExecuteAsync(new SetParametersCommand(Drive(copied,2)));var after=session.Snapshot;
            Assert.Same(source,after.Sketches[source.Id]);Assert.Equal(3,after.Sketches[source.Id].Circles.Single(c=>c.Id==circle.Id).Radius,7);
            Assert.Equal(2,after.Sketches[copied.Id].Circles.Single(c=>c.Id==circle.Id).Radius,7);
            Assert.InRange(Math.Abs(after.Features.Values.Single(f=>f.PartId==copied.PartId).Result.VolumeMm3-(Rectangle(40,30,1,join)-4*Math.PI)*3),0,expected*1e-8);
            Assert.Equal(feature.Result.Revision,after.Features[feature.Id].Result.Revision);
            string file=files.PathFor("driven-offset.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
            using var loaded=await new CadDocumentStorage().LoadAsync(file,assets);loaded.Snapshot.Validate();
            Assert.Equal(copied.Arcs.ToArray(),loaded.Snapshot.Sketches[copied.Id].Arcs.ToArray());Assert.Equal(2,loaded.Snapshot.Sketches[copied.Id].Circles.Single(c=>c.Id==circle.Id).Radius,7);
            Assert.Equal(after.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray(),loaded.Snapshot.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray());
        }
        Assert.Equal(0,assets.Count);
    }
    private static SketchDraft Draft()=>new(CadSketch.Create(DefinitionId.New(),"Join",RigidTransform3d.Identity));
    private static SketchProfile Profile(CadSketch sketch,SketchRegionReference reference)=>reference.CircleId is {} c?
        SketchProfileBuilder.Circle(sketch,c):!reference.MixedCurveIds.IsEmpty?SketchProfileBuilder.Mixed(sketch,reference.MixedCurveIds):
        SketchProfileBuilder.Polygon(sketch,reference.PolygonLineIds);
    private static void Reverse(SketchDraft draft)=>draft.Change(s=>s with
        {Lines=s.Lines.Select(l=>l with{Start=l.End,End=l.Start}).ToImmutableArray(),Arcs=s.Arcs.Select(a=>a with{Start=a.End,End=a.Start}).ToImmutableArray()});
    private static double Rectangle(double w,double h,double d,SketchOffsetJoin join)=>w*h+2*d*(w+h)+
        (d<0||join==SketchOffsetJoin.Miter?4:join==SketchOffsetJoin.Round?Math.PI:2)*d*d;
    public static IEnumerable<object[]> Rectangles()=>from policy in Enum.GetValues<SketchOffsetJoin>()
        from distance in new[]{.25,1,-.25,-1} from reverse in new[]{false,true} select new object[]{policy,distance,reverse};
    [Theory][MemberData(nameof(Rectangles))]
    public async Task RectangleHasIndependentFormulaExactNewIdsAndSingleDraftUndo(SketchOffsetJoin join,double distance,bool reverse)
    {
        var draft=Draft();draft.AddRectangle(new(0,0),new(20,12));if(reverse)Reverse(draft);var before=draft.Value;
        var created=draft.OffsetLoop(before.Lines[0].Id,distance,join);var after=draft.Value;var profile=Profile(after,created.Reference);
        Assert.Equal(before.Points.ToArray(),after.Points.Take(before.Points.Length).ToArray());
        Assert.Equal(before.Lines.ToArray(),after.Lines.Take(before.Lines.Length).ToArray());Assert.Equal(before.Constraints,after.Constraints);
        var ids=created.Reference.MixedCurveIds.IsEmpty?created.Reference.PolygonLineIds:created.Reference.MixedCurveIds;
        Assert.DoesNotContain(before.Lines[0].Id,ids);Assert.Equal(ids.Length,ids.Distinct().Count());
        Assert.Equal(join==SketchOffsetJoin.Round&&distance>0?4:0,after.Arcs.Length);
        Point2d P(SketchEntityId id)=>after.Points.Single(p=>p.Id==id).Position;
        foreach(var arc in after.Arcs)Assert.InRange(Math.Abs(SketchArcGeometry.Through(P(arc.Start),P(arc.Middle),P(arc.End)).Radius-Math.Abs(distance)),0,1e-7);
        var assets=new MemoryAssetStore();using(var body=await new OcctGeometryKernel().EvaluateAsync(new ExtrudeRecipe(profile,3,after.Plane),assets))
            Assert.InRange(Math.Abs(Rectangle(20,12,distance,join)*3-body.Geometry.VolumeMm3),0,Rectangle(20,12,distance,join)*3*1e-8);
        Assert.Equal(0,assets.Count);draft.Undo();Assert.Same(before,draft.Value);draft.Redo();Assert.Same(after,draft.Value);
    }
    public static IEnumerable<object[]> Regions()=>from policy in Enum.GetValues<SketchOffsetJoin>()
        from distance in new[]{1d,-1d} from reverse in new[]{false,true} select new object[]{policy,distance,reverse};
    [Theory][MemberData(nameof(Regions))]
    public async Task FourLevelRegionPreservesMaterialHierarchyAndNativeSolids(SketchOffsetJoin join,double distance,bool reverse)
    {
        var d=Draft();d.AddRectangle(new(0,0),new(100,80));var outer=d.Value.Lines[0].Id;
        d.AddCircle(new(50,40),20);var hole=d.Value.Circles[0].Id;d.AddRectangle(new(40,30),new(60,50));var island=d.Value.Lines[^4].Id;
        d.AddCircle(new(50,40),4);var inner=d.Value.Circles[^1].Id;if(reverse)Reverse(d);var before=d.Value;
        var result=d.OffsetRegions(new[]{outer,hole,island,inner},distance,join);var root=result.Roots.Single();
        Assert.Equal(new[]{0,1,2,3},result.Boundaries.Select(b=>b.Depth).Order().ToArray());
        Assert.NotNull(root.Children.Single().Children.Single().Children.Single().CircleId);
        Assert.Equal(before.Circles.ToArray(),d.Value.Circles.Take(2).ToArray());
        var reference=new SketchProfileReference(d.Value.Id,d.Value.Revision,root.PolygonLineIds,root.CircleId)
            {MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
        var snapshot=DocumentSnapshot.Create("Offset hierarchy") with{Sketches=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(d.Value.Id,d.Value)};
        var recipe=reference.Resolve(snapshot,d.Value.PartId,new ExtrudeRecipe(new([]),3,d.Value.Plane));
        double expected=3*(Rectangle(100,80,distance,join)-Math.PI*Math.Pow(20-distance,2)+Rectangle(20,20,distance,join)-Math.PI*Math.Pow(4-distance,2));
        var assets=new MemoryAssetStore();using(var body=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
        using(var native=OcctGeometryBridge.ReadShape(body.Geometry,assets))
        {Assert.True(native.IsValid);Assert.Equal(2,native.GetTopologySummary().UniqueCounts.SolidCount);Assert.InRange(Math.Abs(expected-body.Geometry.VolumeMm3),0,expected*1e-8);}
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(SketchOffsetJoin.Round)][InlineData(SketchOffsetJoin.Bevel)]
    public void SharpCornersAreExplicitAndFailuresRetainRedo(SketchOffsetJoin join)
    {
        var d=Draft();d.AddLine(new(0,0),new(100,0));var first=d.Value.Lines[0];d.AddLine(new(100,0),new(100,.1),start:first.End);
        d.AddLine(new(100,.1),new(0,0),start:d.Value.Lines[1].End,end:first.Start);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.OffsetLoop(first.Id,1));Assert.Same(before,d.Value);
        var created=d.OffsetLoop(first.Id,1,join);Profile(created.Sketch,created.Reference).Validate();d.Undo();Assert.True(d.CanRedo);
        foreach(double distance in new[]{0d,double.NaN,double.PositiveInfinity})
        {Assert.Throws<CadValidationException>(()=>d.OffsetLoop(first.Id,distance,join));Assert.Same(before,d.Value);Assert.True(d.CanRedo);}
        Assert.Throws<CadValidationException>(()=>d.OffsetLoop(first.Id,1,(SketchOffsetJoin)99));Assert.True(d.CanRedo);
        d.Redo();Assert.Same(created.Sketch,d.Value);
    }
    [Theory][InlineData(128,true)][InlineData(129,false)]
    public void GeneratedRoundBridgesCountTowardMixedBoundaryBudget(int count,bool accepted)
    {
        var d=Draft();var points=Enumerable.Range(0,count).Select(i=>new SketchPoint(SketchEntityId.New(),new(100*Math.Cos(Math.Tau*i/count),100*Math.Sin(Math.Tau*i/count)))).ToImmutableArray();
        d.Change(s=>s with{Points=points,Lines=Enumerable.Range(0,count).Select(i=>new SketchLine(SketchEntityId.New(),points[i].Id,points[(i+1)%count].Id)).ToImmutableArray()});
        var before=d.Value;
        if(accepted){var result=d.OffsetLoop(before.Lines[0].Id,1,SketchOffsetJoin.Round);Assert.Equal(256,result.Reference.MixedCurveIds.Length);}
        else {Assert.Throws<CadValidationException>(()=>d.OffsetLoop(before.Lines[0].Id,1,SketchOffsetJoin.Round));Assert.Same(before,d.Value);}
    }
    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task EditorPolicyAndDistanceInvalidatePreviewWithoutReapplyingOffset(string language)
    {
        var previous=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
            await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Editor",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,12));
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var source=session.Snapshot.Sketches[d.Value.Id];var baseline=session.Snapshot;
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                await using var editor=new SketchEditorViewModel(document,kernel,solver,source,false){Tool="OffsetLoop",OffsetJoin=SketchOffsetJoin.Round,OffsetDistance=1};
                Assert.Equal(3,editor.OffsetJoins.Count);Assert.All(editor.OffsetJoins,option=>Assert.False(string.IsNullOrWhiteSpace(option.Label)));
                editor.EditCurve(source.Lines[0].Id,new(5,0));var result=editor.Sketch;Assert.Equal(4,result.Arcs.Length);
                Assert.Single(editor.SelectedEntities);Assert.DoesNotContain(source.Lines[0].Id,editor.SelectedEntities);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(baseline,session.Snapshot);
                result=editor.Sketch;
                editor.OffsetJoin=SketchOffsetJoin.Bevel;Assert.False(editor.CanConfirm);Assert.Same(result,editor.Sketch);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                result=editor.Sketch;
                editor.OffsetDistance=2;Assert.False(editor.CanConfirm);Assert.Same(result,editor.Sketch);
                editor.CancelCommand.Execute(null);Assert.Same(baseline,session.Snapshot);Assert.Null(document.PreviewScene);
            }
            finally{document.Detach();}
            Assert.Equal(0,assets.Count);
        }
        finally{CultureInfo.CurrentUICulture=previous;}
    }
    [Theory][InlineData(SketchOffsetJoin.Miter,false)][InlineData(SketchOffsetJoin.Round,false)][InlineData(SketchOffsetJoin.Bevel,false)]
    [InlineData(SketchOffsetJoin.Miter,true)][InlineData(SketchOffsetJoin.Round,true)][InlineData(SketchOffsetJoin.Bevel,true)]
    public async Task TerminalAndAgentReturnNewAuthorsCommitOnceAndSurviveReopen(SketchOffsetJoin join,bool agent)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Tools",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,12));
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var before=session.Snapshot;var source=before.Sketches[d.Value.Id];
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                var service=new CadCommandLineService();var args=new Dictionary<string,object>{["action"]=agent?"offset_sketch_regions":"offset_sketch_loop",
                    ["sketchId"]=source.Id.ToString(),["revision"]=source.Revision,["seeds"]=new[]{source.Lines[0].Id.ToString()},["distanceMm"]=1};
                if(join!=SketchOffsetJoin.Miter)args["join"]=join.ToString();
                string response;
                if(agent)
                {args["documentId"]=before.Id.ToString();args["stateId"]=before.StateId.ToString();response=await new CadAgentToolset(service,document).ExecuteAsync(new AiToolCall("offset","cad_edit",JsonSerializer.Serialize(args)),default);}
                else {var executed=await service.ExecuteAsync("SKETCH_OFFSET "+JsonSerializer.Serialize(args),new CadCommandContext(document));Assert.True(executed.Success,executed.Message);response=executed.Message;}
                using var json=JsonDocument.Parse(response);Assert.True(json.RootElement.GetProperty("success").GetBoolean(),response);
                Assert.Equal(join.ToString(),json.RootElement.GetProperty("join").GetString());var created=json.RootElement.GetProperty("boundaries")[0].GetProperty("created");
                var resultIds=created.GetProperty(join==SketchOffsetJoin.Round?"mixedCurveIds":"polygonLineIds").EnumerateArray().Select(v=>new SketchEntityId(Guid.Parse(v.GetString()!))).ToImmutableArray();
                var after=session.Snapshot;var current=after.Sketches[source.Id];Assert.NotEqual(source.Revision,current.Revision);Assert.DoesNotContain(source.Lines[0].Id,resultIds);
                await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
                var reference=new SketchProfileReference(current.Id,current.Revision,join==SketchOffsetJoin.Round?[]:resultIds){MixedBoundaryIds=join==SketchOffsetJoin.Round?resultIds:[]};
                await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,current.Plane)),"Offset body",part,sketchSource:reference));
                Assert.InRange(Math.Abs(Rectangle(20,12,1,join)*3-session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3),0,1e-5);
                string path=files.PathFor("join.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
                using var reopened=await new CadDocumentStorage().LoadAsync(path,assets);reopened.Snapshot.Validate();
                Assert.Equal(current.Arcs.ToArray(),reopened.Snapshot.Sketches[current.Id].Arcs.ToArray());Assert.Equal(current.Lines.ToArray(),reopened.Snapshot.Sketches[current.Id].Lines.ToArray());
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("extra")][InlineData("bad-join")][InlineData("wrong-revision")][InlineData("wrong-sketch")]
    [InlineData("bad-seed")][InlineData("collapse")][InlineData("multiple-loop-seeds")][InlineData("cancel")]
    public async Task StrictOffsetFailurePreservesDocumentHistoryAndAssets(string fault)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Reject",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,12));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var before=session.Snapshot;long generation=session.Generation;
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var args=new Dictionary<string,object>{["action"]="offset_sketch_loop",["sketchId"]=d.Value.Id.ToString(),["revision"]=before.Sketches[d.Value.Id].Revision,
                ["seeds"]=new[]{d.Value.Lines[0].Id.ToString()},["distanceMm"]=1,["join"]="Round"};
            if(fault=="extra")args["proof"]="fake";if(fault=="bad-join")args["join"]="round";if(fault=="wrong-revision")args["revision"]=Guid.NewGuid();
            if(fault=="wrong-sketch")args["sketchId"]=before.Id.ToString();if(fault=="bad-seed")args["seeds"]=new[]{Guid.NewGuid().ToString()};
            if(fault=="collapse")args["distanceMm"]=-6;if(fault=="multiple-loop-seeds")args["seeds"]=d.Value.Lines.Take(2).Select(l=>l.Id.ToString()).ToArray();
            var service=new CadCommandLineService();var token=fault=="cancel"?new CancellationToken(true):default;
            var result=await service.ExecuteAsync("SKETCH_OFFSET "+JsonSerializer.Serialize(args),new CadCommandContext(document),token);
            Assert.False(result.Success);Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }
    [Theory]
    [InlineData("offset_sketch_loop","readonly")][InlineData("offset_sketch_regions","readonly")]
    [InlineData("offset_sketch_loop","closing")][InlineData("offset_sketch_regions","closing")]
    [InlineData("offset_sketch_loop","stale-state")][InlineData("offset_sketch_regions","stale-state")]
    [InlineData("offset_sketch_loop","foreign-document")][InlineData("offset_sketch_regions","foreign-document")]
    [InlineData("offset_sketch_loop","duplicate")][InlineData("offset_sketch_regions","duplicate")]
    [InlineData("offset_sketch_loop","empty-seeds")][InlineData("offset_sketch_regions","empty-seeds")]
    public async Task AgentOffsetRejectsUneditableOrUnprovenAuthorState(string action,string fault)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Guarded offset",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,12));
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            var captured=session.Snapshot;var sketch=captured.Sketches[d.Value.Id];
            var arguments=new Dictionary<string,object>{["action"]=action,["documentId"]=captured.Id.ToString(),["stateId"]=captured.StateId.ToString(),
                ["sketchId"]=sketch.Id.ToString(),["revision"]=sketch.Revision,["seeds"]=new[]{sketch.Lines[0].Id.ToString()},["distanceMm"]=1,["join"]="Round"};
            if(fault=="readonly")
            {
                using var asset=assets.Stage(new byte[]{1,2,3});
                await session.ExecuteAsync(new EditDocumentCommand("Preserve future extension",s=>s with{Extensions=[new("vendor.future",1,asset.Id,"vendor")]}));
                arguments["stateId"]=session.Snapshot.StateId.ToString();
            }
            if(fault=="stale-state")await session.ExecuteAsync(DocumentEdits.SetGrid(captured.Settings.Grid with{SpacingMm=17}));
            if(fault=="foreign-document")arguments["documentId"]=Guid.NewGuid().ToString();
            if(fault=="empty-seeds")arguments["seeds"]=Array.Empty<string>();
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                if(fault=="closing")document.IsClosingRequested=true;
                var before=session.Snapshot;var usage=session.HistoryUsage;long generation=session.Generation;int count=assets.Count;
                string input=JsonSerializer.Serialize(arguments);if(fault=="duplicate")input=input[..^1]+",\"join\":\"Bevel\"}";
                var response=await new CadAgentToolset(new CadCommandLineService(),document).ExecuteAsync(new("guard","cad_edit",input),default);
                using var parsed=JsonDocument.Parse(response);Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),response);
                Assert.Same(before,session.Snapshot);Assert.Equal(usage,session.HistoryUsage);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task OffsetCommitRechecksRunningCancellationAndConcurrentState(bool agent,bool cancelled)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var dispatcher=new OffsetCommitDispatcher();
        await using(var session=new CadDocumentSession(initial,assets,kernel,dispatcher))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Pending offset",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,12));
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[d.Value.Id];
            var reference=new SketchProfileReference(sketch.Id,sketch.Revision,sketch.Lines.Select(l=>l.Id).ToImmutableArray());
            var recipe=reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane));
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Existing source",part,sketchSource:reference));
            var captured=session.Snapshot;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());using var cancel=new CancellationTokenSource();
            Task? pending=null;Task<string>? agentPending=null;Task<CadCommandResult>? terminalPending=null;
            Exception? primaryFailure=null;
            try
            {
                var arguments=new Dictionary<string,object>{["action"]="offset_sketch_loop",["sketchId"]=sketch.Id.ToString(),["revision"]=sketch.Revision,
                    ["seeds"]=new[]{sketch.Lines[0].Id.ToString()},["distanceMm"]=1,["join"]="Round"};
                var service=new CadCommandLineService();dispatcher.Pause=true;
                if(agent)
                {
                    arguments["documentId"]=captured.Id.ToString();arguments["stateId"]=captured.StateId.ToString();
                    pending=agentPending=new CadAgentToolset(service,document).ExecuteAsync(new("pending","cad_edit",JsonSerializer.Serialize(arguments)),cancel.Token);
                }
                else pending=terminalPending=service.ExecuteAsync("SKETCH_OFFSET "+JsonSerializer.Serialize(arguments),new CadCommandContext(document),cancel.Token);
                // Offset preserves the existing source profile, so its dependent body can
                // legitimately reuse geometry. Gate the actual prepared commit rather than
                // assuming the production path must call the native kernel again.
                await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));Assert.True(session.IsBusy);dispatcher.Pause=false;
                if(cancelled)cancel.Cancel();else await session.ExecuteAsync(DocumentEdits.SetGrid(captured.Settings.Grid with{SpacingMm=17}));
                var before=session.Snapshot;var usage=session.HistoryUsage;long generation=session.Generation;int count=assets.Count;
                dispatcher.Release.TrySetResult();
                if(agent&&cancelled)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
                else if(agent)
                {
                    string response=await agentPending!;
                    using var parsed=JsonDocument.Parse(response);Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),response);
                }
                else
                {
                    var result=await terminalPending!;
                    Assert.False(result.Success);Assert.Equal(cancelled,result.IsCancelled);
                }
                pending=null;
                Assert.Same(before,session.Snapshot);Assert.Equal(usage,session.HistoryUsage);Assert.Equal(generation,session.Generation);
                Assert.False(session.IsBusy);Assert.Equal(count,assets.Count);
                Assert.Equal(sketch.Revision,session.Snapshot.Sketches[sketch.Id].Revision);
                Assert.Equal(captured.Features.Values.Single().Result,session.Snapshot.Features.Values.Single().Result);
            }
            catch(Exception ex){primaryFailure=ex;throw;}
            finally
            {
                dispatcher.Pause=false;dispatcher.Release.TrySetResult();
                // Drain the real operation, without running commit assertions a second
                // time during cleanup. Preserve a readiness failure if draining also fails.
                try
                {
                    if(pending is not null)
                    {
                        try{await pending;}
                        catch(OperationCanceledException) when(agent&&cancelled){}
                        catch(Exception cleanup) when(primaryFailure is not null)
                        {throw new AggregateException("Offset test failed and its pending operation also failed during cleanup.",primaryFailure,cleanup);}
                    }
                }
                finally{document.Detach();}
            }
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class OffsetCommitDispatcher:ISessionDispatcher
    {
        internal bool Pause {get;set;}
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task InvokeAsync(Action action)
        {if(Pause){Entered.TrySetResult();await Release.Task;}action();}
    }
}
