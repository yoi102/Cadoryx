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
using Xunit;

namespace Cadoryx.Tests;

public sealed class M9SketchRegionTests
{
    private static readonly ImmutableArray<Point2d> Arch=
        [new(0,0),new(2,4),new(5,6),new(8,4),new(10,0)];

    [Fact]
    public async Task SplineHoleIsExactLinkedAndSurvivesRoundTrip()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Spline hole",RigidTransform3d.Identity));
        draft.AddRectangle(new(-5,-5),new(20,15));draft.AddSpline(Arch);
        var outer=Assert.Single(SketchLoops.Find(draft.Value));var spline=Assert.Single(draft.Value.Splines);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];
        var source=SketchProfileReference.Create(sketch,outer) with{HoleSplineIds=[spline.Id]};
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),5,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Spline hole extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-2331.5),0,.1);
        var path=files.PathFor("spline-hole.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        loaded.Snapshot.Validate();
        Assert.Equal(spline.Id,Assert.Single(loaded.Snapshot.Features[feature.Id].SketchSource!.HoleSplineIds));
        Assert.InRange(Math.Abs(loaded.Snapshot.Features[feature.Id].Result.VolumeMm3-feature.Result.VolumeMm3),0,1e-9);
        foreach(var ext in new[]{"step","iges","stl"})
        {
            var export=files.PathFor("spline-hole."+ext);await kernel.ExportAsync(loaded.Snapshot,assets,export);
            Assert.True(new FileInfo(export).Length>0);
        }
    }

    [Fact]
    public async Task MixedSplineAndLinesCreateExactAssociativeExtrusion()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Mixed spline",RigidTransform3d.Identity));
        draft.AddSpline(Arch);var spline=Assert.Single(draft.Value.Splines);
        draft.AddLine(new(10,0),new(10,-4),spline.Controls[^1]);var right=draft.Value.Lines[^1];
        draft.AddLine(new(10,-4),new(0,-4),right.End);var bottom=draft.Value.Lines[^1];
        draft.AddLine(new(0,-4),new(0,0),bottom.End,spline.Controls[0]);
        var loop=Assert.Single(SketchMixedLoops.Find(draft.Value));
        Assert.Contains(spline.Id,loop);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];
        var source=SketchProfileReference.CreateMixed(sketch,loop);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Mixed spline extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-221.1),0,.1);
        var file=files.PathFor("mixed-spline.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using var loaded=await new CadDocumentStorage().LoadAsync(file,assets);
        loaded.Snapshot.Validate();
        Assert.Equal(loop.Length,loaded.Snapshot.Features[feature.Id].SketchSource!.MixedBoundaryIds.Length);
    }

    [Fact]
    public async Task NestedHoleIslandAndIslandHoleRecomputeAndRoundTrip()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Nested",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(20,20));
        draft.AddCircle(new(10,10),6);var outerHole=draft.Value.Circles[^1];
        draft.AddCircle(new(10,10),3);var island=draft.Value.Circles[^1];
        draft.AddCircle(new(10,10),1);var islandHole=draft.Value.Circles[^1];
        var outer=Assert.Single(SketchLoops.Find(draft.Value));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];
        var regions=ImmutableArray.Create(new SketchRegionReference(CircleId:outerHole.Id,Children:
            [new SketchRegionReference(CircleId:island.Id,Children:[new SketchRegionReference(CircleId:islandHole.Id,Children:[])])]));
        var source=SketchProfileReference.Create(sketch,outer) with{Regions=regions};
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Nested extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-2*(400-28*Math.PI)),0,.05);
        var file=files.PathFor("nested.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using var loaded=await new CadDocumentStorage().LoadAsync(file,assets);
        loaded.Snapshot.Validate();
        var restored=loaded.Snapshot.Features[feature.Id];
        Assert.Equal(islandHole.Id,restored.SketchSource!.Regions.Single().Children.Single().Children.Single().CircleId);
        Assert.InRange(Math.Abs(restored.Result.VolumeMm3-feature.Result.VolumeMm3),0,1e-9);
        var editor=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            editor.SelectedTargetPart=part;editor.SelectedSketchId=sketch.Id;editor.StartSketchFeature("Extrude");
            editor.SelectedSketchProfile=editor.SketchProfiles.First(p=>p.Source.Lines.SequenceEqual(outer)&&
                p.Source.HoleCircleIds.IsEmpty&&p.Source.Regions.IsEmpty);
            editor.AutoNestSketchRegionsCommand.Execute(null);
            Assert.Equal("3 nested sketch regions",editor.AutoNestStatus);
            await editor.PreviewCommand.ExecuteAsync(null);
            Assert.NotNull(editor.PreviewScene);
            await editor.ConfirmCommand.ExecuteAsync(null);
            var autoFeature=session.Snapshot.Features.Values.Single(f=>f.Id!=feature.Id);
            Assert.Equal(outerHole.Id,autoFeature.SketchSource!.Regions.Single().CircleId);
        }
        finally{editor.Detach();}
        var before=session.Snapshot;
        var changed=sketch with{Circles=sketch.Circles.Select(c=>c.Id==islandHole.Id?c with{Radius=1.5}:c).ToImmutableArray(),
            Constraints=sketch.Constraints.Select(c=>c is RadiusConstraint r&&r.Circle==islandHole.Id?r with{Radius=1.5}:c).ToImmutableArray()};
        await session.ExecuteAsync(new UpsertSketchCommand(changed,solver));
        Assert.Equal(1.5,session.Snapshot.Sketches[sketch.Id].Circles.Single(c=>c.Id==islandHole.Id).Radius);
        Assert.Equal(1.5,((ExtrudeRecipe)session.Snapshot.Features[feature.Id].Recipe).Profile.Islands.Single().Holes.Single().Radius);
        Assert.True(session.Snapshot.Features[feature.Id].Result.VolumeMm3<feature.Result.VolumeMm3,
            $"Before {feature.Result.VolumeMm3}; after {session.Snapshot.Features[feature.Id].Result.VolumeMm3}; stale {session.Snapshot.Features[feature.Id].IsStale}");
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
    }
}
