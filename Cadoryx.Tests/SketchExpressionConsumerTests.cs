using System.Collections.Immutable;
using System.Globalization;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionConsumerTests
{
    private static RigidTransform3d PathPlane=>new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0));
    private static async Task<(CadSketch Sketch,SketchEntityId Curve)> Source(DefinitionId part,int kind,bool reverse,bool finite=false)
    {
        var(d,source)=SketchExpressionEntityTests.Source(part,kind);d.SetConstruction([source],true);var id=d.AddExactOffset(source,.03);
        if(finite)d.EditExpression(id,d.Value.ExpressionCurves.Single(c=>c.Id==id).Geometry.Trim(.1,.8));
        if(reverse)d.EditExpression(id,d.Value.ExpressionCurves.Single(c=>c.Id==id).Geometry.Reverse());d.SetConstruction([id],false);
        var r=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(r.Succeeded,r.Diagnostic);return(r.Solution!,id);
    }
    public static IEnumerable<object[]> Profiles()=>from kind in Enumerable.Range(1,6) from reverse in new[]{false,true} select new object[]{kind,reverse};
    [Theory][MemberData(nameof(Profiles))]
    public async Task StandaloneExactAuthorsAreOfferedAsClosedOrChordProfilesWithoutCreatingAuthorIds(int kind,bool reverse)
    {
        var(s,id)=await Source(DefinitionId.New(),kind,reverse);var before=s.ExpressionCurves.Single().Endpoints!;var points=s.Points.ToArray();
        Assert.Contains(SketchMixedLoops.Find(s),loop=>loop.SequenceEqual(new[]{id}));
        var p=SketchProfileBuilder.Mixed(s,[id]);var e=s.ExpressionCurves.Single(c=>c.Id==id).Geometry;
        Assert.True(e.SameValues(p.BoundaryCurves[0].Expression!));bool closed=double.Hypot(e.At(0).X-e.At(1).X,e.At(0).Y-e.At(1).Y)<=1e-7;
        Assert.Equal(closed?1:2,p.BoundaryCurves.Length);if(!closed)Assert.Equal(new(e.At(1),e.At(0)),p.BoundaryCurves[1]);
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();double area=kernel.InspectRegion(p.BoundaryCurves.Select(ExactSketchCurve.FromBoundary).ToImmutableArray()).Area;
        using(var body=await kernel.EvaluateAsync(new ExtrudeRecipe(p,3,s.Plane),assets))Assert.InRange(Math.Abs(body.Geometry.VolumeMm3-3*area),0,1e-7);
        Assert.Equal(points,s.Points.ToArray());Assert.Equal(before,s.ExpressionCurves.Single(c=>c.Id==id).Endpoints);Assert.Equal(0,assets.Count);
    }
    public static IEnumerable<object[]> Paths()=>from kind in Enumerable.Range(0,8) from reverse in new[]{false,true} from edgeReverse in new[]{false,true} select new object[]{kind,reverse,edgeReverse};
    [Theory][MemberData(nameof(Paths))]
    public async Task EveryFiniteExpressionFamilyResolvesByTypedIdAndPreservesIndependentNativeValues(int kind,bool reverse,bool edgeReverse)
    {
        var(initial,part)=SketchCommandTests.Document();var(s,id)=await Source(part,kind,reverse,true);var doc=initial with{Sketches=initial.Sketches.Add(s.Id,s)};
        var reference=new SketchPathReference(s.Id,s.Revision,[new(id,edgeReverse)]);var path=reference.Resolve(doc,part);var c=Assert.Single(path.Curves);
        var original=s.ExpressionCurves.Single(c=>c.Id==id).Geometry;var expected=edgeReverse?original.Reverse():original;
        Assert.True(expected.SameValues(c.Expression!));Assert.Equal(reference,path.Source);Assert.Equal(s.Plane,path.Placement);
        foreach(double t in new[]{0d,.37,1d})SketchAssociativeEndpointTests.Near(expected.At(t),new OcctGeometryKernel().EvaluateExpression(c.Expression!,t).Point,1e-9);
        var section=new AuthoringSection(SketchProfile.FromCircle(new(0,0),.1),RigidTransform3d.Identity);
        Assert.False(GuidedAuthoringSources.HasUnavailableEntities(doc,new SweepRecipe(section,path)));
        var changed=new SketchDraft(s);changed.SetConstruction([id],true);
        Assert.True(GuidedAuthoringSources.HasUnavailableEntities(doc with{Sketches=doc.Sketches.SetItem(s.Id,changed.Value)},new SweepRecipe(section,path)));
        Assert.Throws<CadValidationException>(()=>reference.Resolve(doc with{Sketches=doc.Sketches.SetItem(s.Id,changed.Value)},part));
    }
    [Theory][InlineData("missing")][InlineData("construction")][InlineData("unsolved")][InlineData("revision")][InlineData("owner")][InlineData("duplicate")][InlineData("order")]
    public async Task ExpressionPathGuardsRejectBrokenIntentBeforeReturningAnyChain(string mutation)
    {
        var(initial,part)=SketchCommandTests.Document();var(s,id)=await Source(part,3,false,true);var reference=new SketchPathReference(s.Id,s.Revision,[new(id)]);
        if(mutation=="missing")s=s with{ExpressionCurves=[]};
        if(mutation=="construction")s=s with{ExpressionCurves=[s.ExpressionCurves.Single() with{IsConstruction=true}]};
        if(mutation=="unsolved")
        {var e=s.ExpressionCurves.Single().Endpoints!;s=s with{Points=s.Points.Select(p=>p.Id==e.Start?p with{Position=new(p.Position.X+.01,p.Position.Y)}:p).ToImmutableArray()};}
        if(mutation=="revision")reference=reference with{Revision=Guid.NewGuid()};
        if(mutation=="duplicate")reference=reference with{Edges=[new(id),new(id)]};
        if(mutation=="order")
        {var d=new SketchDraft(s);d.AddLine(new(20,20),new(21,21));s=d.Value;reference=reference with{Edges=[new(id),new(s.Lines[^1].Id)]};}
        var doc=initial with{Sketches=initial.Sketches.Add(s.Id,s)};Assert.Throws<CadValidationException>(()=>reference.Resolve(doc,mutation=="owner"?DefinitionId.New():part));
    }
    private static async Task<(CadSketch Section,SketchEntityId SectionCurve,CadSketch Path,SketchEntityId PathCurve)> SweepAuthors(Fixture f,bool reverse,bool bezier=false)
    {
        var section=new SketchDraft(CadSketch.Create(f.Part,"Section",RigidTransform3d.Translate(.1,0,0)));
        if(bezier)section.AddBezier(new(7.5,0),new(8,1),new(8.5,0),construction:true);else section.AddCircle(new(0,0),.5,construction:true);
        var source=bezier?section.Value.Beziers[0].Id:section.Value.Circles[0].Id;var sec=section.AddExactOffset(source,bezier ? .03 : .05);section.SetConstruction([sec],false);
        section.Change(s=>s with{Constraints=[..s.Constraints,..s.Points.Where(p=>p.Id!=s.ExpressionCurves.Single().Endpoints!.Start&&p.Id!=s.ExpressionCurves.Single().Endpoints!.End)
            .Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position))]});
        var path=new SketchDraft(CadSketch.Create(f.Part,"Path",PathPlane));path.AddLine(reverse?new(0,10):new(0,0),reverse?new(0,0):new(0,10),construction:true);
        var line=path.Value.Lines[0];var origin=reverse?line.End:line.Start;var expr=path.AddExactOffset(line.Id,reverse?-.1:.1);path.SetConstruction([expr],false);
        path.Change(s=>s with{Constraints=[new FixPointConstraint(SketchConstraintId.New(),origin,new(0,0)),new VerticalConstraint(SketchConstraintId.New(),line.Id),new LengthConstraint(SketchConstraintId.New(),line.Id,10)]});
        await f.Store(section.Value);await f.Store(path.Value);return(f.Session.Snapshot.Sketches[section.Value.Id],sec,f.Session.Snapshot.Sketches[path.Value.Id],expr);
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task AssociatedExpressionSectionAndDirectedPathRebuildUndoStoreAndRecoverWithoutGuessingTopology(bool reverse,bool driveSection)
    {
        using var files=new TestFiles();await using var f=new Fixture();var(s,sec,p,edge)=await SweepAuthors(f,reverse);
        var sectionSource=SketchProfileReference.CreateMixed(s,[sec]);var pathSource=new SketchPathReference(p.Id,p.Revision,[new(edge,reverse)]);
        var recipe=new SweepRecipe(new(SketchProfileBuilder.Mixed(s,[sec]),s.Plane){Source=sectionSource},pathSource.Resolve(f.Session.Snapshot,f.Part));
        Assert.False(GuidedAuthoringSources.HasUnavailableEntities(f.Session.Snapshot,recipe));await f.Session.ExecuteAsync(new AddBodyCommand(recipe,"Expression sweep",f.Part));
        var before=f.Session.Snapshot;var feature=before.Features.Values.Single();double radius=.55,length=10;
        if(driveSection)
        {var parameter=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,"0.08 mm");await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.SketchCurve(s.Id,sec,"OffsetDistance",0))])));radius=.58;}
        else
        {var constraint=p.Constraints.OfType<LengthConstraint>().Single();await f.Store(p with{Constraints=p.Constraints.Replace(constraint,constraint with{Length=15})});length=15;}
        var after=f.Session.Snapshot;var current=Assert.IsType<SweepRecipe>(after.Features[feature.Id].Recipe);
        Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3-Math.PI*radius*radius*length),0,1e-7);
        Assert.Equal(after.Sketches[p.Id].Revision,current.Path.Source!.Revision);Assert.Equal(after.Sketches[s.Id].Revision,current.Section.Source!.Revision);
        Assert.Equal(pathSource.Edges.ToArray(),current.Path.Source.Edges.ToArray());Assert.NotEqual(feature.Result.AssetId,after.Features[feature.Id].Result.AssetId);
        Assert.Equal(s.ExpressionCurves.Single().Endpoints,after.Sketches[s.Id].ExpressionCurves.Single().Endpoints);Assert.Equal(p.ExpressionCurves.Single().Endpoints,after.Sketches[p.Id].ExpressionCurves.Single().Endpoints);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        var storage=new CadDocumentStorage();var file=files.PathFor("sweep.cadoryx");await f.Session.SaveAsync(storage,file);using(var loaded=await storage.LoadAsync(file,f.Assets))
        {loaded.Snapshot.Validate();var restored=Assert.IsType<SweepRecipe>(loaded.Snapshot.Features[feature.Id].Recipe);Assert.True(SketchMixedProfile.SameLoop(current.Path.Curves,restored.Path.Curves));Assert.Equal(current.Path.Source.Edges.ToArray(),restored.Path.Source!.Edges.ToArray());}
        RecoveryKey key;using(var recovery=new CadRecoveryStore(files.PathFor("recovery"),storage)){var id=Guid.NewGuid();key=new(recovery.RunId,id);await recovery.WriteAsync(id,after,f.Assets,null);}
        using var reader=new CadRecoveryStore(files.PathFor("recovery"),storage);using var recovered=await reader.OpenAsync(key,f.Assets);Assert.Equal(after.StateId,recovered.Document.Snapshot.StateId);
        var oldPath=after.Sketches[p.Id];var edit=new SketchDraft(oldPath);edit.SetConstruction([edge],true);await f.Store(edit.Value);
        var stale=f.Session.Snapshot;Assert.True(stale.Features[feature.Id].IsStale);Assert.Equal(after.Features[feature.Id].Result,stale.Features[feature.Id].Result);
        await f.Session.UndoAsync();Assert.Same(after,f.Session.Snapshot);edit=new(oldPath);edit.RemoveEntities([edge]);await f.Store(edit.Value);
        Assert.True(f.Session.Snapshot.Features[feature.Id].IsStale);Assert.Equal(after.Features[feature.Id].Result,f.Session.Snapshot.Features[feature.Id].Result);
    }
    public static IEnumerable<object[]> Editors()=>from language in new[]{"en-US","zh-CN","ja-JP"} from mode in new[]{"Extrude","Revolve","Sweep","Loft"} select new object[]{language,mode};
    [Theory][MemberData(nameof(Editors))]
    public async Task ActualEditorChoicesRetainExpressionIntentAndUndoOrCancelNativeConsumers(string language,string mode)
    {
        var previous=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            await using var f=new Fixture();var(s,sec,p,edge)=await SweepAuthors(f,false,true);var before=f.Session.Snapshot;
            f.Document.SelectedTargetPart=f.Part;f.Document.ToolKind=mode is "Extrude" or "Revolve"?mode:"Extrude";
            Assert.Contains(f.Document.SketchProfiles,c=>c.Source.SketchId==s.Id&&c.Source.MixedBoundaryIds.SequenceEqual(new[]{sec}));
            if(mode=="Revolve")
            {
                f.Document.UseLinkedSketch=true;f.Document.AngleDegrees=360;
                f.Document.SelectedSketchProfile=f.Document.SketchProfiles.Single(c=>c.Source.SketchId==s.Id&&c.Source.MixedBoundaryIds.SequenceEqual(new[]{sec}));
                await f.Document.PreviewCommand.ExecuteAsync(null);Assert.NotNull(f.Document.PreviewScene);Assert.True(f.Document.PreviewScene.Items.Any(i=>i.Geometry.Kind==BodyKind.Solid));
                await f.Document.ConfirmCommand.ExecuteAsync(null);var feature=f.Session.Snapshot.Features.Values.Single();var recipe=Assert.IsType<RevolveRecipe>(feature.Recipe);
                Assert.Contains(recipe.Profile.BoundaryCurves,c=>c.Expression is not null);Assert.Equal(s.Id,feature.SketchSource!.SketchId);Assert.Equal(new[]{sec},feature.SketchSource.MixedBoundaryIds.ToArray());
                await f.Session.UndoAsync();
            }
            else if(mode=="Extrude")
            {
                await using var editor=new ExtrusionEditorViewModel(f.Document,f.Kernel);editor.SelectedProfile=editor.Profiles.Single(c=>c.Source?.SketchId==s.Id&&c.Source.MixedBoundaryIds.SequenceEqual(new[]{sec}));
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);editor.CancelCommand.Execute(null);
            }
            else
            {
                await using var editor=new GuidedEditorViewModel(f.Document,f.Kernel,mode=="Sweep"?GuidedEditorKind.Sweep:GuidedEditorKind.Loft);
                var profile=editor.Profiles.Single(c=>c.Section.Source?.SketchId==s.Id&&c.Section.Source.MixedBoundaryIds.SequenceEqual(new[]{sec}));
                if(mode=="Sweep")
                {
                    editor.SelectedSweepProfile=profile;editor.SelectedPathSketch=editor.PathSketches.Single(c=>c.Value==p.Id);editor.SelectedAvailableEdge=editor.AvailableEdges.Single(c=>c.Entity==edge);editor.AddEdgeCommand.Execute(null);
                    Assert.True(Assert.IsType<SweepRecipe>(editor.BuildRecipe()).Path.Curves[0].Expression!.SameValues(p.ExpressionCurves.Single().Geometry));
                }
                else
                {
                    editor.AddStationCommand.Execute(null);editor.SelectedAssignmentProfile=profile;editor.AssignProfileCommand.Execute(null);editor.AddStationCommand.Execute(null);editor.SelectedAssignmentProfile=profile;editor.AssignProfileCommand.Execute(null);
                    editor.DetachStationCommand.Execute(null);editor.SelectedStation!.Frame.Z=5;
                }
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);editor.CancelCommand.Execute(null);
            }
            Assert.Same(before,f.Session.Snapshot);Assert.Null(f.Document.PreviewScene);
        }finally{CultureInfo.CurrentUICulture=previous;}
    }
    [Fact] public async Task ExplicitClosureOffersOneWholeChainAndNeverAnImplicitChordThroughAttachedEdges()
    {
        var(s,id)=await Source(DefinitionId.New(),3,false);var d=new SketchDraft(s);var c=s.ExpressionCurves.Single();var e=c.Endpoints!;
        d.AddLine(c.Geometry.At(1),c.Geometry.At(0),e.End,e.Start);var line=d.Value.Lines[^1].Id;var loops=SketchMixedLoops.Find(d.Value);
        Assert.DoesNotContain(loops,l=>l.SequenceEqual(new[]{id}));Assert.Contains(loops,l=>l.ToHashSet().SetEquals([id,line]));
    }
    [Fact] public async Task AStandaloneLineExpressionStaysAPathAndDoesNotAdvertiseAZeroAreaChordProfile()
    {
        var(initial,part)=SketchCommandTests.Document();var(s,id)=await Source(part,0,false);Assert.DoesNotContain(SketchMixedLoops.Find(s),l=>l.SequenceEqual(new[]{id}));
        var path=new SketchPathReference(s.Id,s.Revision,[new(id)]).Resolve(initial with{Sketches=initial.Sketches.Add(s.Id,s)},part);Assert.NotNull(path.Curves[0].Expression);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LinkedExpressionLoftStationDriversKeepExplicitCorrespondenceAndNativeFrustumVolume(bool reverse)
    {
        using var files=new TestFiles();await using var f=new Fixture();var stations=new List<CadSketch>();var ids=new List<SketchEntityId>();
        foreach(var tuple in new[]{(.5,0d),(.8,6d)})
        {
            var d=new SketchDraft(CadSketch.Create(f.Part,"Station",RigidTransform3d.Translate(0,0,tuple.Item2)));d.AddCircle(new(0,0),tuple.Item1,construction:true);
            var id=d.AddExactOffset(d.Value.Circles[0].Id,.05);if(reverse)d.EditExpression(id,d.Value.ExpressionCurves.Single().Geometry.Reverse());d.SetConstruction([id],false);
            d.Change(s=>s with{Constraints=s.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),s.Circles[0].Center,new(0,0)))});await f.Store(d.Value);stations.Add(f.Session.Snapshot.Sketches[d.Value.Id]);ids.Add(id);
        }
        var channel=Guid.NewGuid();var recipe=new LoftRecipe(stations.Select(s=>s.Plane).ToImmutableArray(),[new(channel,null,stations.Select((s,i)=>SketchProfileBuilder.Mixed(s,[ids[i]])).ToImmutableArray())
            {Sources=stations.Select((s,i)=>SketchProfileReference.CreateMixed(s,[ids[i]])).Cast<SketchProfileReference?>().ToImmutableArray()}],Ruled:true);
        await f.Session.ExecuteAsync(new AddBodyCommand(recipe,"Associated loft",f.Part));var before=f.Session.Snapshot;var feature=before.Features.Values.Single();
        var parameter=new CadParameter(Guid.NewGuid(),"top",ParameterDimension.Length,"0.1 mm");await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.SketchCurve(stations[1].Id,ids[1],"OffsetDistance",0))])));
        var after=f.Session.Snapshot;double expected=6*Math.PI*(.55*.55+.55*.9+.9*.9)/3;Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3-expected),0,1e-7);
        var current=Assert.IsType<LoftRecipe>(after.Features[feature.Id].Recipe);Assert.Equal(channel,current.Channels.Single().Id);Assert.Equal(stations.Select(s=>(SketchId?)s.Id),current.Channels[0].Sources.Select(s=>s?.SketchId));
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        await f.Session.SaveAsync(new CadDocumentStorage(),files.PathFor("loft.cadoryx"));using var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("loft.cadoryx"),f.Assets);loaded.Snapshot.Validate();
    }
    private static double Integral(Func<double,double> f)
    {
        const int n=8192;double sum=f(0)+f(1);
        for(int i=1;i<n;i++)sum+=(i%2==0?2:4)*f((double)i/n);
        return sum/(3*n);
    }
    // Independent formula for (7.5+u, 2u(1-u)) plus .03 times its right unit normal.
    private static (Point2d P,Point2d D) QuadraticOffset(double u)
    {
        double v=2-4*u,s=Math.Sqrt(1+v*v),s3=s*s*s;
        return(new(7.5+u+.03*v/s,2*u*(1-u)-.03/s),new(1-.12/s3,v-.12*v/s3));
    }
    public static IEnumerable<object[]> StraightSweeps()=>from reverse in new[]{false,true} from tilted in new[]{false,true}
        from output in new[]{GuidedOutput.Solid,GuidedOutput.Sheet} select new object[]{reverse,tilted,output};
    [Theory][MemberData(nameof(StraightSweeps))]
    public async Task ExactOffsetStraightSweepMatchesIndependentAreaAndPerimeterInBothWorldDirections(bool reverse,bool tilted,GuidedOutput output)
    {
        var e=SketchCurveExpressionGeometry.FromBoundary(new(new(7.5,0),new(8.5,0)){BezierControl=new(8,1)}).Offset(.03);
        if(reverse)e=e.Reverse();var profile=new SketchProfile([]){BoundaryCurves=[new(e.At(0),e.At(1)){Expression=e},new(e.At(1),e.At(0))]};
        var line=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(0,10))).Offset(.1).Trim(.1,.9);if(reverse)line=line.Reverse();
        var world=tilted?new RigidTransform3d(new(11,-7,3),Quaterniond.FromEulerDegrees(23,41,17)):RigidTransform3d.Identity;
        var start=line.At(0);var sectionFrame=world*new RigidTransform3d(PathPlane.Apply(new(start.X,start.Y,0)),reverse?Quaterniond.FromEulerDegrees(180,0,0):Quaterniond.Identity);
        var recipe=new SweepRecipe(new(profile,sectionFrame),new([new(line.At(0),line.At(1)){Expression=line}],world*PathPlane),output);
        var a=QuadraticOffset(0).P;var b=QuadraticOffset(1).P;
        double area=Math.Abs(Integral(u=>{var(p,d)=QuadraticOffset(u);return p.X*d.Y-p.Y*d.X;})+b.X*a.Y-b.Y*a.X)/2;
        double perimeter=Integral(u=>{var d=QuadraticOffset(u).D;return double.Hypot(d.X,d.Y);})+double.Hypot(a.X-b.X,a.Y-b.Y);
        var assets=new MemoryAssetStore();using(var result=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
        {
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);
            Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            Assert.Equal(output==GuidedOutput.Solid?BodyKind.Solid:BodyKind.Sheet,result.Geometry.Kind);
            if(output==GuidedOutput.Solid)Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-8*area),0,1e-7);
            else{Assert.Equal(0,result.Geometry.VolumeMm3);Assert.InRange(Math.Abs(shape.InspectProperties(InspectionPropertyKind.Area).Mass-8*perimeter),0,1e-7);}
            Assert.True(e.SameValues(recipe.Section.Profile.BoundaryCurves[0].Expression!));Assert.True(line.SameValues(recipe.Path.Curves[0].Expression!));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(GuidedOutput.Solid)][InlineData(GuidedOutput.Sheet)]
    public async Task PermittedSectionObliquenessUsesActualPathDisplacement(GuidedOutput output)
    {
        const double angle=.0002;var sectionFrame=new RigidTransform3d(Vector3d.Zero,Quaterniond.FromAxisAngle(new(0,1,0),angle));
        var recipe=new SweepRecipe(new(SketchProfile.FromCircle(new(0,0),2),sectionFrame),new([new(new(0,0),new(0,8))],PathPlane),output);
        var assets=new MemoryAssetStore();using(var result=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
        {
            Assert.InRange(Math.Abs(result.Geometry.Bounds.Min.X+2*Math.Cos(angle)),0,1e-6);
            Assert.InRange(Math.Abs(result.Geometry.Bounds.Max.X-2*Math.Cos(angle)),0,1e-6);
            if(output==GuidedOutput.Solid)Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-32*Math.PI*Math.Cos(angle)),0,1e-7);
            else
            {
                using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);
                double expected=16*2*Math.PI*Integral(u=>Math.Sqrt(Math.Cos(angle)*Math.Cos(angle)*Math.Sin(2*Math.PI*u)*Math.Sin(2*Math.PI*u)+Math.Cos(2*Math.PI*u)*Math.Cos(2*Math.PI*u)));
                Assert.InRange(Math.Abs(shape.InspectProperties(InspectionPropertyKind.Area).Mass-expected),0,1e-6);
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task StraightBasicAndExpressionPathsRetainNestedRegionContainment(bool expression,bool reverse)
    {
        var path=new SketchBoundaryCurve(reverse?new(0,10):new(0,0),reverse?new(0,0):new(0,10));
        if(expression){var e=SketchCurveExpressionGeometry.FromBoundary(path).Offset(reverse?-.1:.1);path=new(e.At(0),e.At(1)){Expression=e};}
        var start=path.Start;var frame=new RigidTransform3d(PathPlane.Apply(new(start.X,start.Y,0)),reverse?Quaterniond.FromEulerDegrees(180,0,0):Quaterniond.Identity);
        var profile=SketchProfile.FromCircle(new(0,0),3) with{Holes=[new(new(0,0),2)],Islands=[new([],new(new(0,0),1))]};
        var recipe=new SweepRecipe(new(profile,frame),new([path],PathPlane));var assets=new MemoryAssetStore();
        using(var result=await new OcctGeometryKernel().EvaluateAsync(recipe,assets)){Assert.Equal(BodyKind.Compound,result.Geometry.Kind);Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-60*Math.PI),0,1e-7);}
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("origin")][InlineData("normal")][InlineData("outside-hole")]
    public async Task ProvenStraightPathsStillRejectBadPoseAndRegionAtomically(string fault)
    {
        var line=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(0,10))).Offset(.1);
        var profile=SketchProfile.FromCircle(new(0,0),1);var frame=RigidTransform3d.Translate(.1,0,0);
        if(fault=="origin")frame=RigidTransform3d.Translate(.11,0,0);
        if(fault=="normal")frame=new(frame.Translation,Quaterniond.FromEulerDegrees(0,90,0));
        if(fault=="outside-hole")profile=profile with{Holes=[new(new(5,0),.2)]};
        var recipe=new SweepRecipe(new(profile,frame),new([new(line.At(0),line.At(1)){Expression=line}],PathPlane));var assets=new MemoryAssetStore();
        await Assert.ThrowsAsync<CadValidationException>(()=>new OcctGeometryKernel().EvaluateAsync(recipe,assets));Assert.Equal(0,assets.Count);
    }
    public static IEnumerable<object[]> ParallelLofts()=>from reverse in new[]{false,true} from placement in Enumerable.Range(0,3)
        from output in new[]{GuidedOutput.Solid,GuidedOutput.Sheet} select new object[]{reverse,placement,output};
    [Theory][MemberData(nameof(ParallelLofts))]
    public async Task CongruentRuledExactOffsetSectionsRetainIndependentVolumeAndSurfaceArea(bool reverse,int placement,GuidedOutput output)
    {
        var e=SketchCurveExpressionGeometry.FromBoundary(new(new(7.5,0),new(8.5,0)){BezierControl=new(8,1)}).Offset(.03);if(reverse)e=e.Reverse();
        var profile=new SketchProfile([]){BoundaryCurves=[new(e.At(0),e.At(1)){Expression=e},new(e.At(1),e.At(0))]};
        var delta=placement==2?new Vector3d(1,.4,5):new(0,0,5);var world=placement==1?new RigidTransform3d(new(11,-7,3),Quaterniond.FromEulerDegrees(23,41,17)):RigidTransform3d.Identity;
        var recipe=new LoftRecipe([world,world*new RigidTransform3d(delta,Quaterniond.Identity)],[new(Guid.NewGuid(),null,[profile,profile])],output,Ruled:true);
        var a=QuadraticOffset(0).P;var b=QuadraticOffset(1).P;
        double area=Math.Abs(Integral(u=>{var(p,d)=QuadraticOffset(u);return p.X*d.Y-p.Y*d.X;})+b.X*a.Y-b.Y*a.X)/2;
        double side=Integral(u=>{var d=QuadraticOffset(u).D;return new Vector3d(d.X,d.Y,0).Cross(delta).Length;})+new Vector3d(a.X-b.X,a.Y-b.Y,0).Cross(delta).Length;
        var assets=new MemoryAssetStore();using(var result=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
        {
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            if(output==GuidedOutput.Solid)Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-5*area),0,1e-7);
            else{Assert.Equal(BodyKind.Sheet,result.Geometry.Kind);Assert.InRange(Math.Abs(shape.InspectProperties(InspectionPropertyKind.Area).Mass-side),0,1e-7);}
            var edges=shape.GetSubShapes(ShapeKind.Edge);try{Assert.Contains(edges,c=>c.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.OffsetCurve);}finally{foreach(var edge in edges)edge.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public OcctGeometryKernel Kernel {get;}=new();public CadDocumentSession Session {get;}public CadDocumentViewModel Document {get;}public DefinitionId Part {get;}
        public Fixture(){var(initial,part)=SketchCommandTests.Document();Part=part;Session=new(initial,Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        public Task Store(CadSketch sketch)=>Session.ExecuteAsync(new UpsertSketchCommand(sketch,new ManagedSketchConstraintSolver()));
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
