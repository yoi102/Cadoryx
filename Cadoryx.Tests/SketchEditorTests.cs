using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchEditorTests
{
    [Fact] public async Task NewSketchAndItsPartAreOnePreviewedCancelableTransaction()
    {
        var initial=DocumentSnapshot.Create("Empty");var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher(),"saved.cadoryx");var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var s=CadSketch.Create(DefinitionId.New(),"Draft",RigidTransform3d.Identity);
            await using(var canceled=new SketchEditorViewModel(document,kernel,new ManagedSketchConstraintSolver(),s,true))
            {
                canceled.Mutate(d=>d.AddRectangle(new(0,0),new(40,30)));await canceled.PreviewCommand.ExecuteAsync(null);
                Assert.True(canceled.CanConfirm,canceled.Status);Assert.Same(initial,session.Snapshot);Assert.False(session.IsDirty);canceled.CancelCommand.Execute(null);
                Assert.False(canceled.CanConfirm);Assert.Null(document.PreviewScene);
            }
            await using(var editor=new SketchEditorViewModel(document,kernel,new ManagedSketchConstraintSolver(),s,true))
            {
                editor.Name="Authored";editor.Plane="XZ";editor.OriginZ=15;editor.Mutate(d=>d.AddRectangle(new(0,0),new(40,30)));
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var actual=Assert.Single(session.Snapshot.Sketches.Values);Assert.Equal("Authored",actual.Name);Assert.Equal(15,actual.Plane.Translation.Z);Assert.NotEqual(Quaterniond.Identity,actual.Plane.Rotation);
            Assert.Single(session.Snapshot.Definitions.Values.OfType<PartDefinition>());await session.UndoAsync();Assert.Same(initial,session.Snapshot);Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }
    [Fact] public async Task DimensionsPreviewRebuildsButCancelKeepsOriginalBodiesAndSavepoint()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var s=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(s,solver));s=session.Snapshot.Sketches[s.Id];await session.ExecuteAsync(SketchAssociationTests.Extrude(s,10));
        var before=session.Snapshot;int count=assets.Count;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,s,false))
            {
                var width=s.Constraints.OfType<OffsetXConstraint>().Single();editor.SelectedConstraint=editor.Constraints.Single(c=>c.Id==width.Id);editor.DimensionValue=60;editor.ApplyDimensionCommand.Execute(null);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Equal(18000,Assert.Single(document.PreviewScene!.Items).Geometry.VolumeMm3,3);Assert.Same(before,session.Snapshot);
                editor.Name="Changed after preview";Assert.False(editor.CanConfirm);Assert.Null(document.PreviewScene);Assert.Equal(count,assets.Count);
                await editor.PreviewCommand.ExecuteAsync(null);editor.CancelCommand.Execute(null);
            }
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        finally{document.Detach();}
    }
    [Fact] public async Task UnappliedNumericInputInvalidatesAndIsConsumedByPreview()
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),kernel,new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var s=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(s,solver));s=session.Snapshot.Sketches[s.Id];var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,s,false);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm);
            var width=s.Constraints.OfType<OffsetXConstraint>().Single();editor.SelectedConstraint=editor.Constraints.Single(c=>c.Id==width.Id);Assert.True(editor.CanConfirm);
            editor.DimensionValue=70;Assert.False(editor.CanConfirm);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            Assert.Equal(70,editor.Sketch.Points[1].Position.X-editor.Sketch.Points[0].Position.X,6);
            editor.Name="";await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);
        }
        finally{document.Detach();}
    }
    [Fact] public async Task ConflictAndExternalDocumentChangesDisableConfirmation()
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),kernel,new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var s=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(s,solver));s=session.Snapshot.Sketches[s.Id];var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,s,false);
            editor.SelectedEntities.Add(s.Points[0].Id);editor.SelectedEntities.Add(s.Points[1].Id);editor.ConstraintKind="OffsetX";editor.DimensionValue=99;editor.AddConstraintCommand.Execute(null);
            var before=session.Snapshot;await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.NotEmpty(editor.Report!.ConflictingConstraints);Assert.Same(before,session.Snapshot);
            editor.UndoCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            await session.ExecuteAsync(new EditDocumentCommand("Other edit",d=>d with{Name="Changed"}));Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);Assert.Null(document.PreviewScene);
        }
        finally{document.Detach();}
    }
    [Fact] public async Task ClosingDuringSolveCancelsWithoutPublishingOrLosingInputAssets()
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());var solver=new WaitingSolver();
        try
        {
            var editor=new SketchEditorViewModel(document,kernel,solver,SketchTestData.Rectangle(part),true);
            var preview=editor.PreviewCommand.ExecuteAsync(null);await solver.Started.Task;await editor.DisposeAsync();await preview;
            Assert.Same(initial,session.Snapshot);Assert.False(editor.CanConfirm);Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }
    [Fact] public async Task ModelingProfileChoicePreservesAssociationAndIgnoresFrozenCoordinateInputs()
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),kernel,new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var s=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(s,solver));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedSketchId=s.Id;document.StartSketchFeature("Extrude");Assert.NotNull(document.SelectedSketchProfile);document.SizeZ=10;document.PositionX=1000;
            await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);await document.ConfirmCommand.ExecuteAsync(null);
            var feature=Assert.Single(session.Snapshot.Features.Values);Assert.Equal(s.Id,feature.SketchSource!.SketchId);Assert.Equal(0,((ExtrudeRecipe)feature.Recipe).Placement.Translation.X);
            document.EditFeature(feature.Id);document.SizeZ=15;await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);await document.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal(18000,Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3,3);Assert.NotNull(session.Snapshot.Features[feature.Id].SketchSource);
        }
        finally{document.Detach();}
    }
    [Fact] public void DraftSharesEndpointsAndEntityDeletionRemovesAffectedConstraintsWithUndo()
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Draft",RigidTransform3d.Identity));draft.AddRectangle(new(0,0),new(20,10));
        var rectangle=draft.Value;Assert.Single(SketchLoops.Find(rectangle));draft.AddLine(new(0,0),new(-10,0),rectangle.Points[0].Id);Assert.Equal(5,draft.Value.Points.Length);Assert.Empty(SketchLoops.Find(draft.Value));
        draft.Undo();Assert.Same(rectangle,draft.Value);draft.RemoveEntities([rectangle.Points[0].Id]);draft.Value.Validate();Assert.Equal(2,draft.Value.Lines.Length);Assert.DoesNotContain(draft.Value.Constraints,c=>SketchConstraintEditing.Targets(c).Contains(rectangle.Points[0].Id));
        draft.Undo();Assert.Same(rectangle,draft.Value);draft.Redo();Assert.Equal(2,draft.Value.Lines.Length);
    }
    [Fact] public void CircleConstructionAndMultipleLoopsHaveExplicitFeatureEligibility()
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Draft",RigidTransform3d.Identity));draft.AddCircle(new(0,0),5,construction:true);Assert.Empty(SketchLoops.Find(draft.Value));
        draft.AddRectangle(new(20,0),new(30,10));draft.AddRectangle(new(40,0),new(50,10));Assert.Equal(2,SketchLoops.Find(draft.Value).Length);
        Assert.True(draft.Value.Circles.Single().IsConstruction);Assert.IsType<RadiusConstraint>(draft.Value.Constraints[0]);
    }
    private sealed class WaitingSolver:ISketchConstraintSolver
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public string Version=>"waiting";
        public async Task<SketchSolveReport> SolveAsync(CadSketch s,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {Started.TrySetResult();await Task.Delay(Timeout.Infinite,cancellationToken);throw new InvalidOperationException();}
    }
}
