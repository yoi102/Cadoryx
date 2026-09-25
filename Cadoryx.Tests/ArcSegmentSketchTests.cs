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

public sealed class ArcSegmentSketchTests
{
    [Fact] public void ThreePointDraftIsEditableAndRejectsDegeneration()
    {
        var (_,part)=SketchCommandTests.Document();
        var draft=new SketchDraft(CadSketch.Create(part,"Arc",RigidTransform3d.Identity));
        draft.AddArc(new(0,0),new(5,5),new(10,0));
        var arc=Assert.Single(draft.Value.Arcs);
        var profile=SketchProfileBuilder.ArcSegment(draft.Value,arc.Id);
        Assert.Equal(new ThreePointArcRegion(new(0,0),new(5,5),new(10,0)),profile.Arc);
        Assert.InRange(Math.Abs(SketchArcGeometry.Through(profile.Arc!.Start,profile.Arc.Middle,profile.Arc.End).Radius-5),0,1e-8);
        draft.SetConstruction([arc.Id],true);
        Assert.Throws<CadValidationException>(()=>SketchProfileBuilder.ArcSegment(draft.Value,arc.Id));
        draft.Undo();
        Assert.NotNull(SketchProfileBuilder.ArcSegment(draft.Value,arc.Id).Arc);
        draft.MovePoint(arc.Middle,new(5,6));
        Assert.Equal(new Point2d(5,6),SketchProfileBuilder.ArcSegment(draft.Value,arc.Id).Arc!.Middle);
        Assert.Throws<CadValidationException>(()=>draft.MovePoint(arc.Middle,new(5,0)));
        Assert.Equal(new Point2d(5,6),draft.Value.Points.Single(p=>p.Id==arc.Middle).Position);
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(profile,Math.PI,RigidTransform3d.Identity).Validate());
    }

    [Fact] public async Task ExactArcSegmentExtrudesRecomputesPersistsAndExports()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Arc segment",RigidTransform3d.Translate(2,3,4)));
        draft.AddArc(new(0,0),new(5,5),new(10,0));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var arc=Assert.Single(sketch.Arcs);
        var source=SketchProfileReference.CreateArcSegment(sketch,arc.Id);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),10,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Arc extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-125*Math.PI),0,0.01);
        Assert.Equal(4,feature.Result.Bounds.Min.Z,5);
        var before=session.Snapshot;
        var middle=sketch.Points.Single(p=>p.Id==arc.Middle);
        await session.ExecuteAsync(new UpsertSketchCommand(sketch with{Points=sketch.Points.Replace(middle,middle with{Position=new(5,6)})},solver));
        var after=session.Snapshot;
        Assert.Equal(arc.Id,after.Features[feature.Id].SketchSource!.ArcId);
        Assert.NotEqual(before.Features[feature.Id].Result.VolumeMm3,after.Features[feature.Id].Result.VolumeMm3);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.StartSketchFeature("Extrude");
            Assert.Contains(document.SketchProfiles,p=>p.Source.ArcId==arc.Id);
            Assert.DoesNotContain(document.SketchProfiles,p=>p.Source.ArcId==arc.Id&&p.Source.CircleId is not null);
        }
        finally{document.Detach();}
        foreach(var extension in new[]{"step","iges","stl"})
        {
            var path=files.PathFor("arc."+extension);await kernel.ExportAsync(after,assets,path);
            Assert.True(new FileInfo(path).Length>0);
        }
        var file=files.PathFor("arc.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();
            Assert.Equal(arc.Id,Assert.Single(loaded.Snapshot.Sketches[sketch.Id].Arcs).Id);
            Assert.Equal(arc.Id,loaded.Snapshot.Features[feature.Id].SketchSource!.ArcId);
            Assert.Equal(after.Features[feature.Id].Result.VolumeMm3,loaded.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?
            s with{SchemaVersion=14}:s).ToImmutableArray()});
        using(var migrated=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            migrated.Snapshot.Validate();
            Assert.Equal(arc.Id,migrated.Snapshot.Features[feature.Id].SketchSource!.ArcId);
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind switch
        {"features"=>s with{SchemaVersion=13},"sketches"=>s with{SchemaVersion=3},_=>s}).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
    }
}
