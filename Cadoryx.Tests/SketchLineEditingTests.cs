using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.IO;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchLineEditingTests
{
    private static SketchDraft Draft()=>new(CadSketch.Create(DefinitionId.New(),"Edit",RigidTransform3d.Identity));
    private static Point2d Point(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    [Fact] public void EndTrimPreservesLineIdentityAndSharedPointWithoutMovingItsOtherGeometry()
    {
        var d=Draft();d.AddLine(new(0,0),new(10,0));var line=d.Value.Lines[0];
        d.AddLine(new(0,0),new(0,5),line.Start);d.AddLine(new(4,-5),new(4,5),construction:true);
        d.Change(s=>s with{Constraints=[new LengthConstraint(SketchConstraintId.New(),line.Id,10),
            new FixPointConstraint(SketchConstraintId.New(),line.Start,new(0,0))]});
        var before=d.Value;d.TrimLine(line.Id,new(2,0));
        var edited=d.Value.Lines.Single(l=>l.Id==line.Id);
        Assert.Equal(new Point2d(4,0),Point(d.Value,edited.Start));Assert.Equal(line.End,edited.End);
        Assert.Equal(new Point2d(0,0),Point(d.Value,line.Start));Assert.Equal(before.Lines[1],d.Value.Lines.Single(l=>l.Id==before.Lines[1].Id));
        Assert.Single(d.Value.Constraints);Assert.IsType<FixPointConstraint>(d.Value.Constraints[0]);d.Value.Validate();
        var after=d.Value;d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(after,d.Value);
    }
    [Fact] public void InteriorTrimSplitsIdentityAndRemovesOnlyConstraintsOnEditedGeometry()
    {
        var d=Draft();d.AddLine(new(0,0),new(10,0),construction:true);var line=d.Value.Lines[0];
        d.AddLine(new(3,-5),new(3,5));d.AddLine(new(7,-5),new(7,5));
        var keep=new VerticalConstraint(SketchConstraintId.New(),d.Value.Lines[1].Id);
        d.Change(s=>s with{Constraints=[new HorizontalConstraint(SketchConstraintId.New(),line.Id),keep]});
        d.TrimLine(line.Id,new(5,0));
        Assert.DoesNotContain(d.Value.Lines,l=>l.Id==line.Id);
        var pieces=d.Value.Lines.Where(l=>l.IsConstruction).OrderBy(l=>Point(d.Value,l.Start).X).ToArray();
        Assert.Equal(2,pieces.Length);Assert.Equal(new Point2d(3,0),Point(d.Value,pieces[0].End));
        Assert.Equal(new Point2d(7,0),Point(d.Value,pieces[1].Start));Assert.Equal(keep,Assert.Single(d.Value.Constraints));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ExtendChoosesNearestFiniteIntersectionAndReusesUniqueBoundaryEndpoint(bool start)
    {
        var d=Draft();d.AddLine(new(0,0),new(10,0));var line=d.Value.Lines[0];
        double near=start?-3:13,far=start?-6:16;
        d.AddLine(new(far,-2),new(far,2));d.AddLine(new(near,0),new(near,2));var boundary=d.Value.Lines[^1];
        // This nearer supporting line does not meet the ray within its finite segment.
        d.AddLine(new(start?-1:11,2),new(start?-1:11,4));
        d.ExtendLine(line.Id,new(start?1:9,0));var edited=d.Value.Lines.Single(l=>l.Id==line.Id);
        Assert.Equal(boundary.Start,start?edited.Start:edited.End);
        Assert.Equal(start?line.End:line.Start,start?edited.End:edited.Start);d.Value.Validate();
    }
    [Fact] public void AmbiguousOrMissingBoundariesLeaveValueAndHistoryUnchanged()
    {
        var d=Draft();d.AddLine(new(0,0),new(10,0));var line=d.Value.Lines[0];var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.TrimLine(line.Id,new(2,0)));
        Assert.Throws<CadValidationException>(()=>d.ExtendLine(line.Id,new(5,0)));
        Assert.Throws<CadValidationException>(()=>d.ExtendLine(line.Id,new(9,0)));Assert.Same(before,d.Value);
        d.AddLine(new(3,-2),new(3,2));before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.TrimLine(line.Id,new(3,0)));Assert.Same(before,d.Value);
        d.AddLine(new(13,0),new(13,2));d.AddLine(new(13,0),new(14,2));before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.ExtendLine(line.Id,new(9,0)));Assert.Same(before,d.Value);
        d.AddLine(new(15,0),new(20,0));before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.ExtendLine(line.Id,new(9,0)));Assert.Same(before,d.Value);
        d.Undo();Assert.Equal(before.Lines.Length-1,d.Value.Lines.Length);
    }
    [Fact] public async Task SplittingAssociatedBoundaryFreezesItsFeatureWithoutGuessingAPiece()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var solver=new ManagedSketchConstraintSolver();
        var d=new SketchDraft(CadSketch.Create(part,"Linked",RigidTransform3d.Identity));d.AddRectangle(new(1,0),new(11,5));
        d.AddLine(new(4,-2),new(4,2),construction:true);d.AddLine(new(8,-2),new(8,2),construction:true);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];
        var source=SketchProfileReference.Create(s,s.Lines.Where(l=>!l.IsConstruction).Select(l=>l.Id));
        var recipe=source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Linked",part,sketchSource:source));var before=session.Snapshot;
        var edit=new SketchDraft(s);edit.TrimLine(s.Lines[0].Id,new(6,0));
        await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.True(feature.IsStale);Assert.Equal(Assert.Single(before.Features.Values).SketchSource,feature.SketchSource);
        Assert.Equal(150,Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3,5);session.Snapshot.Validate();
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
    }
    [Fact] public async Task EditorLineEditPreviewConfirmUndoAndFileReopenPreserveAuthoredIdentities()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var solver=new ManagedSketchConstraintSolver();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Editable",RigidTransform3d.Identity));
        d.AddLine(new(0,0),new(10,0));d.AddLine(new(4,-2),new(4,2));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var before=session.Snapshot;var sketch=before.Sketches[d.Value.Id];
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false){Tool="TrimLine"};
            editor.EditLine(sketch.Lines[0].Id,new(2,0));Assert.False(editor.CanConfirm);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
            await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;var line=after.Sketches[sketch.Id].Lines.Single(l=>l.Id==sketch.Lines[0].Id);
        Assert.Equal(new Point2d(4,0),Point(after.Sketches[sketch.Id],line.Start));
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var storage=new CadDocumentStorage();var path=files.PathFor("trim.cadoryx");await session.SaveAsync(storage,path);
        using var loaded=await storage.LoadAsync(path,assets);loaded.Snapshot.Validate();
        var restored=loaded.Snapshot.Sketches[sketch.Id];Assert.Equal(line,restored.Lines.Single(l=>l.Id==line.Id));
        Assert.Equal(Point(after.Sketches[sketch.Id],line.Start),Point(restored,line.Start));
    }
}
