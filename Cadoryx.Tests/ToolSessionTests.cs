using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;
namespace Cadoryx.Tests;
public sealed class ToolSessionTests
{
    [Fact] public async Task PreviewDoesNotDirtyDocumentAndCancelReleasesAssets()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Preview"),assets,kernel,new InlineSessionDispatcher(),"existing.cadoryx");
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var original=session.Snapshot;await vm.PreviewCommand.ExecuteAsync(null);
            Assert.True(vm.HasPreview);Assert.Same(original,session.Snapshot);Assert.False(session.IsDirty);Assert.Single(vm.PreviewScene!.Items);
            vm.CancelCommand.Execute(null);Assert.False(vm.HasPreview);Assert.Equal(0,assets.Count);Assert.False(vm.ConfirmCommand.CanExecute(null));
            await vm.PreviewCommand.ExecuteAsync(null);vm.SizeX=80;Assert.False(vm.HasPreview);Assert.Equal(0,assets.Count);
        }
        finally{await vm.StopToolsAsync();vm.Detach();}
    }
    [Fact] public async Task BooleanPreviewShowsCandidateAndConfirmUsesExactPreparedAsset()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Boolean"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"A"));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(5,10,10,RigidTransform3d.Identity),"B"));
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            vm.Selection.Replace(vm.Scene.Items.OrderByDescending(i=>i.Geometry.VolumeMm3).Select(i=>new SelectionTarget(i.Path,i.BodyId,i.Geometry.Revision)));
            await vm.BooleanPreviewCommand.ExecuteAsync("Cut");Assert.True(vm.HasPreview);
            Assert.Equal(2,session.Snapshot.Bodies.Count);var prepared=Assert.Single(vm.PreviewScene!.Items).Geometry;
            Assert.Equal(500,prepared.VolumeMm3,5);await vm.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal(prepared,Assert.Single(session.Snapshot.Bodies.Values).Geometry);
            await session.UndoAsync();Assert.Equal(2,session.Snapshot.Bodies.Count);
        }
        finally{await vm.StopToolsAsync();vm.Detach();}
    }
    [Fact] public async Task FeatureEditingKeepsPlacementRotation()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Rotated"),assets,kernel,new InlineSessionDispatcher());
        var placement=new RigidTransform3d(new(10,20,30),Quaterniond.FromAxisAngle(Vector3d.UnitZ,0.7));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,placement),"Box"));
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var feature=session.Snapshot.Features.Keys.Single();vm.EditFeature(feature);vm.SizeX=15;
            await vm.PreviewCommand.ExecuteAsync(null);await vm.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal(placement,Assert.IsType<BoxRecipe>(session.Snapshot.Features[feature].Recipe).Placement);
        }
        finally{await vm.StopToolsAsync();vm.Detach();}
    }
}
