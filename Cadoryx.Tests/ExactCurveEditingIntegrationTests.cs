using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExactCurveEditingIntegrationTests
{
    private static CubicSplineDefinition Curve()=>new([new(8,0),new(20,2),new(16,4),new(18,6),new(16,9)],[0,.2,1],[4,1,4]);
    private static void Near(Point2d a,Point2d b)=>Assert.InRange(double.Hypot(a.X-b.X,a.Y-b.Y),0,1e-7);
    private static Point2d At(QuadraticBezierRegion b,double t)=>SketchBezierGeometry.At(b.Start,b.Control,b.End,t);

    [Theory][InlineData(false)][InlineData(true)]
    public async Task AdvancedExtrusionCanReselectSplitCurvesWhileMainToolAndPartAreDifferent(bool spline)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Split author",RigidTransform3d.Identity));
        if(spline)d.AddSpline(Curve());else d.AddBezier(new(8,0),new(10,3),new(12,0));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
        var id=spline?sketch.Splines[0].Id:sketch.Beziers[0].Id;
        var source=spline?SketchProfileReference.CreateSplineSegment(sketch,id):SketchProfileReference.CreateBezierSegment(sketch,id);
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Linked",part,sketchSource:source));
        var feature=session.Snapshot.Features.Values.Single();
        d=new SketchDraft(sketch);
        if(spline)d.RemoveSplineInterval(id,.2,.8);else d.RemoveBezierInterval(id,.2,.8);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var frozen=session.Snapshot;
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.ToolKind="Box";document.SelectedTargetPart=null;Assert.DoesNotContain(document.SketchProfiles,p=>p.Source.SplineId is not null||p.Source.BezierId is not null);
            await using var editor=new ExtrusionEditorViewModel(document,kernel,feature.Id);
            Assert.True(editor.CanChooseProfile);
            var choices=editor.Profiles.Where(p=>p.Source?.SketchId==sketch.Id&&(spline?p.Source.SplineId:p.Source.BezierId)!=id).ToArray();Assert.Equal(2,choices.Length);
            editor.SelectedProfile=choices[0];await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(frozen,session.Snapshot);
            await editor.ConfirmCommand.ExecuteAsync(null);Assert.False(session.Snapshot.Features[feature.Id].IsStale);
            await session.UndoAsync();Assert.Same(frozen,session.Snapshot);
        }
        finally{document.Detach();}
    }

    [Fact] public void ClosedSplineAndEqualNearestPointerSelectionRejectWithoutChangingTheDraft()
    {
        var (_,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Closed author",RigidTransform3d.Identity));
        var closed=new CubicSplineDefinition([new(0,0),new(3,0),new(3,3),new(0,0)],[0,1],[4,4]);d.AddSpline(closed);var id=d.Value.Splines[0].Id;
        d.AddLine(new(-1,.5),new(4,.5),construction:true);var before=d.Value;
        var error=Assert.Throws<CadValidationException>(()=>d.TrimSplineAt(id,new(0,0),new OcctGeometryKernel()));
        Assert.Equal("CurveAmbiguousProjection",error.Data[SketchCurveFailure.ResourceKey]);Assert.Same(before,d.Value);
        d.TrimSpline(id,.1,.8);foreach(double t in new[]{0d,.25,.7,1})Near(closed.Evaluate(.1+.7*t).Point,SketchExactSplineEditing.Definition(d.Value,id).Evaluate(t).Point);
        d.Undo();Assert.Same(before,d.Value);
        d.AddBezier(new(-2,0),new(0,4),new(2,0));var bezier=d.Value.Beziers[0].Id;before=d.Value;
        error=Assert.Throws<CadValidationException>(()=>d.TrimBezierAt(bezier,new(0,-10),new OcctGeometryKernel()));
        Assert.Equal("CurveAmbiguousProjection",error.Data[SketchCurveFailure.ResourceKey]);Assert.Same(before,d.Value);
    }

    [Fact] public void PositiveEndpointRadiiCannotHideAnExactInteriorAxisTouch()
    {
        // Degree elevation of x=(2t-1)^2, y=3t: both endpoints are positive, x(.5)=0.
        var c=new CubicSplineDefinition([new(1,0),new(-1d/3,1),new(-1d/3,2),new(1,3)],[0,1],[4,4]);
        Assert.InRange(Math.Abs(c.Evaluate(.5).Point.X),0,1e-14);
        var profile=new SketchProfile([]){Spline=new(c.Controls){Knots=CubicSplineKnotVector.From(c)}};
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(profile,Math.PI,RigidTransform3d.Identity).Validate());
    }

    [Fact] public async Task RecursiveNonuniformProfileReplayRoundtripsAndRebuildsTheSameMaterial()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Exact replay"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(80,30,5,RigidTransform3d.Identity),"Plate"));var plate=session.Snapshot.Bodies.Values.Single();
        CubicSplineRegion Arch(double x,double y,double scale)=>new([new(x,y),new(x,y+2*scale),new(x+2*scale,y+2*scale),new(x+4*scale,y+2*scale),new(x+4*scale,y)]){Knots=new([0,.2,1],[4,1,4])};
        var profile=new SketchProfile([new(0,0),new(20,0),new(20,15),new(0,15)])
        {SplineHoles=[Arch(2,2,4)],Islands=[new([],null){Spline=Arch(9,4,.5),Holes=[new(new(10,4.2),.05)],Islands=[new([],new(new(10,4.2),.02))]}]};
        await session.ExecuteAsync(new AddExtrusionCommand(new(profile,2,RigidTransform3d.Translate(1,1,5),new(ExtrusionSide.Negative,Operation:ExtrusionOperation.Cut,Source:plate.Geometry)),"Seed",targetBody:plate.Id));
        var seed=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,2,25,[Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
        await session.ExecuteAsync(new AddExtrusionPatternCommand(seed.OutputBodyId,new(seed.Id,(ExtrudeRecipe)seed.Recipe,layout),"Exact replay"));
        var replay=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrusionPatternRecipe);
        using var files=new TestFiles();var path=files.PathFor("recursive-replay.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();
        var recipe=(ExtrusionPatternRecipe)loaded.Snapshot.Features[replay.Id].Recipe;
        Assert.True(SketchProfileReference.SameProfile(profile,recipe.Template.Profile));Assert.Equal(new[]{0d,.2,1},recipe.Template.Profile.Islands[0].Spline!.Knots!.Knots);
        Assert.InRange(Math.Abs(replay.Result.VolumeMm3-(12000-2*(12000-seed.Result.VolumeMm3))),0,1e-5);
        using var rebuilt=await kernel.EvaluateAsync(recipe,assets);
        using var first=OcctGeometryBridge.ReadShape(replay.Result,assets);using var second=OcctGeometryBridge.ReadShape(rebuilt.Geometry,assets);
        using var extra=first.Cut(second);using var missing=second.Cut(first);
        Assert.Equal(0,extra.GetTopologySummary().UniqueCounts.SolidCount);Assert.Equal(0,missing.GetTopologySummary().UniqueCounts.SolidCount);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void BezierKeepsExactQuadraticAndEndpointIdentityThroughTrimExtendAndSplit(bool start)
    {
        var (_,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Bezier",RigidTransform3d.Identity));
        d.AddBezier(new(0,0),new(1.5,2),new(3,0));var b=d.Value.Beziers[0];var original=d.Value;
        var definition=SketchExactBezierEditing.Definition(original,b.Id);
        d.TrimBezier(b.Id,.2,.8);Assert.Equal(b.Id,d.Value.Beziers[0].Id);
        foreach(double t in new[]{0d,.3,.8,1})Near(At(definition,.2+.6*t),At(SketchExactBezierEditing.Definition(d.Value,b.Id),t));
        d.Undo();Assert.Same(original,d.Value);d.ExtendBezier(b.Id,start,.2);
        var extended=SketchExactBezierEditing.Definition(d.Value,b.Id);
        foreach(double t in new[]{0d,.3,.8,1})Near(At(definition,t),At(extended,(t+(start?.2:0))/1.2));
        Assert.Equal(start?b.End:b.Start,start?d.Value.Beziers[0].End:d.Value.Beziers[0].Start);
        d.Undo();d.RemoveBezierInterval(b.Id,.2,.8);Assert.Equal(2,d.Value.Beziers.Length);
        Assert.DoesNotContain(d.Value.Beziers,c=>c.Id==b.Id);var split=d.Value;split.Validate();
        d.Undo();Assert.Same(original,d.Value);d.Redo();Assert.Same(split,d.Value);
        Assert.Throws<CadValidationException>(()=>d.TrimBezier(split.Beziers[0].Id,.8,.2));Assert.Same(split,d.Value);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void BezierPointerEditingUsesFiniteNativeIntersectionsAndOriginalPolynomial(bool start)
    {
        var (_,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Finite",RigidTransform3d.Identity));
        d.AddBezier(new(0,0),new(1.5,2),new(3,0));var b=d.Value.Beziers[0];var definition=SketchExactBezierEditing.Definition(d.Value,b.Id);
        d.AddLine(new(start?-.6:3.6,-8),new(start?-.6:3.6,8),construction:true);
        d.ExtendBezierAt(b.Id,At(definition,start?.1:.9),new OcctGeometryKernel());
        var extended=SketchExactBezierEditing.Definition(d.Value,b.Id);Near(new(start?-.6:3.6,-.96),start?extended.Start:extended.End);
        d.Undo();d.AddLine(new(1,-5),new(1,5),construction:true);var before=d.Value;
        d.TrimBezierAt(b.Id,At(definition,.1),new OcctGeometryKernel());
        Near(At(definition,1d/3),SketchExactBezierEditing.Definition(d.Value,b.Id).Start);
        d.Undo();Assert.Same(before,d.Value);d.AddBezier(definition.Start,definition.Control,definition.End);before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.TrimBezierAt(b.Id,At(definition,.2),new OcctGeometryKernel()));Assert.Same(before,d.Value);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SplitFreezesLinkedBodyAndDownstreamUntilExplicitReselectionAndReopens(bool revolve)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));d.AddSpline(Curve());
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];var id=s.Splines[0].Id;
        var source=SketchProfileReference.CreateSplineSegment(s,id);
        GeometryRecipe seed=revolve?new RevolveRecipe(new([]),Math.PI,RigidTransform3d.Identity):new ExtrudeRecipe(new([]),3,RigidTransform3d.Identity);
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,seed),"Linked",part,sketchSource:source));
        var feature=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(40,0,0)),"Second",part));
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,session.Snapshot.Bodies.Keys));
        var downstream=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);var before=session.Snapshot;int count=assets.Count;
        var edit=new SketchDraft(s);edit.RemoveSplineInterval(id,.2,.8);await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));
        var split=session.Snapshot;split.Validate();Assert.Equal(count,assets.Count);
        Assert.True(split.Features[feature.Id].IsStale);Assert.True(split.Features[downstream.Id].IsStale);
        Assert.Null(split.Features[feature.Id].TopologyHistory);Assert.Null(split.Features[downstream.Id].TopologyHistory);
        Assert.Equal(feature.Result,split.Features[feature.Id].Result);Assert.Equal(source,split.Features[feature.Id].SketchSource);
        using var files=new TestFiles();var file=files.PathFor("split.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {loaded.Snapshot.Validate();Assert.True(loaded.Snapshot.Features[feature.Id].IsStale);Assert.Equal(id,loaded.Snapshot.Features[feature.Id].SketchSource!.SplineId);}
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(split,session.Snapshot);
        var current=split.Sketches[s.Id];var selected=SketchProfileReference.CreateSplineSegment(current,current.Splines[0].Id);
        var replacement=selected.Resolve(split,part,feature.Recipe);
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,replacement,selected));session.Snapshot.Validate();
        Assert.False(session.Snapshot.Features[feature.Id].IsStale);Assert.False(session.Snapshot.Features[downstream.Id].IsStale);
        Assert.NotEqual(feature.Result.VolumeMm3,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
        Assert.Equal(selected.SplineId,session.Snapshot.Features[feature.Id].SketchSource!.SplineId);
        await session.UndoAsync();Assert.Same(split,session.Snapshot);
    }

    [Theory][InlineData("section")][InlineData("path")][InlineData("loft")]
    public async Task GuidedSourcesSaveNonuniformKnotsAndFreezeRatherThanRebindSplit(string kind)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Guided source",kind=="path"?new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0)):RigidTransform3d.Identity));
        d.AddSpline(kind=="path"?new([new(0,0),new(0,2),new(0,4),new(0,7),new(0,10)],[0,.2,1],[4,1,4]):Curve());
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];var id=s.Splines[0].Id;
        GeometryRecipe recipe;
        if(kind=="path")
        {
            var path=new SketchPathReference(s.Id,s.Revision,[new(id)]).Resolve(session.Snapshot,part);
            recipe=new SweepRecipe(new(SketchProfile.FromCircle(new(0,0),.2),RigidTransform3d.Identity),path);
        }
        else
        {
            var source=SketchProfileReference.CreateSplineSegment(s,id);
            var section=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),1,RigidTransform3d.Identity));
            if(kind=="section")recipe=new SweepRecipe(new(section.Profile,section.Placement){Source=source},
                new([new(new(0,0),new(0,10))],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));
            else recipe=new LoftRecipe([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5)],
                [new(Guid.NewGuid(),null,[section.Profile,section.Profile]){Sources=[source,null]}],Ruled:true);
        }
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Guided",part));var feature=session.Snapshot.Features.Values.Single();
        double expected=kind=="path"?.4*Math.PI:40.208*(kind=="loft"?5:10);Assert.InRange(Math.Abs(feature.Result.VolumeMm3-expected),0,.00001);
        using var files=new TestFiles();string file=files.PathFor("guided.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();var restored=loaded.Snapshot.Features[feature.Id].Recipe;
            if(restored is SweepRecipe sweep)Assert.Equal(new[]{0d,.2,1},(kind=="path"?sweep.Path.Curves[0].SplineKnots:sweep.Section.Profile.Spline!.Knots)!.Knots);
            else Assert.Equal(new[]{0d,.2,1},((LoftRecipe)restored).Channels[0].Profiles[0].Spline!.Knots!.Knots);
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(c=>c.Kind=="features"?c with{SchemaVersion=31}:c).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
        var before=session.Snapshot;var edit=new SketchDraft(s);edit.RemoveSplineInterval(id,.2,.8);
        await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));session.Snapshot.Validate();
        Assert.True(session.Snapshot.Features[feature.Id].IsStale);Assert.Equal(feature.Result,session.Snapshot.Features[feature.Id].Result);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
    }

    [Theory][InlineData("outer")][InlineData("hole")]
    public async Task ForeignKnotCompanionsAreRejectedInsteadOfSilentlyDropped(string field)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Bad companion"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Identity),"Box"));
        using var files=new TestFiles();string file=files.PathFor("companion.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);int count=assets.Count;
        FormatEvolutionTests.RewriteSection(file,"features",bytes=>
        {
            var payload=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);var f=payload.Features[0];var r=f.Recipe;
            var knots=new PackSplineKnots([0,.2,1],[4,1,4]);
            payload.Features[0]=f with{Recipe=field=="outer"?r with{SplineControls=Curve().Controls.Select(p=>new[]{p.X,p.Y}).ToArray(),SplineKnots=knots}:
                r with{SplineHoles=[Curve().Controls.Select(p=>new[]{p.X,p.Y}).ToArray()],SplineHoleKnots=[knots]}};
            return MessagePackSerializer.Serialize(payload);
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));Assert.Equal(count,assets.Count);
    }

    [Fact] public async Task MixedBoundaryAndRecursiveSplineHoleIslandKnotsRoundtripAndRemainExact()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var c=Curve();var midpoint=new Point2d(12,4.5);
        var mixed=new SketchProfile([]){BoundaryCurves=[new(c.Controls[0],c.Controls[^1]){SplineControls=c.Controls,SplineKnots=CubicSplineKnotVector.From(c)},
            new(c.Controls[^1],midpoint),new(midpoint,c.Controls[0])]};
        await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(mixed,3,RigidTransform3d.Identity),"Mixed",part));
        Assert.InRange(Math.Abs(session.Snapshot.Features.Values.Single().Result.VolumeMm3-120.624),0,1e-6);
        CubicSplineRegion Arch(double x,double y,double scale)=>new([new(x,y),new(x,y+2*scale),new(x+2*scale,y+2*scale),new(x+4*scale,y+2*scale),new(x+4*scale,y)])
            {Knots=new([0,.2,1],[4,1,4])};
        var root=new SketchProfile([new(0,0),new(20,0),new(20,15),new(0,15)])
        {
            SplineHoles=[Arch(2,2,4)],Islands=[new([],null){Spline=Arch(9,4,.5),
                Holes=[new(new(10,4.2),.05)],Islands=[new([],new(new(10,4.2),.02))]}]
        };
        await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(root,2,RigidTransform3d.Identity),"Nested",part));
        using var files=new TestFiles();var file=files.PathFor("recursive.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();foreach(var feature in session.Snapshot.Features.Values)
            {
                var expected=(ExtrudeRecipe)feature.Recipe;var restored=(ExtrudeRecipe)loaded.Snapshot.Features[feature.Id].Recipe;
                Assert.True(SketchProfileReference.SameProfile(expected.Profile,restored.Profile));
                using var rebuilt=await kernel.EvaluateAsync(restored,assets);Assert.InRange(Math.Abs(rebuilt.Geometry.VolumeMm3-feature.Result.VolumeMm3),0,1e-6);
            }
            var nested=(ExtrudeRecipe)loaded.Snapshot.Features.Values.Single(f=>f.Name=="Nested").Recipe;
            Assert.Equal(new[]{0d,.2,1},nested.Profile.SplineHoles[0].Knots!.Knots);
            Assert.Equal(new[]{0d,.2,1},nested.Profile.Islands[0].Spline!.Knots!.Knots);Assert.Single(nested.Profile.Islands[0].Islands);
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(c=>c.Kind=="features"?c with{SchemaVersion=31}:c).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
    }

    [Theory][InlineData(17)][InlineData(1024)]
    public async Task LargeUnconstrainedAuthorCurveSavesWithoutDenseSolvingWhileLegacyMasqueradeRejects(int controlCount)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Large author",RigidTransform3d.Identity));
        d.AddSpline(Enumerable.Range(0,controlCount).Select(i=>new Point2d(i,Math.Sin(i*Math.PI/(controlCount-1)))));
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.Equal(2*controlCount,report.DegreesOfFreedom);Assert.Equal(0,report.Iterations);Assert.Equal(0,report.EquationCount);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
        using var files=new TestFiles();var file=files.PathFor("17.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {Assert.Equal(controlCount,loaded.Snapshot.Sketches[d.Value.Id].Splines[0].Controls.Length);Assert.NotNull(loaded.Snapshot.Sketches[d.Value.Id].Splines[0].Knots);}
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(c=>c.Kind=="sketches"?c with{SchemaVersion=11}:c).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
        var before=d.Value;Assert.Throws<CadValidationException>(()=>d.AddSpline(Enumerable.Range(0,1025).Select(i=>new Point2d(i,1))));Assert.Same(before,d.Value);
    }

    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public void RepeatedKnotExtremaRejectAnInteriorAxisCrossing(int multiplicity)
    {
        // Exercise every permitted interior knot multiplicity.
        var controls=Enumerable.Range(0,multiplicity+4).Select(i=>new Point2d(1,i)).ToImmutableArray();
        var curve=new CubicSplineDefinition(controls,[0,.2,1],[4,multiplicity,4]);
        var positive=new SketchProfile([]){Spline=new(controls){Knots=CubicSplineKnotVector.From(curve)}};
        new RevolveRecipe(positive,Math.PI,RigidTransform3d.Identity).Validate();
        // All endpoint radii are positive; only an internal extremum can prove this crossing.
        var crossing=curve with{Controls=controls.Select((p,i)=>new Point2d(i==0||i==controls.Length-1?1:-4,p.Y)).ToImmutableArray()};
        var profile=new SketchProfile([]){Spline=new(crossing.Controls){Knots=CubicSplineKnotVector.From(crossing)}};
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(profile,Math.PI,RigidTransform3d.Identity).Validate());
    }

    [Fact] public async Task NonuniformG1CurveParametersDatumBooleanDrawingAndStepStayAssociated()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Spin",DatumAxisKind.Independent,Direction:Vector3d.UnitZ);
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=session.Snapshot.Datums.Axes[axis.Id];
        var d=new SketchDraft(CadSketch.Create(part,"Nonuniform G1",RigidTransform3d.Identity));
        d.AddLine(new(2,-1),new(8,0),construction:true);var line=d.Value.Lines[0];d.AddSpline(Curve(),existingStart:line.End);var spline=d.Value.Splines[0];
        d.Change(s=>s with{Constraints=[..s.Points.Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),
            new EndpointTangentConstraint(SketchConstraintId.New(),line.Id,spline.Id,line.End)]});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
        var source=SketchProfileReference.CreateSplineSegment(sketch,spline.Id);
        var seed=new RevolveRecipe(new([]),Math.PI,RigidTransform3d.Identity){Axis=new(axis.Frame.Translation,Vector3d.UnitZ,axis.Id,axis.Revision)};
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,seed),"Spin",part,sketchSource:source));var spin=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(50,50,30,RigidTransform3d.Translate(-25,-25,-5)),"Stock",part));
        var stock=session.Snapshot.Features.Values.Single(f=>f.Id!=spin.Id);
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[stock.OutputBodyId,spin.OutputBodyId]));var cut=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
        var sheet=TechnicalDrawingSheet.A4Landscape("Exact author");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
        var before=session.Snapshot;var offset=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,"3 mm");var angle=new CadParameter(Guid.NewGuid(),"turn",ParameterDimension.Angle,"90 deg");
        var book=new ParameterBook([offset,angle],[new(Guid.NewGuid(),offset.Id,ParameterTarget.Datum(axis.Id,"X")),new(Guid.NewGuid(),angle.Id,ParameterTarget.Feature(spin.Id,"Angle"))]);
        await session.ExecuteAsync(new SetParametersCommand(book));var after=session.Snapshot;
        double volume=Math.PI/2*RegionRadialMoment(Curve(),3);
        Assert.InRange(Math.Abs(after.Features[spin.Id].Result.VolumeMm3-volume),0,1e-5);
        Assert.InRange(Math.Abs(after.Features[cut.Id].Result.VolumeMm3-(75000-volume)),0,1e-5);
        Assert.All(after.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.Equal(after.Features[cut.Id].Result.Revision,v.Sources.Single().Revision);});
        var tangency=after.Sketches[sketch.Id].Constraints.OfType<EndpointTangentConstraint>().Single();Assert.Equal(line.End,tangency.Joint);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        using var files=new TestFiles();string step=files.PathFor("exact.step");await kernel.ExportAsync(after,assets,step);
        using(var imported=await kernel.ImportAsync(step,assets))Assert.InRange(Math.Abs(imported.Snapshot.Bodies.Values.Sum(b=>b.Geometry.VolumeMm3)-(75000-volume)),0,.0001);
        string saved=files.PathFor("associated.cadoryx");await session.SaveAsync(new CadDocumentStorage(datumResolver:kernel),saved);
        using var loaded=await new CadDocumentStorage(datumResolver:kernel).LoadAsync(saved,assets);loaded.Snapshot.Validate();
        Assert.Equal(new[]{0d,.2,1},loaded.Snapshot.Sketches[sketch.Id].Splines[0].Knots!.Knots);
        var bad=book with{Parameters=book.Parameters.Select(p=>p.Id==offset.Id?p with{Expression="12 mm"}:p).ToImmutableArray()};int count=assets.Count;
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new SetParametersCommand(bad)));Assert.Same(after,session.Snapshot);Assert.Equal(count,assets.Count);
    }
    private static double RegionRadialMoment(CubicSplineDefinition curve,double axis)
    {
        // Five-point Gauss is exact for x(t)^2*y'(t), degree eight on each cubic span.
        double[] nodes=[-.906179845938664,-.538469310105683,0,.538469310105683,.906179845938664];
        double[] weights=[.236926885056189,.478628670499366,.568888888888889,.478628670499366,.236926885056189];
        double Integral(double first,double last,Func<double,double> value)=>nodes.Select((n,i)=>weights[i]*value((first+last)/2+(last-first)*n/2)).Sum()*(last-first)/2;
        double sum=0,width=curve.Knots[^1]-curve.Knots[0];
        for(int i=0;i<curve.Knots.Length-1;i++)sum+=Integral((curve.Knots[i]-curve.Knots[0])/width,(curve.Knots[i+1]-curve.Knots[0])/width,
            t=>{var e=curve.Evaluate(t);return Math.Pow(e.Point.X-axis,2)*e.Derivative.Y/2;});
        var a=curve.Controls[^1];var b=curve.Controls[0];sum+=Integral(0,1,t=>Math.Pow(a.X+(b.X-a.X)*t-axis,2)*(b.Y-a.Y)/2);
        return Math.Abs(sum);
    }
}
