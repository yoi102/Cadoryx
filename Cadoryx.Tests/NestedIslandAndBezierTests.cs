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

public sealed class NestedIslandAndBezierTests
{
    [Fact] public async Task QuadraticBezierIsExactEditableAndVersioned()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Bezier arch",RigidTransform3d.Translate(2,3,4)));
        draft.AddBezier(new(0,0),new(5,5),new(10,0));draft.Undo();Assert.Empty(draft.Value.Beziers);
        draft.Redo();var bezier=Assert.Single(draft.Value.Beziers);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var source=SketchProfileReference.CreateBezierSegment(sketch,bezier.Id);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),6,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Bezier extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-100),0,.02);
        Assert.Equal(4,feature.Result.Bounds.Min.Z,5);
        var before=session.Snapshot;var control=sketch.Points.Single(p=>p.Id==bezier.Control);
        await session.ExecuteAsync(new UpsertSketchCommand(sketch with
            {Points=sketch.Points.Replace(control,control with{Position=new(5,6)})},solver));
        var after=session.Snapshot;Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3-120),0,.02);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var invalid=after.Sketches[sketch.Id];var moved=invalid.Points.Single(p=>p.Id==bezier.Control);
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(invalid with
            {Points=invalid.Points.Replace(moved,moved with{Position=new(5,0)})},solver)));
        Assert.Same(after,session.Snapshot);
        foreach(var ext in new[]{"step","iges","stl"})
        {
            var path=files.PathFor("bezier."+ext);await kernel.ExportAsync(after,assets,path);
            Assert.True(new FileInfo(path).Length>0);
        }
        var file=files.PathFor("bezier.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();Assert.Equal(bezier.Id,loaded.Snapshot.Features[feature.Id].SketchSource!.BezierId);
            Assert.Single(loaded.Snapshot.Sketches[sketch.Id].Beziers);
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?
            s with{SchemaVersion=17}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
    }

    [Fact] public async Task PolygonHoleWithIslandMakesTwoPreciseSolids()
    {
        var outer=ImmutableArray.Create(new Point2d(0,0),new Point2d(20,0),new Point2d(20,20),new Point2d(0,20));
        var hole=ImmutableArray.Create(new Point2d(6,6),new Point2d(10,6),new Point2d(10,10),new Point2d(6,10));
        var island=ImmutableArray.Create(new Point2d(7,7),new Point2d(9,7),new Point2d(9,9),new Point2d(7,9));
        var profile=new SketchProfile(outer){PolygonHoles=[hole],Islands=[new(island)]};profile.Validate();
        var assets=new MemoryAssetStore();using var result=await new OcctGeometryKernel().EvaluateAsync(
            new ExtrudeRecipe(profile,5,RigidTransform3d.Identity),assets);
        Assert.Equal(BodyKind.Compound,result.Geometry.Kind);
        Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-1940),0,.01);
        Assert.Throws<CadValidationException>(()=>(profile with{Islands=[new(outer)]}).Validate());
        Assert.Throws<CadValidationException>(()=>(profile with{Islands=[new(island),new(island)]}).Validate());
    }

    [Fact] public void ArcIslandProbeOnCircularHoleSeamRemainsInside()
    {
        var island=ImmutableArray.Create<SketchBoundaryCurve>(
            new(new(-2,-2),new(-2,2)),new(new(-2,2),new(2,2)),
            new(new(2,2),new(2,-2)),new(new(2,-2),new(-2,-2),new(0,-4)));
        var profile=SketchProfile.FromCircle(new(0,0),10) with
        {
            Holes=[new(new(0,0),5)],Islands=[new([],null,island)]
        };
        profile.Validate();
    }

    [Fact] public async Task LinkedPolygonIslandValidatesCachedProfile()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Polygon island",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(20,20));draft.AddRectangle(new(6,6),new(10,10));
        draft.AddRectangle(new(7,7),new(9,9));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];
        var source=SketchProfileReference.CreateWithPolygonHoles(sketch,sketch.Lines.Take(4).Select(l=>l.Id),
            [sketch.Lines.Skip(4).Take(4).Select(l=>l.Id)]) with
        {IslandPolygonLines=[sketch.Lines.Skip(8).Select(l=>l.Id).ToImmutableArray()]};
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),5,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Polygon island extrusion",part,sketchSource:source));
        session.Snapshot.Validate();
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-1940),0,.01);
        Assert.NotEmpty(System.Text.Json.JsonSerializer.Serialize(feature,CadJson.Options));
    }

    [Fact] public async Task CurvedHoleIslandKeepsStableSourceAndRejectsNesting()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Curved island",RigidTransform3d.Identity));
        draft.AddCircle(new(0,0),10);draft.AddCircle(new(0,0),5);
        draft.AddArc(new(-2,-2),new(0,-4),new(2,-2));var arc=draft.Value.Arcs[^1];
        draft.AddLine(new(2,-2),new(2,2),arc.End);
        draft.AddLine(new(2,2),new(-2,2),draft.Value.Lines[^1].End);
        draft.AddLine(new(-2,2),new(-2,-2),draft.Value.Lines[^1].End,arc.Start);
        var loop=Assert.Single(SketchMixedLoops.Find(draft.Value));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];
        var source=SketchProfileReference.CreateCircleWithHoles(sketch,sketch.Circles[0].Id,[sketch.Circles[1].Id])
            with{IslandMixedIds=[loop]};
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Curved island extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.Equal(BodyKind.Compound,feature.Result.Kind);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-(32+154*Math.PI)),0,.03);
        var cached=(ExtrudeRecipe)feature.Recipe;
        Assert.Throws<CadValidationException>(()=>source.ValidateCache(session.Snapshot,
            feature with{Recipe=cached with{Profile=cached.Profile with{Islands=[]}}}));
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.StartSketchFeature("Extrude");
            document.SelectedSketchProfile=document.SketchProfiles.Single(p=>p.Source.CircleId==sketch.Circles[0].Id&&
                p.Source.HoleCircleIds.SequenceEqual([sketch.Circles[1].Id]));
            Assert.Single(document.SketchIslandMixedChoices);
        }
        finally{document.Detach();}
        var file=files.PathFor("curved-island.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();
            Assert.True(loop.SequenceEqual(loaded.Snapshot.Features[feature.Id].SketchSource!.IslandMixedIds.Single()));
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?
            s with{SchemaVersion=16}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
    }

    [Fact] public async Task EmptyNewFieldsCanMigrateFromPreviousFeatureAndSketchVersions()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(CadSketch.Create(part,"Legacy compatible",RigidTransform3d.Identity),
            new ManagedSketchConstraintSolver()));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Legacy box",part));
        var file=files.PathFor("legacy-compatible.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind switch
        {
            "features"=>s with{SchemaVersion=16},
            "sketches"=>s with{SchemaVersion=4},
            _=>s
        }).ToImmutableArray()});
        using var loaded=await new CadDocumentStorage().LoadAsync(file,assets);
        loaded.Snapshot.Validate();
        Assert.Single(loaded.Snapshot.Features);
        Assert.Single(loaded.Snapshot.Sketches);
    }
}
