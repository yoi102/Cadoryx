using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class SketchNativeProjectionEditorTests
{
    private static readonly RigidTransform3d Plane=new(new(1,2,20),Quaterniond.FromAxisAngle(new(1,0,0),.4));
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExactPickerCreatesLinkedOrIndependentDraftAndParentPreviewIsCancelable(bool linked)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Source",part));
        var before=session.Snapshot;var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var parent=new SketchEditorViewModel(doc,kernel,solver,CadSketch.Create(part,"Projected",Plane),true);
            await using(var picker=parent.CreateNativeProjectionPicker())
            {
                picker.Linked=linked;var selection=SketchEllipseProjectionTests.Pick(before,assets,CurveGeometryType.Circle);
                await picker.PickExactAsync(selection);Assert.True(picker.CanUse,picker.Status);Assert.Equal(Plane,parent.Sketch.Plane);
                Assert.Empty(parent.Sketch.Ellipses);await picker.UseCommand.ExecuteAsync(null);Assert.Single(parent.Sketch.Ellipses);Assert.Equal(linked?1:0,parent.Sketch.NativeProjections.Length);
                Assert.Same(before,session.Snapshot);Assert.Null(doc.PreviewScene);
            }
            await parent.PreviewCommand.ExecuteAsync(null);Assert.True(parent.CanConfirm,parent.Status);Assert.Same(before,session.Snapshot);
            parent.CancelCommand.Execute(null);Assert.Same(before,session.Snapshot);Assert.Null(doc.PreviewScene);
            await parent.PreviewCommand.ExecuteAsync(null);await parent.ConfirmCommand.ExecuteAsync(null);Assert.Single(session.Snapshot.Sketches);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Single(session.Snapshot.Sketches);
        }
        finally{doc.Detach();}
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task PendingEllipseNumericInputInvalidatesAndIsConsumedByPreview()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var parent=new SketchEditorViewModel(doc,kernel,new ManagedSketchConstraintSolver(),CadSketch.Create(part,"Conic",Plane),true);
            parent.AddEllipseCommand.Execute(null);parent.Select(parent.Sketch.Ellipses[0].Id);await parent.PreviewCommand.ExecuteAsync(null);Assert.True(parent.CanConfirm,parent.Status);
            parent.EllipseCosineX=17;Assert.False(parent.CanConfirm);Assert.Null(doc.PreviewScene);
            await parent.PreviewCommand.ExecuteAsync(null);Assert.True(parent.CanConfirm,parent.Status);Assert.Equal(17,parent.Sketch.Ellipses[0].Geometry.Cosine.X);
            Assert.Equal(Plane,parent.Sketch.Plane);parent.UndoCommand.Execute(null);Assert.Equal(10,parent.Sketch.Ellipses[0].Geometry.Cosine.X);parent.RedoCommand.Execute(null);Assert.Equal(17,parent.Sketch.Ellipses[0].Geometry.Cosine.X);
        }
        finally{doc.Detach();}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedPlaneRequiresExplicitReselectionOrDetachBeforeParentCanConfirm(bool reselect)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Source",part));
        var before=session.Snapshot;var selection=SketchEllipseProjectionTests.Pick(before,assets,CurveGeometryType.Circle);var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var parent=new SketchEditorViewModel(doc,kernel,new ManagedSketchConstraintSolver(),CadSketch.Create(part,"Draft",Plane),true);
            await using(var picker=parent.CreateNativeProjectionPicker()){await picker.PickExactAsync(selection);await picker.UseCommand.ExecuteAsync(null);}
            var id=parent.Sketch.Ellipses[0].Id;parent.Select(id);parent.OriginZ=40;await parent.PreviewCommand.ExecuteAsync(null);
            Assert.False(parent.CanConfirm);Assert.Same(before,session.Snapshot);Assert.Null(doc.PreviewScene);
            if(reselect)
            {
                await using var picker=parent.CreateNativeProjectionPicker();Assert.True(picker.CanReplace);picker.ReplaceSelected=true;
                await picker.PickExactAsync(selection);Assert.True(picker.CanUse,picker.Status);await picker.UseCommand.ExecuteAsync(null);
                Assert.Equal(id,Assert.Single(parent.Sketch.Ellipses).Id);Assert.Equal(40,Assert.Single(parent.Sketch.NativeProjections).TargetPlane.Translation.Z);
            }
            else{parent.DetachProjectionCommand.Execute(null);Assert.Empty(parent.Sketch.NativeProjections);}
            await parent.PreviewCommand.ExecuteAsync(null);Assert.True(parent.CanConfirm,parent.Status);await parent.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal(40,Assert.Single(session.Snapshot.Sketches.Values).Plane.Translation.Z);await session.UndoAsync();Assert.Same(before,session.Snapshot);
        }
        finally{doc.Detach();}
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateNativeResultCannotEnterDraftAfterGenerationChangeOrClose(bool close)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();var kernel=new GatedKernel(native);
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Source",part));
        var selection=SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.Circle);var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        var parent=new SketchEditorViewModel(doc,kernel,new ManagedSketchConstraintSolver(),CadSketch.Create(part,"Draft",Plane),true);var picker=parent.CreateNativeProjectionPicker();Task? disposal=null;
        try
        {
            var pending=picker.PickExactAsync(selection);await kernel.Picked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if(close){disposal=parent.DisposeAsync().AsTask();await session.DisposeAsync();Assert.True(assets.Count>0);Assert.False(disposal.IsCompleted);}
            else await session.ExecuteAsync(new EditDocumentCommand("Changed",s=>s with{Name="Changed"}));
            kernel.Release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(15));if(disposal is not null)await disposal;
            Assert.Empty(parent.Sketch.Ellipses);Assert.Empty(parent.Sketch.NativeProjections);Assert.False(picker.CanUse);
        }
        finally{kernel.Release.TrySetResult();await picker.DisposeAsync();await parent.DisposeAsync();doc.Detach();}
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }
    private sealed class GatedKernel(OcctGeometryKernel native):IGeometryKernel,ISketchEdgeProjector
    {
        public TaskCompletionSource Picked {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;
        public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>native.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public Task ConfirmEdgeProjectionAsync(ExactSketchEdgeProjection value,IAssetStore assets,CancellationToken token=default)=>native.ConfirmEdgeProjectionAsync(value,assets,token);
        public async Task<ExactSketchEdgeProjection> ProjectEdgeAsync(DocumentSnapshot doc,DefinitionId part,ExactTopologySelection selection,RigidTransform3d plane,IAssetStore assets,CancellationToken token=default)
        {
            var result=await native.ProjectEdgeAsync(doc,part,selection,plane,assets,CancellationToken.None);
            Picked.TrySetResult();await Release.Task;return result;
        }
    }
}
