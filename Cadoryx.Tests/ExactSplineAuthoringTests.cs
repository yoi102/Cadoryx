using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExactSplineAuthoringTests
{
    private static CubicSplineDefinition Curve()=>new([new(8,0),new(20,2),new(16,4),new(18,6),new(16,9)],[0,.2,1],[4,1,4]);
    private static void Near(Point2d a,Point2d b)=>Assert.InRange(double.Hypot(a.X-b.X,a.Y-b.Y),0,1e-7);
    private static SketchDraft Draft(DefinitionId part)
    {var d=new SketchDraft(CadSketch.Create(part,"Exact author",RigidTransform3d.Identity));d.AddSpline(Curve());return d;}

    [Fact] public async Task NonuniformAuthorGeometryBuildsRecomputesAndReopensWithoutLosingKnots()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var solver=new ManagedSketchConstraintSolver();
        var d=Draft(part);var id=d.Value.Splines[0].Id;
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
        var source=SketchProfileReference.CreateSplineSegment(sketch,id);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,RigidTransform3d.Identity));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Exact volume",part,sketchSource:source));
        var feature=session.Snapshot.Features.Values.Single();Assert.InRange(Math.Abs(feature.Result.VolumeMm3-120.624),0,1e-6);
        var before=session.Snapshot;var edited=new SketchDraft(sketch);edited.SetSplineKnots(id,new([0,.6,1],[4,1,4]));
        Assert.Equal(sketch.Splines[0].Controls,edited.Value.Splines[0].Controls);
        await session.ExecuteAsync(new UpsertSketchCommand(edited.Value,solver));var after=session.Snapshot;
        Assert.NotEqual(feature.Result.VolumeMm3,after.Features[feature.Id].Result.VolumeMm3);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        using var files=new TestFiles();string path=files.PathFor("exact.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();
        Assert.Equal(new[]{0d,.6,1},loaded.Snapshot.Sketches[sketch.Id].Splines[0].Knots!.Knots);
        var restored=(ExtrudeRecipe)loaded.Snapshot.Features[feature.Id].Recipe;
        Assert.True(CubicSplineRegion.Same(restored.Profile.Spline,((ExtrudeRecipe)after.Features[feature.Id].Recipe).Profile.Spline));
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind switch
        {"features"=>s with{SchemaVersion=31},"sketches"=>s with{SchemaVersion=11},_=>s}).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
    }

    [Fact] public void KnotChangesParticipateInProfileAndReversedBoundaryCacheIdentity()
    {
        var a=new CubicSplineRegion(Curve().Controls){Knots=CubicSplineKnotVector.From(Curve())};
        var b=a with{Knots=new([0,.6,1],[4,1,4])};
        Assert.False(SketchProfileReference.SameProfile(new([]){Spline=a},new([]){Spline=b}));
        var edge=new SketchBoundaryCurve(a.Controls[0],a.Controls[^1]){SplineControls=a.Controls,SplineKnots=a.Knots};
        var reversed=edge.Reversed();Assert.False(SketchMixedProfile.Same(edge,edge with{SplineKnots=b.Knots}));
        foreach(double t in new[]{0d,.1,.2,.55,1})Near(edge.SplineDefinition.Evaluate(t).Point,reversed.SplineDefinition.Evaluate(1-t).Point);
    }

    [Fact] public void SingleIntervalPreservesCurveIdAndSplitInvalidatesItWithUndoRedo()
    {
        var (_,part)=SketchCommandTests.Document();var d=Draft(part);var original=d.Value;var spline=original.Splines[0];
        d.TrimSpline(spline.Id,.2,.8);Assert.Equal(spline.Id,d.Value.Splines[0].Id);
        var trimmed=SketchExactSplineEditing.Definition(d.Value,spline.Id);
        foreach(double t in new[]{0d,.3,.8,1})Near(Curve().Evaluate(.2+.6*t).Point,trimmed.Evaluate(t).Point);
        d.Undo();Assert.Same(original,d.Value);d.RemoveSplineInterval(spline.Id,.2,.8);
        var split=d.Value;Assert.Equal(2,split.Splines.Length);Assert.DoesNotContain(split.Splines,c=>c.Id==spline.Id);
        split.Validate();d.Undo();Assert.Same(original,d.Value);d.Redo();Assert.Same(split,d.Value);
        var before=d.Value;Assert.Throws<CadValidationException>(()=>d.TrimSpline(split.Splines[0].Id,.8,.2));Assert.Same(before,d.Value);
    }

    [Fact] public void PointTrimUsesExactIntersectionsAndCoincidentBoundariesRejectAtomically()
    {
        var (_,part)=SketchCommandTests.Document();var d=Draft(part);var spline=d.Value.Splines[0];
        d.AddLine(new(0,4),new(30,4),construction:true);var before=d.Value;
        d.TrimSplineAt(spline.Id,Curve().Evaluate(.1).Point,new OcctGeometryKernel());
        var result=SketchExactSplineEditing.Definition(d.Value,spline.Id);Assert.InRange(Math.Abs(result.Controls[0].Y-4),0,1e-7);
        d.Undo();Assert.Same(before,d.Value);d.AddSpline(Curve());before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.TrimSplineAt(spline.Id,Curve().Evaluate(.1).Point,new OcctGeometryKernel()));Assert.Same(before,d.Value);
    }

    [Fact] public void PointExtensionContinuesTheCubicPolynomialToAnExactFiniteBoundary()
    {
        var (_,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Continue",RigidTransform3d.Identity));
        var definition=new CubicSplineDefinition([new(0,0),new(1,1),new(2,1),new(3,0)],[0,1],[4,4]);d.AddSpline(definition);
        var id=d.Value.Splines[0].Id;d.AddLine(new(3.6,-5),new(3.6,5),construction:true);
        d.ExtendSplineAt(id,definition.Evaluate(.9).Point,new OcctGeometryKernel());
        var extended=SketchExactSplineEditing.Definition(d.Value,id);Near(new(3.6,-.72),extended.Controls[^1]);
        foreach(double t in new[]{0d,.2,.7,1})Near(definition.Evaluate(t).Point,extended.Evaluate(t/1.2).Point);
    }

    [Fact] public async Task SelfCrossingCurveCanBeAuthoredButCannotReplaceAValidSolid()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var solver=new ManagedSketchConstraintSolver();
        var d=new SketchDraft(CadSketch.Create(part,"Crossing diagnostics",RigidTransform3d.Identity));
        d.AddSpline(new CubicSplineDefinition([new(0,0),new(6,9),new(-6,9),new(2,0)],[0,1],[4,4]));d.Value.Validate();
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var before=session.Snapshot;
        var source=SketchProfileReference.CreateSplineSegment(before.Sketches[d.Value.Id],d.Value.Splines[0].Id);
        var recipe=source.Resolve(before,part,new ExtrudeRecipe(new([]),3,RigidTransform3d.Identity));
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new AddBodyCommand(recipe,"Invalid region",part,sketchSource:source)));
        Assert.Same(before,session.Snapshot);Assert.Empty(session.Snapshot.Features);
    }

    [Fact] public async Task PreviousDeclaredSchemasWithoutNewFieldsStillMigrate()
    {
        var (document,_)=SketchCommandTests.Document();var assets=new MemoryAssetStore();using var files=new TestFiles();
        string path=files.PathFor("legacy.cadoryx");await new CadDocumentStorage().SaveAsync(document,assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind switch
        {"document"=>s with{SchemaVersion=33},"features"=>s with{SchemaVersion=31},"sketches"=>s with{SchemaVersion=11},_=>s}).ToImmutableArray()});
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();Assert.Equal(document.StateId,loaded.Snapshot.StateId);
    }
}
