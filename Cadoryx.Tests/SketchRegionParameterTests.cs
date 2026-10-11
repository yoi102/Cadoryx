using System.Collections.Immutable;
using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionParameterTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public SketchRegionParameterTests(Xunit.Abstractions.ITestOutputHelper output)=>this.output=output;
    private static readonly OcctGeometryKernel Kernel=new();
    private static (DocumentSnapshot Doc,CadSketch Sketch,Guid Group) Source(bool fourLayers=false)
    {
        var(doc,part)=SketchCommandTests.Document();var draft=new SketchDraft(CadSketch.Create(part,"Region drivers",RigidTransform3d.Identity),Kernel);
        foreach(double radius in fourLayers?new[]{8d,6d,4d,2d}:new[]{3d})draft.AddCircle(new(0,0),radius);
        draft.Change(s=>s with{Constraints=s.Constraints.AddRange(s.Circles.Select(c=>new FixPointConstraint(SketchConstraintId.New(),c.Center,new(0,0))))});
        var group=draft.OffsetExpressionRegions(draft.Value.Circles.Select(c=>c.Id),.2,SketchRegionAuthorJoin.Miter,Kernel);
        return(doc with{Sketches=doc.Sketches.Add(draft.Value.Id,draft.Value)},draft.Value,group);
    }
    private static ParameterBinding Bind(CadParameter parameter,ParameterTarget target)=>new(Guid.NewGuid(),parameter.Id,target);
    [Fact]
    public void LmStudioTargetPresentationRetainsAllKindsAndDoesNotRestrictOtherPropertiesToDistance()
    {
        using var raw=JsonDocument.Parse(CadAgentParameterTools.Schema);
        var wire=Cadoryx.AI.LmStudio.ToolSchemaPresentation.Create(raw.RootElement);
        var target=wire.GetProperty("properties").GetProperty("target");var fields=target.GetProperty("properties");
        Assert.Equal(new[]{"AssemblyPattern","Datum","ExactSketchCurve","Feature","SketchConstraint","SketchRegion"},
            fields.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(v=>v.GetString()).Order(StringComparer.Ordinal));
        Assert.False(fields.GetProperty("property").TryGetProperty("enum",out _));
        Assert.Equal(64,fields.GetProperty("property").GetProperty("maxLength").GetInt32());
        Assert.Equal("uuid",fields.GetProperty("owner").GetProperty("format").GetString());
        Assert.Equal("uuid",fields.GetProperty("curve").GetProperty("format").GetString());
        Assert.False(target.GetProperty("additionalProperties").GetBoolean());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task NumericSourceAxesAndGroupDistanceCommitTogetherInEitherBindingOrder(bool reverse)
    {
        var(doc,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Ellipse group parameters",RigidTransform3d.Identity),Kernel);
        d.AddEllipse(new(new(0,0),new(3,0),new(0,2),0,Math.Tau));var ellipse=d.Value.Ellipses[0];
        var group=d.OffsetExpressionRegions([ellipse.Id],.2,SketchRegionAuthorJoin.Miter,Kernel);
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver(Kernel)));var s=session.Snapshot.Sketches[d.Value.Id];
        var root=Assert.Single(SketchRegionAuthorModel.Roots(s,group));var profile=new SketchProfileReference(s.Id,s.Revision,[]){MixedBoundaryIds=root.MixedCurveIds};
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,s.Plane)),"Elliptical material",part,sketchSource:profile));
        var width=new CadParameter(Guid.NewGuid(),"width",ParameterDimension.Length,"4 mm");var height=new CadParameter(Guid.NewGuid(),"height",ParameterDimension.Length,"3 mm");
        var offset=new CadParameter(Guid.NewGuid(),"distance",ParameterDimension.Length,"height / 10");
        var bindings=new[]{Bind(width,ParameterTarget.SketchCurve(s.Id,ellipse.Id,"CosineX")),Bind(height,ParameterTarget.SketchCurve(s.Id,ellipse.Id,"SineY")),Bind(offset,ParameterTarget.SketchRegion(s.Id,group))};
        var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(new([width,height,offset],(reverse?bindings.Reverse():bindings).ToImmutableArray())));var after=session.Snapshot;
        // Independent Steiner area and Simpson integration of the source ellipse perimeter.
        const int count=16384;double step=Math.Tau/count,sum=0;
        for(int i=0;i<=count;i++){double t=i*step;sum+=(i==0||i==count?1:i%2==0?2:4)*Math.Sqrt(16*Math.Sin(t)*Math.Sin(t)+9*Math.Cos(t)*Math.Cos(t));}
        double perimeter=sum*step/3,area=12*Math.PI+.3*perimeter+.09*Math.PI;
        using(var native=OcctGeometryBridge.ReadShape(Assert.Single(after.Features.Values).Result,assets))
        {
            double standard=Math.Abs(native.InspectProperties(OcctSharp.InspectionPropertyKind.Volume).Mass);
            var adaptive=native.InspectAdaptiveProperties(OcctSharp.InspectionPropertyKind.Volume,1e-11);
            output.WriteLine($"Steiner volume={2*area:R}; stored={Assert.Single(after.Features.Values).Result.VolumeMm3:R}; default={standard:R}; adaptive={Math.Abs(adaptive.Properties.Mass):R}");
            Assert.InRange(Math.Abs(Math.Abs(adaptive.Properties.Mass)-2*area),0,1e-6);
        }
        Assert.InRange(Math.Abs(Assert.Single(after.Features.Values).Result.VolumeMm3-2*area),0,1e-6);
        var updated=after.Sketches[s.Id];Assert.Equal(new Point2d(4,0),updated.Ellipses[0].Geometry.Cosine);Assert.Equal(new Point2d(0,3),updated.Ellipses[0].Geometry.Sine);
        Assert.Equal(.3,updated.RegionOffsets[0].Distance);Assert.Equal(s.ExpressionCurves.Select(c=>c.Id),updated.ExpressionCurves.Select(c=>c.Id));
        Kernel.ConfirmExpressionRegions(updated);ParameterGraph.ValidateApplied(after);SketchAssociativeEndpoints.RequireSolved(updated);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
    }
    [Theory][InlineData("owner")][InlineData("group")][InlineData("property")][InlineData("constraint")][InlineData("index")][InlineData("curve")]
    public void TargetRequiresExactlyTheDeclaredSketchGroupDistanceIdentity(string fault)
    {
        var(doc,s,group)=Source();var target=ParameterTarget.SketchRegion(s.Id,group);
        Assert.Contains(target,ParameterTargets.All(doc));Assert.Equal(.2,ParameterTargets.Read(doc,target).Value);Assert.True(ParameterTargets.Read(doc,target).HasDimension(ParameterDimension.Length));
        target=fault switch
        {"owner"=>target with{Owner=Guid.NewGuid()},"group"=>target with{Curve=Guid.NewGuid()},"property"=>target with{Property="Radius"},
            "constraint"=>target with{Constraint=Guid.NewGuid()},"index"=>target with{Index=0},_=>target with{Curve=null}};
        Assert.Throws<CadValidationException>(()=>ParameterTargets.Read(doc,target));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SourceRadiusAndWholeDistanceAreStagedTogetherIndependentOfBindingOrder(bool reverse)
    {
        using var files=new TestFiles();var(doc,s,group)=Source(true);var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        var root=Assert.Single(SketchRegionAuthorModel.Roots(s,group));var reference=new SketchProfileReference(s.Id,s.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
        await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(doc,s.PartId,new ExtrudeRecipe(new([]),3,s.Plane)),"Material",s.PartId,sketchSource:reference));
        var feature=Assert.Single(session.Snapshot.Features.Values);var outer=s.Circles[0];var radiusConstraint=s.Constraints.OfType<RadiusConstraint>().Single(c=>c.Circle==outer.Id);
        var radius=new CadParameter(Guid.NewGuid(),"radius",ParameterDimension.Length,"10 mm");var offset=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,"radius / 20");
        var target=ParameterTarget.SketchRegion(s.Id,group);var bindings=new[]{Bind(radius,ParameterTarget.Sketch(s.Id,radiusConstraint.Id)),Bind(offset,target)};
        var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(new([radius,offset],(reverse?bindings.Reverse():bindings).ToImmutableArray())));var after=session.Snapshot;
        var updated=after.Sketches[s.Id];Assert.Equal(.5,updated.RegionOffsets.Single().Distance);Assert.Equal(10,updated.Circles[0].Radius);
        double area=Math.PI*(10.5*10.5-5.5*5.5+4.5*4.5-1.5*1.5);Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3-3*area),0,1e-6);
        Assert.Equal(s.ExpressionCurves.Select(c=>c.Id),updated.ExpressionCurves.Select(c=>c.Id));Assert.Equal(s.ExpressionCurves.Select(c=>c.Endpoints),updated.ExpressionCurves.Select(c=>c.Endpoints));
        Kernel.ConfirmExpressionRegions(updated);ParameterGraph.ValidateApplied(after);SketchAssociativeEndpoints.RequireSolved(updated);
        var storage=new CadDocumentStorage(regionVerifier:Kernel);var path=files.PathFor("region-parameters.cadoryx");await session.SaveAsync(storage,path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(60,manifest.Sections.Single(v=>v.Kind=="document").SchemaVersion);
        Assert.Equal(3,manifest.Sections.Single(v=>v.Kind=="parameters").SchemaVersion);Assert.Equal(18,manifest.Sections.Length);
        using(var loaded=await storage.LoadAsync(path,assets)){ParameterGraph.ValidateApplied(loaded.Snapshot);Assert.Equal(target,loaded.Snapshot.Parameters.Bindings.Single(b=>b.Parameter==offset.Id).Target);Assert.True(SketchRegionAuthorModel.SameTable(updated,loaded.Snapshot.Sketches[s.Id]));}
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
    }
    [Theory][InlineData("document")][InlineData("section")]
    public async Task WholeGroupTargetsCannotMasqueradeUnderEitherLegacyDocumentOrParameterSchema(string fault)
    {
        using var files=new TestFiles();var(doc,s,group)=Source();var p=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,".2 mm");
        doc=doc with{Parameters=new([p],[Bind(p,ParameterTarget.SketchRegion(s.Id,group))])};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);var path=files.PathFor("new.cadoryx");
        await storage.SaveAsync(doc,assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(v=>fault=="document"&&v.Kind=="document"?v with{SchemaVersion=58}:fault=="section"&&v.Kind=="parameters"?v with{SchemaVersion=2}:v).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("header")][InlineData("truncated")][InlineData("trailing")]
    public async Task ParameterByteGuardsRejectBeforePublishingAnyLoadedGroup(string fault)
    {
        using var files=new TestFiles();var(doc,_,_)=Source();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);var path=files.PathFor("malformed.cadoryx");
        await storage.SaveAsync(doc,assets,path);
        FormatEvolutionTests.RewriteSection(path,"parameters",bytes=>
        {
            if(fault=="trailing")return bytes.Concat(new byte[]{0}).ToArray();
            var buffer=new System.Buffers.ArrayBufferWriter<byte>();var writer=new MessagePack.MessagePackWriter(buffer);
            writer.WriteArrayHeader(2);writer.WriteArrayHeader(fault=="header"?257:1);writer.Flush();return buffer.WrittenSpan.ToArray();
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task Old58EmptyParameterSemanticsMigrateWithoutInventingGroupDistanceBindings()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);var path=files.PathFor("v58.cadoryx");
        // Actual old region bytes carry unrecorded branches. A new algorithm2
        // author cannot become an old fixture by changing its manifest labels.
        var fixture=SketchRegionJointAuthorStorageTests.Fixture("circle","cadoryx");
        using var original=await storage.LoadAsync(fixture,assets);var s=original.Snapshot.Sketches.Values.Single();File.Copy(fixture,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(v=>v.Kind=="document"?v with{SchemaVersion=58}:v.Kind=="parameters"?v with{SchemaVersion=2}:v).ToImmutableArray()});
        using var loaded=await storage.LoadAsync(path,assets);Assert.Empty(loaded.Snapshot.Parameters.Bindings);Assert.Empty(loaded.Snapshot.Parameters.Parameters);
        Assert.True(SketchRegionAuthorModel.SameTable(s,loaded.Snapshot.Sketches[s.Id]));Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
    }
    [Fact] public void ReferenceAliasCycleAndFrozenDriverRejectWithoutChangingDistanceOrOutputs()
    {
        var(doc,s,group)=Source();var target=ParameterTarget.SketchRegion(s.Id,group);
        var reference=new CadParameter(Guid.NewGuid(),"readDistance",ParameterDimension.Length,"",target);var driver=new CadParameter(Guid.NewGuid(),"distance",ParameterDimension.Length,"readDistance + .1 mm");
        Assert.Throws<CadValidationException>(()=>ParameterGraph.Evaluate(doc with{Parameters=new([reference,driver],[Bind(driver,target)])}));
        var frozen=s with{RegionOffsets=[s.RegionOffsets[0] with{IsFrozen=true,FrozenReason="Frozen explicitly"}]};var frozenDoc=doc with{Sketches=doc.Sketches.SetItem(s.Id,frozen)};
        Assert.DoesNotContain(target,ParameterTargets.All(frozenDoc));Assert.Equal(.2,ParameterTargets.Read(frozenDoc,target).Value);
        Assert.Throws<CadValidationException>(()=>ParameterGraph.Evaluate(frozenDoc with{Parameters=new([driver with{Expression=".2 mm"}],[Bind(driver,target)])}));Kernel.ConfirmExpressionRegions(s);
    }
    [Fact] public void ReselectedSourceCannotRetainAnExistingGroupDistanceTarget()
    {
        var(doc,s,group)=Source();var target=ParameterTarget.SketchRegion(s.Id,group);var p=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,".2 mm");
        doc=doc with{Parameters=new([p],[Bind(p,target)])};var g=s.RegionOffsets[0];var replacement=g.Sources[0][0] with{Entity=SketchEntityId.New()};
        var after=doc with{Sketches=doc.Sketches.SetItem(s.Id,s with{RegionOffsets=[g with{Sources=[[replacement]]}]})};
        Assert.Throws<CadValidationException>(()=>SketchRegionParameterTargets.GuardStructure(doc,after));
    }
    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task ParameterWindowOffersOneWholeDistanceAndNoOutputDistance(string language)
    {
        var culture=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            var(doc,s,group)=Source();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
            try
            {await using var editor=new ParameterEditorViewModel(document,Kernel);var target=Assert.Single(editor.Targets,t=>t.Target.Kind==ParameterTargetKind.SketchRegion);Assert.Equal(ParameterTarget.SketchRegion(s.Id,group),target.Target);
                Assert.False(string.IsNullOrWhiteSpace(target.Label));Assert.DoesNotContain(editor.Targets,t=>t.Target.Kind==ParameterTargetKind.ExactSketchCurve&&s.ExpressionCurves.Any(c=>c.Id.Value==t.Target.Curve));}
            finally{document.Detach();}
            Assert.Equal(0,assets.Count);
        }
        finally{CultureInfo.CurrentUICulture=culture;}
    }
    [Fact] public async Task InspectedWholeTargetRoundtripsThroughAgentAndCliBindings()
    {
        var(doc,s,group)=Source();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());var service=new CadCommandLineService();var tools=new CadAgentToolset(service,document);
        try
        {
            async Task<JsonElement> Edit(string action,object fields)
            {
                var args=JsonSerializer.SerializeToNode(fields)!.AsObject();args["action"]=action;args["documentId"]=session.Snapshot.Id.ToString();args["stateId"]=session.Snapshot.StateId.ToString();
                var result=await tools.ExecuteAsync(new("region-driver","cad_parameter",args.ToJsonString()),default);
                using var parsed=JsonDocument.Parse(result);Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);return parsed.RootElement.Clone();
            }
            var created=await Edit("set_expression",new{name="offset",dimension="Length",expression=".2 mm"});var parameter=created.GetProperty("id").GetGuid();
            var inspected=await tools.ExecuteAsync(new("region-read","cad_inspect",JsonSerializer.Serialize(new{area="parameter_targets",id=s.Id.Value})),default);
            using var parsed=JsonDocument.Parse(inspected);Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),inspected);
            var target=parsed.RootElement.GetProperty("items").EnumerateArray().Single(v=>v.GetProperty("target").GetProperty("kind").GetString()=="SketchRegion").GetProperty("target").Clone();
            Assert.Equal(s.Id.Value,target.GetProperty("owner").GetGuid());Assert.Equal(group,target.GetProperty("curve").GetGuid());Assert.Equal("Distance",target.GetProperty("property").GetString());
            Assert.False(target.TryGetProperty("index",out _));Assert.False(target.TryGetProperty("constraint",out _));
            var bound=await Edit("bind",new{parameterId=parameter,target});var binding=bound.GetProperty("id").GetGuid();
            await Edit("set_expression",new{id=parameter,name="offset",dimension="Length",expression=".3 mm"});Assert.Equal(.3,session.Snapshot.Sketches[s.Id].RegionOffsets[0].Distance);
            async Task<CadCommandResult> Run(string command)=>await service.ExecuteAsync(command,new CadCommandContext(document));
            var unbound=await Run($"PARAM UNBIND {binding:D}");Assert.True(unbound.Success,unbound.Message);
            var changed=await Run($"PARAM SET {parameter:D} offset LENGTH "+'"'+".4 mm"+'"');Assert.True(changed.Success,changed.Message);
            var rebound=await Run($"PARAM BIND {parameter:D} SketchRegion {s.Id.Value:D} Distance {group:D}");Assert.True(rebound.Success,rebound.Message);
            Assert.Equal(.4,session.Snapshot.Sketches[s.Id].RegionOffsets[0].Distance);ParameterGraph.ValidateApplied(session.Snapshot);
            var before=session.Snapshot;var bad=await Run($"PARAM BIND {parameter:D} SketchRegion {s.Id.Value:D} Distance {group:D} 0");Assert.False(bad.Success);Assert.Same(before,session.Snapshot);
        }
        finally{document.Detach();}
    }

    [Fact] public async Task IndependentPartCopiesTheScopedGroupDriverAndBothCopiesRecompute()
    {
        var(doc,s,group)=Source();var p=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,".2 mm");doc=doc with{Parameters=new([p],[Bind(p,ParameterTarget.SketchRegion(s.Id,group))])};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());var root=new OccurrencePath(doc.Id,[]);var second=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,s.PartId,second,"Second",RigidTransform3d.Translate(10,0,0)));
        var command=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(second));await session.ExecuteAsync(command);Assert.NotNull(command.ResultPartId);
        var cloned=Assert.Single(session.Snapshot.Sketches.Values,v=>v.PartId==command.ResultPartId);Assert.NotEqual(s.Id,cloned.Id);
        Assert.Contains(session.Snapshot.Parameters.Bindings,b=>b.Target==ParameterTarget.SketchRegion(cloned.Id,group)&&b.Parameter==p.Id);
        var book=session.Snapshot.Parameters with{Parameters=[p with{Expression=".3 mm"}]};await session.ExecuteAsync(new SetParametersCommand(book));
        Assert.Equal(.3,session.Snapshot.Sketches[s.Id].RegionOffsets[0].Distance);Assert.Equal(.3,session.Snapshot.Sketches[cloned.Id].RegionOffsets[0].Distance);ParameterGraph.ValidateApplied(session.Snapshot);
    }
}
