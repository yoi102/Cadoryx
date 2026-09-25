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

public sealed class MixedCurveSketchTests
{
    private static SketchDraft Draft(DefinitionId part)
    {
        var draft=new SketchDraft(CadSketch.Create(part,"Mixed loop",RigidTransform3d.Translate(2,3,4)));
        draft.AddArc(new(0,0),new(5,-5),new(10,0));
        var arc=draft.Value.Arcs.Single();
        draft.AddLine(new(10,0),new(10,10),arc.End);
        var topRight=draft.Value.Lines[^1].End;
        draft.AddLine(new(10,10),new(0,10),topRight);
        var topLeft=draft.Value.Lines[^1].End;
        draft.AddLine(new(0,10),new(0,0),topLeft,arc.Start);
        return draft;
    }

    [Fact] public void OneArcAndThreeLinesFormAStableUnambiguousLoop()
    {
        var (_,part)=SketchCommandTests.Document();var draft=Draft(part);
        var loop=Assert.Single(SketchMixedLoops.Find(draft.Value));
        var profile=SketchProfileBuilder.Mixed(draft.Value,loop);
        Assert.Equal(4,profile.BoundaryCurves.Length);
        Assert.Single(profile.BoundaryCurves.Where(c=>c.Middle is not null));
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(profile,Math.PI,RigidTransform3d.Identity).Validate());
        var arc=draft.Value.Arcs.Single();draft.SetConstruction([arc.Id],true);
        Assert.Empty(SketchMixedLoops.Find(draft.Value));draft.Undo();
        Assert.Single(SketchMixedLoops.Find(draft.Value));
        draft.AddLine(new(0,0),new(10,0),arc.Start,arc.End);
        Assert.Empty(SketchMixedLoops.Find(draft.Value)); // A branch is never guessed into one profile.
    }

    [Fact] public void CrossingAndMalformedMixedBoundariesAreRejected()
    {
        var crossing=new SketchProfile([]){BoundaryCurves=[
            new(new(0,0),new(10,0),new(5,12)),
            new(new(10,0),new(10,10)),new(new(10,10),new(0,10)),new(new(0,10),new(0,0))]};
        Assert.Throws<CadValidationException>(()=>crossing.Validate());
        var twoArcs=new SketchProfile([]){BoundaryCurves=[
            new(new(0,0),new(10,0),new(5,-5)),
            new(new(10,0),new(10,10),new(12,5)),new(new(10,10),new(0,10)),new(new(0,10),new(0,0))]};
        twoArcs.Validate();
        var (_,part)=SketchCommandTests.Document();var draft=Draft(part);var loop=SketchMixedLoops.Find(draft.Value)[0];
        Assert.Throws<CadValidationException>(()=>SketchProfileBuilder.Mixed(draft.Value,[loop[0],loop[2],loop[1],loop[3]]));
        // Reversing the whole ordered loop is valid; a shuffled order is not.
    }

    [Fact] public async Task ExactMixedLoopRecomputesAndSurvivesFileAndExport()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=Draft(part);await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var loop=Assert.Single(SketchMixedLoops.Find(sketch));
        var source=SketchProfileReference.CreateMixed(sketch,loop);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),10,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Mixed extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-(1000+125*Math.PI)),0,0.02);
        Assert.Equal(4,feature.Result.Bounds.Min.Z,5);
        var before=session.Snapshot;var arc=sketch.Arcs.Single();var middle=sketch.Points.Single(p=>p.Id==arc.Middle);
        await session.ExecuteAsync(new UpsertSketchCommand(sketch with{Points=sketch.Points.Replace(middle,middle with{Position=new(5,-6)})},solver));
        var after=session.Snapshot;
        Assert.True(loop.SequenceEqual(after.Features[feature.Id].SketchSource!.MixedBoundaryIds));
        Assert.NotEqual(before.Features[feature.Id].Result.VolumeMm3,after.Features[feature.Id].Result.VolumeMm3);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var invalid=after.Sketches[sketch.Id];var moved=invalid.Points.Single(p=>p.Id==arc.Middle);
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(invalid with
            {Points=invalid.Points.Replace(moved,moved with{Position=new(5,12)})},solver)));
        Assert.Same(after,session.Snapshot);
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.StartSketchFeature("Extrude");
            Assert.Contains(document.SketchProfiles,p=>p.Source.MixedBoundaryIds.SequenceEqual(loop));
            document.SelectedSketchProfile=document.SketchProfiles.Single(p=>p.Source.MixedBoundaryIds.SequenceEqual(loop));
            Assert.True(document.CanChooseSketchHoles);
            document.StartSketchFeature("Revolve");
            Assert.DoesNotContain(document.SketchProfiles,p=>!p.Source.MixedBoundaryIds.IsEmpty);
        }
        finally{document.Detach();}
        foreach(var ext in new[]{"step","iges","stl"})
        {
            var path=files.PathFor("mixed."+ext);await kernel.ExportAsync(after,assets,path);
            Assert.True(new FileInfo(path).Length>0);
        }
        var file=files.PathFor("mixed.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();
            Assert.True(loop.SequenceEqual(loaded.Snapshot.Features[feature.Id].SketchSource!.MixedBoundaryIds));
            Assert.Equal(after.Features[feature.Id].Result.VolumeMm3,loaded.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        }
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?
            s with{SchemaVersion=15}:s).ToImmutableArray()});
        using(var migrated=await new CadDocumentStorage().LoadAsync(file,assets))migrated.Snapshot.Validate();
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?
            s with{SchemaVersion=14}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
    }
}
