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

public sealed class CircularSketchProfileTests
{
    [Fact] public async Task MoreThanEightHolesCanBeSelectedAndAnExistingExtrusionCanRebindAtomically()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Many holes",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(100,100));
        for(int i=0;i<9;i++)draft.AddCircle(new(10+i*10,10),2);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.SelectedSketchId=draft.Value.Id;document.StartSketchFeature("Extrude");
            Assert.Equal(9,document.SketchHoleChoices.Count);
            foreach(var hole in document.SketchHoleChoices)hole.IsSelected=true;
            document.SizeZ=10;
            await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);
            await document.ConfirmCommand.ExecuteAsync(null);
            var created=Assert.Single(session.Snapshot.Features.Values);
            Assert.Equal(9,created.SketchSource!.HoleCircleIds.Length);
            Assert.InRange(Math.Abs(100000-360*Math.PI-created.Result.VolumeMm3),0,0.02);
            var before=session.Snapshot;
            document.EditFeature(created.Id);
            Assert.Equal(9,document.SketchHoleChoices.Count(c=>c.IsSelected));
            document.SketchHoleChoices[0].IsSelected=false;
            await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);
            await document.ConfirmCommand.ExecuteAsync(null);
            var rebound=session.Snapshot.Features[created.Id];
            Assert.Equal(8,rebound.SketchSource!.HoleCircleIds.Length);
            Assert.InRange(Math.Abs(100000-320*Math.PI-rebound.Result.VolumeMm3),0,0.02);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Equal(8,session.Snapshot.Features[created.Id].SketchSource!.HoleCircleIds.Length);
            var unchanged=session.Snapshot;
            var stale=rebound.SketchSource! with{Revision=Guid.NewGuid()};
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RecomputeCommand(created.Id,rebound.Recipe,stale)));
            Assert.Same(unchanged,session.Snapshot);
        }
        finally{document.Detach();}
    }

    [Fact] public void HolesRequireStrictContainmentAndSeparation()
    {
        var square=new SketchProfile([new(0,0),new(20,0),new(20,20),new(0,20)]);
        (square with{Holes=[new(new(5,5),2),new(new(15,15),3)]}).Validate();
        foreach(var hole in new[]{new CircularSketchRegion(new(2,10),2),new(new(1,1),2),new(new(21,10),1)})
            Assert.Throws<CadValidationException>(()=>(square with{Holes=[hole]}).Validate());
        Assert.Throws<CadValidationException>(()=>(square with{Holes=[new(new(5,5),3),new(new(11,5),3)]}).Validate());
        Assert.Throws<CadValidationException>(()=>(SketchProfile.FromCircle(new(0,0),10) with{Holes=[new(new(8,0),2)]}).Validate());
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(square with{Holes=[new(new(5,5),1)]},1,RigidTransform3d.Identity).Validate());
    }

    [Fact] public async Task ExactThroughHolesHaveCorrectVolumesAndExports()
    {
        using var files=new TestFiles();var (initial,_)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var square=new SketchProfile([new(0,0),new(20,0),new(20,20),new(0,20)],null,[new(new(5,5),2),new(new(15,15),3)]);
        await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(square,10,RigidTransform3d.Translate(2,3,4)),"Two holes"));
        var polygon=Assert.Single(session.Snapshot.Features.Values);
        Assert.Equal(4000-130*Math.PI,polygon.Result.VolumeMm3,3);
        Assert.Equal(2,polygon.Result.Bounds.Min.X,4);Assert.Equal(14,polygon.Result.Bounds.Max.Z,4);
        var annulus=SketchProfile.FromCircle(new(0,0),10) with{Holes=[new(new(0,0),3)]};
        await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(annulus,5,RigidTransform3d.Translate(30,0,0)),"Annulus"));
        Assert.InRange(Math.Abs(455*Math.PI-session.Snapshot.Features.Values.Single(f=>f.Name=="Annulus").Result.VolumeMm3),0,0.001);
        foreach(var extension in new[]{"step","iges","stl"})
        {
            var path=files.PathFor("holes."+extension);await kernel.ExportAsync(session.Snapshot,assets,path);
            Assert.True(new FileInfo(path).Length>0);
        }
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("holes.cadoryx"));
        using var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("holes.cadoryx"),assets);
        loaded.Snapshot.Validate();
        Assert.Equal(2,Assert.IsType<ExtrudeRecipe>(loaded.Snapshot.Features[polygon.Id].Recipe).Profile.Holes.Length);
        Assert.Equal(4000-130*Math.PI,loaded.Snapshot.Features[polygon.Id].Result.VolumeMm3,3);
        foreach(var feature in loaded.Snapshot.Features.Values)
            Assert.False(string.IsNullOrWhiteSpace(System.Text.Json.JsonSerializer.Serialize(feature,CadJson.Options)));
        FormatEvolutionTests.RewriteManifest(files.PathFor("holes.cadoryx"),m=>m with{Sections=m.Sections.Select(e=>e.Kind=="features"?e with{SchemaVersion=11}:e).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(files.PathFor("holes.cadoryx"),assets));
    }

    [Fact] public async Task LinkedHoleKeepsIdentityAndRejectsInvalidRecomputeAtomically()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Holed rectangle",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(20,20));draft.AddCircle(new(5,5),2);draft.AddCircle(new(15,15),3);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var holes=sketch.Circles.Select(c=>c.Id).ToImmutableArray();
        var source=SketchProfileReference.CreateWithHoles(sketch,sketch.Lines.Select(l=>l.Id),holes);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),10,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Linked holes",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);Assert.Equal(4000-130*Math.PI,feature.Result.VolumeMm3,3);
        var before=session.Snapshot;var radius=sketch.Constraints.OfType<RadiusConstraint>().First();
        await session.ExecuteAsync(new UpsertSketchCommand(sketch with{Constraints=sketch.Constraints.Replace(radius,radius with{Radius=3})},solver));
        var after=session.Snapshot;Assert.Equal(4000-180*Math.PI,after.Features[feature.Id].Result.VolumeMm3,3);
        Assert.Equal(holes,after.Features[feature.Id].SketchSource!.HoleCircleIds);
        Assert.False(string.IsNullOrWhiteSpace(System.Text.Json.JsonSerializer.Serialize(after.Features[feature.Id],CadJson.Options)));
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("linked-holes.cadoryx"));
        using(var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("linked-holes.cadoryx"),assets))
            Assert.False(string.IsNullOrWhiteSpace(System.Text.Json.JsonSerializer.Serialize(loaded.Snapshot.Features[feature.Id],CadJson.Options)));
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var changed=after.Sketches[sketch.Id];var moved=changed.Points.Single(p=>p.Id==changed.Circles[0].Center);
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(changed with{Points=changed.Points.Replace(moved,moved with{Position=new(0,5)})},solver)));
        Assert.Same(after,session.Snapshot);
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.StartSketchFeature("Extrude");
            Assert.Contains(document.SketchProfiles,p=>!p.Source.HoleCircleIds.IsDefaultOrEmpty&&p.Source.HoleCircleIds.Length==2);
            document.StartSketchFeature("Revolve");Assert.DoesNotContain(document.SketchProfiles,p=>!p.Source.HoleCircleIds.IsDefaultOrEmpty);
            document.EditFeature(feature.Id);
            Assert.True(document.CanChooseSketchProfile);
            Assert.False(document.CanChangeSketchAssociation);
            Assert.Equal(2,document.SelectedSketchProfile!.Source.HoleCircleIds.Length);
            Assert.Equal(2,document.SketchHoleChoices.Count(c=>c.IsSelected));
        }
        finally{document.Detach();}
    }

    [Fact] public async Task LinkedAnnulusSurvivesRecoveryStyleRoundTripAndJsonComparison()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Annulus",RigidTransform3d.Translate(140,0,0)));
        draft.AddCircle(new(0,0),10);draft.AddCircle(new(0,0),2);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var outer=sketch.Circles.Single(c=>c.Radius==10);var inner=sketch.Circles.Single(c=>c.Radius==2);
        var source=SketchProfileReference.CreateCircleWithHoles(sketch,outer.Id,[inner.Id]);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),6,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Recover annulus",part,sketchSource:source));
        var original=Assert.Single(session.Snapshot.Features.Values);
        Assert.False(string.IsNullOrWhiteSpace(System.Text.Json.JsonSerializer.Serialize(original,CadJson.Options)));
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("annulus.cadoryx"));
        using var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("annulus.cadoryx"),assets);
        var restored=loaded.Snapshot.Features[original.Id];
        Assert.False(string.IsNullOrWhiteSpace(System.Text.Json.JsonSerializer.Serialize(restored,CadJson.Options)));
        Assert.True(original.SketchSource!.HoleCircleIds.SequenceEqual(restored.SketchSource!.HoleCircleIds));
    }
    [Fact] public void OnlyAnExplicitNonConstructionCircleFormsAnExactRegion()
    {
        var (_,part)=SketchCommandTests.Document();
        var draft=new SketchDraft(CadSketch.Create(part,"Circle",RigidTransform3d.Identity));
        draft.AddCircle(new(3,4),5);
        var circle=Assert.Single(draft.Value.Circles);
        Assert.Equal(new CircularSketchRegion(new(3,4),5),SketchProfileBuilder.Circle(draft.Value,circle.Id).Circle);
        Assert.Empty(SketchLoops.Find(draft.Value));
        draft.SetConstruction([circle.Id],true);
        Assert.Throws<CadValidationException>(()=>SketchProfileBuilder.Circle(draft.Value,circle.Id));
        draft.Undo();
        Assert.NotNull(SketchProfileBuilder.Circle(draft.Value,circle.Id).Circle);
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(SketchProfileBuilder.Circle(draft.Value,circle.Id),Math.PI,RigidTransform3d.Identity).Validate());
    }

    [Fact] public async Task LinkedCircleExtrudesExactlyAndRebuildsAfterRadiusEditWithUndoAndFileRoundTrip()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Disk",RigidTransform3d.Translate(5,6,7)));
        draft.AddCircle(new(2,3),5);
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var circle=Assert.Single(sketch.Circles);
        var source=SketchProfileReference.CreateCircle(sketch,circle.Id);
        await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(SketchProfileBuilder.Circle(sketch,circle.Id),10,sketch.Plane),"Disk extrusion",part,sketchSource:source));
        var before=session.Snapshot;var feature=Assert.Single(before.Features.Values);
        Assert.Equal(Math.PI*250,feature.Result.VolumeMm3,4);
        Assert.Equal(2,feature.Result.Bounds.Min.X,4);
        Assert.Equal(12,feature.Result.Bounds.Max.X,4);
        var radius=Assert.Single(sketch.Constraints.OfType<RadiusConstraint>());
        var changed=sketch with{Constraints=sketch.Constraints.Replace(radius,radius with{Radius=7})};
        await session.ExecuteAsync(new UpsertSketchCommand(changed,solver));
        var after=session.Snapshot;
        Assert.Equal(Math.PI*490,after.Features[feature.Id].Result.VolumeMm3,4);
        Assert.Equal(after.Sketches[sketch.Id].Revision,after.Features[feature.Id].SketchSource!.Revision);
        foreach(var extension in new[]{"step","iges","stl"})
        {
            var path=files.PathFor("circle."+extension);
            await kernel.ExportAsync(after,assets,path);
            Assert.True(new FileInfo(path).Length>0);
        }
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();Assert.Same(after,session.Snapshot);
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("circle.cadoryx"));
        using var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("circle.cadoryx"),assets);
        var restored=loaded.Snapshot.Features[feature.Id];Assert.Equal(circle.Id,restored.SketchSource!.CircleId);
        Assert.Equal(7,Assert.IsType<ExtrudeRecipe>(restored.Recipe).Profile.Circle!.Radius);
        Assert.False(string.IsNullOrWhiteSpace(System.Text.Json.JsonSerializer.Serialize(restored,CadJson.Options)));
        loaded.Snapshot.Validate();
        FormatEvolutionTests.RewriteManifest(files.PathFor("circle.cadoryx"),m=>m with{Sections=m.Sections.Select(e=>e.Kind=="features"?e with{SchemaVersion=11}:e).ToImmutableArray()});
        using(var migrated=await new CadDocumentStorage().LoadAsync(files.PathFor("circle.cadoryx"),assets))
            Assert.Equal(circle.Id,migrated.Snapshot.Features[feature.Id].SketchSource!.CircleId);
        FormatEvolutionTests.RewriteManifest(files.PathFor("circle.cadoryx"),m=>m with{Sections=m.Sections.Select(e=>e.Kind=="features"?e with{SchemaVersion=10}:e).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(files.PathFor("circle.cadoryx"),assets));
    }

    [Fact] public async Task CircleRadiusEditorInputUpdatesItsDrivingConstraintAndProfilePicker()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Selectable circle",RigidTransform3d.Identity));draft.AddCircle(new(0,0),4);
        var solver=new ManagedSketchConstraintSolver();await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.StartSketchFeature("Extrude");
            Assert.Contains(document.SketchProfiles,p=>p.Source.CircleId==sketch.Circles[0].Id);
            await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false);
            editor.Select(sketch.Circles[0].Id);editor.CircleRadius=6;editor.ApplyCoordinatesCommand.Execute(null);
            Assert.Equal(6,Assert.Single(editor.Sketch.Constraints.OfType<RadiusConstraint>()).Radius);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            Assert.Equal(6,Assert.Single(editor.Sketch.Circles).Radius);
            editor.ToggleConstructionCommand.Execute(null);
            Assert.True(Assert.Single(editor.Sketch.Circles).IsConstruction);
            editor.UndoCommand.Execute(null);
            Assert.False(Assert.Single(editor.Sketch.Circles).IsConstruction);
            editor.CancelCommand.Execute(null);
            Assert.Equal(4,Assert.Single(session.Snapshot.Sketches[sketch.Id].Circles).Radius);
        }
        finally{document.Detach();}
    }
}
