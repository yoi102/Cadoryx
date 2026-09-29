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

public sealed class CubicSplineSketchTests
{
    private static readonly ImmutableArray<Point2d> Controls=
        [new(0,0),new(2,4),new(5,6),new(8,4),new(10,0)];

    [Fact] public async Task CubicSplineIsExactLinkedEditableAndVersioned()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Cubic spline",RigidTransform3d.Translate(2,3,4)));
        draft.AddSpline(Controls);draft.Undo();Assert.Empty(draft.Value.Splines);draft.Redo();
        var spline=Assert.Single(draft.Value.Splines);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];
        var source=SketchProfileReference.CreateSplineSegment(sketch,spline.Id);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),5,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Spline extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-168.5),0,.03);
        Assert.Equal(4,feature.Result.Bounds.Min.Z,5);
        var before=session.Snapshot;var middle=sketch.Points.Single(p=>p.Id==spline.Controls[2]);
        await session.ExecuteAsync(new UpsertSketchCommand(sketch with
        {Points=sketch.Points.Replace(middle,middle with{Position=new(5,8)})},solver));
        var after=session.Snapshot;Assert.True(after.Features[feature.Id].Result.VolumeMm3>feature.Result.VolumeMm3);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var illegal=after.Sketches[sketch.Id];var point=illegal.Points.Single(p=>p.Id==spline.Controls[2]);
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(illegal with
        {Points=illegal.Points.Replace(point,point with{Position=new(5,-2)})},solver)));
        Assert.Same(after,session.Snapshot);
        foreach(var ext in new[]{"step","iges","stl"})
        {
            var path=files.PathFor("spline."+ext);await kernel.ExportAsync(after,assets,path);
            Assert.True(new FileInfo(path).Length>0);
        }
        var file=files.PathFor("spline.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();
            Assert.Equal(spline.Id,loaded.Snapshot.Features[feature.Id].SketchSource!.SplineId);
            Assert.Equal(5,Assert.Single(loaded.Snapshot.Sketches[sketch.Id].Splines).Controls.Length);
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind switch
        {"features"=>s with{SchemaVersion=18},"sketches"=>s with{SchemaVersion=5},_=>s}).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
    }

    [Fact] public void SplineBoundaryRejectsUnsafeControlPolygon()
    {
        SketchSplineGeometry.Validate(Controls);
        Assert.Equal(Controls[0],SketchSplineGeometry.At(Controls,0));
        Assert.Equal(Controls[^1],SketchSplineGeometry.At(Controls,1));
        Assert.Throws<CadValidationException>(()=>SketchSplineGeometry.Validate([new(0,0),new(2,4),new(5,-1),new(8,4),new(10,0)]));
        Assert.Throws<CadValidationException>(()=>SketchSplineGeometry.Validate([new(0,0),new(8,4),new(5,6),new(2,4),new(10,0)]));
        Assert.Throws<CadValidationException>(()=>SketchSplineGeometry.Validate([new(0,0),new(2,4),new(10,0)]));
        SketchSplineGeometry.Validate([new(0,0),new(1,2),new(3,4),new(5,5),new(7,4),new(10,0)]);
        Assert.Throws<CadValidationException>(()=>SketchSplineGeometry.At(Controls,double.NaN));
        Assert.Throws<CadValidationException>(()=>SketchSplineGeometry.Validate(
            Enumerable.Range(0,17).Select(i=>new Point2d(i,i is 0 or 16?0:2)).ToImmutableArray()));
    }

    [Fact] public async Task PreviousFeatureAndSketchSchemasMigrateWhenNewFieldsAreAbsent()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var (document,part)=SketchCommandTests.Document();
        var sketch=SketchTestData.Rectangle(part);
        document=document with{Sketches=document.Sketches.Add(sketch.Id,sketch)};
        var path=files.PathFor("previous.cadoryx");await storage.SaveAsync(document,assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind switch
        {"features"=>s with{SchemaVersion=18},"sketches"=>s with{SchemaVersion=5},_=>s}).ToImmutableArray()});
        using(var migrated=await storage.LoadAsync(path,assets))
        {
            Assert.Contains(migrated.Diagnostics,d=>d.Code=="IO.MIGRATED");
            Assert.Equal(document.Id,migrated.Snapshot.Id);
            Assert.Equal(sketch.Revision,migrated.Snapshot.Sketches[sketch.Id].Revision);
            await storage.SaveAsync(migrated.Snapshot,assets,path);
        }
        using var current=await storage.LoadAsync(path,assets);
        Assert.Empty(current.Diagnostics);
        var manifest=FormatEvolutionTests.Manifest(path);
        Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);
        Assert.Equal(6,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
    }

    [Fact] public async Task FourSixAndSixteenControlSplineProfilesBuildAndRoundtrip()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        ImmutableArray<Point2d>[] samples=[
            [new(0,0),new(10d/3,4),new(20d/3,4),new(10,0)],
            [new(0,0),new(2,3),new(4,5),new(6,5),new(8,3),new(10,0)],
            Enumerable.Range(0,16).Select(i=>new Point2d(i*10d/15,i is 0 or 15?0:4*Math.Sin(Math.PI*i/15))).ToImmutableArray()];
        for(int i=0;i<samples.Length;i++)
        {
            await using var session=new CadDocumentSession(DocumentSnapshot.Create("Spline "+i),assets,kernel,new InlineSessionDispatcher());
            var recipe=new ExtrudeRecipe(new SketchProfile([]){Spline=new(samples[i])},5,RigidTransform3d.Identity);
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Spline"));
            var volume=Assert.Single(session.Snapshot.Features.Values).Result.VolumeMm3;
            Assert.True(volume>0&&double.IsFinite(volume));
            if(i==0)Assert.InRange(Math.Abs(volume-100),0,.03);
            var path=files.PathFor($"spline-{i}.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);
            Assert.Equal(samples[i].Length,Assert.Single(loaded.Snapshot.Features.Values).Recipe is ExtrudeRecipe e?e.Profile.Spline!.Controls.Length:0);
            Assert.InRange(Math.Abs(Assert.Single(loaded.Snapshot.Features.Values).Result.VolumeMm3-volume),0,1e-9);
        }
    }
}
