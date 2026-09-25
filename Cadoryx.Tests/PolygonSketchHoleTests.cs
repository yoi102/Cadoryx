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

public sealed class PolygonSketchHoleTests
{
    private static readonly ImmutableArray<Point2d> Outer=[new(0,0),new(20,0),new(20,20),new(0,20)];
    private static readonly ImmutableArray<Point2d> Inner=[new(6,6),new(10,6),new(10,10),new(6,10)];

    [Fact] public void PolygonHolesRejectTouchingOverlappingAndNestedBoundaries()
    {
        (new SketchProfile(Outer) with{PolygonHoles=[Inner]}).Validate();
        Assert.Throws<CadValidationException>(()=>(new SketchProfile(Outer) with{PolygonHoles=[[new(0,4),new(3,4),new(3,8),new(0,8)]]}).Validate());
        Assert.Throws<CadValidationException>(()=>(new SketchProfile(Outer) with{PolygonHoles=[Inner,[new(8,8),new(9,8),new(9,9),new(8,9)]]}).Validate());
        Assert.Throws<CadValidationException>(()=>(new SketchProfile(Outer) with{PolygonHoles=[Inner],Holes=[new(new(8,8),1)]}).Validate());
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(new SketchProfile(Outer) with{PolygonHoles=[Inner]},1,RigidTransform3d.Identity).Validate());
    }

    [Fact] public async Task ExactPolygonHoleCanBeSelectedReboundPersistedAndExported()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Polygon hole",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(20,20));draft.AddRectangle(new(6,6),new(10,10));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var outer=sketch.Lines.Take(4).Select(l=>l.Id).ToImmutableArray();
        var inner=sketch.Lines.Skip(4).Select(l=>l.Id).ToImmutableArray();
        var source=SketchProfileReference.CreateWithPolygonHoles(sketch,outer,[inner]);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),10,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Polygon through hole",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-3840),0,0.01);
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            vm.SelectedTargetPart=part;vm.EditFeature(feature.Id);
            Assert.Single(vm.SketchPolygonHoleChoices);
            Assert.True(vm.SketchPolygonHoleChoices[0].IsSelected);
            vm.SketchPolygonHoleChoices[0].IsSelected=false;
            await vm.PreviewCommand.ExecuteAsync(null);Assert.True(vm.HasPreview,vm.ToolStatus);
            await vm.ConfirmCommand.ExecuteAsync(null);
            Assert.InRange(Math.Abs(session.Snapshot.Features[feature.Id].Result.VolumeMm3-4000),0,0.01);
            await session.UndoAsync();Assert.InRange(Math.Abs(session.Snapshot.Features[feature.Id].Result.VolumeMm3-3840),0,0.01);
            await session.RedoAsync();Assert.Empty(session.Snapshot.Features[feature.Id].SketchSource!.PolygonHoleLines);
        }
        finally{vm.Detach();}
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("polygon-holes.cadoryx"));
        using(var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("polygon-holes.cadoryx"),assets))
        {
            loaded.Snapshot.Validate();
            Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,
                FormatEvolutionTests.Manifest(files.PathFor("polygon-holes.cadoryx")).Sections.Single(s=>s.Kind=="features").SchemaVersion);
        }
        FormatEvolutionTests.RewriteManifest(files.PathFor("polygon-holes.cadoryx"),m=>m with{
            Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=12}:s).ToImmutableArray()});
        using(var migrated=await new CadDocumentStorage().LoadAsync(files.PathFor("polygon-holes.cadoryx"),assets))
            Assert.Empty(migrated.Snapshot.Features[feature.Id].SketchSource!.PolygonHoleLines);
        // The source with the selected hole must also survive a full file round trip.
        await session.UndoAsync();
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("polygon-holes-selected.cadoryx"));
        using(var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("polygon-holes-selected.cadoryx"),assets))
        {
            Assert.Single(loaded.Snapshot.Features[feature.Id].SketchSource!.PolygonHoleLines);
            Assert.InRange(Math.Abs(loaded.Snapshot.Features[feature.Id].Result.VolumeMm3-3840),0,0.01);
        }
        foreach(var ext in new[]{"step","iges","stl"})
        {
            var path=files.PathFor("polygon-holes."+ext);await kernel.ExportAsync(session.Snapshot,assets,path);
            Assert.True(new FileInfo(path).Length>0);
        }
        FormatEvolutionTests.RewriteManifest(files.PathFor("polygon-holes-selected.cadoryx"),m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=12}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(files.PathFor("polygon-holes-selected.cadoryx"),assets));
    }

    [Fact] public async Task ModelingPickerCreatesLinkedHoleAndSketchEditsRecomputeAtomically()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Editable polygon hole",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(20,20));draft.AddRectangle(new(6,6),new(10,10));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
        var outer=draft.Value.Lines.Take(4).Select(l=>l.Id).ToHashSet();
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            vm.SelectedTargetPart=part;vm.SelectedSketchId=draft.Value.Id;vm.StartSketchFeature("Extrude");
            vm.SelectedSketchProfile=vm.SketchProfiles.Single(p=>p.Source.Lines.Length==4&&p.Source.Lines.ToHashSet().SetEquals(outer));
            var choice=Assert.Single(vm.SketchPolygonHoleChoices);choice.IsSelected=true;vm.SizeZ=10;
            await vm.PreviewCommand.ExecuteAsync(null);Assert.True(vm.HasPreview,vm.ToolStatus);
            await vm.ConfirmCommand.ExecuteAsync(null);
            var feature=Assert.Single(session.Snapshot.Features.Values);
            Assert.Single(feature.SketchSource!.PolygonHoleLines);Assert.InRange(Math.Abs(feature.Result.VolumeMm3-3840),0,0.01);
            var before=session.Snapshot;var sketch=before.Sketches[draft.Value.Id];
            var innerWidth=sketch.Constraints.OfType<OffsetXConstraint>().Last();
            await session.ExecuteAsync(new UpsertSketchCommand(sketch with{Constraints=sketch.Constraints.Replace(innerWidth,innerWidth with{Offset=5})},
                new ManagedSketchConstraintSolver()));
            var changed=session.Snapshot;
            Assert.InRange(Math.Abs(changed.Features[feature.Id].Result.VolumeMm3-3800),0,0.01);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(changed,session.Snapshot);
            var changedSketch=changed.Sketches[draft.Value.Id];var width=changedSketch.Constraints.OfType<OffsetXConstraint>().Last();
            await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(changedSketch with{
                Constraints=changedSketch.Constraints.Replace(width,width with{Offset=15})},new ManagedSketchConstraintSolver())));
            Assert.Same(changed,session.Snapshot);
        }
        finally{vm.Detach();}
    }
}
