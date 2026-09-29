using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class M8SolidHandleTests
{
    [Fact]
    public void HandlesRespectFeatureAndOccurrenceRotationAndScreenProjection()
    {
        var rotation=Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2);
        var box=new BoxRecipe(10,20,30,new(new(4,5,6),rotation));
        var occurrence=RigidTransform3d.Translate(100,0,0);
        var handles=SolidDimensionHandles.Describe(box,occurrence);
        Assert.Equal(3,handles.Count);
        var x=Assert.Single(handles,h=>h.Dimension==SolidDimension.BoxX);
        Assert.InRange((x.WorldPoint-new Vector3d(94,15,21)).Length,0,1e-10);
        Assert.InRange((x.WorldAxis-new Vector3d(0,1,0)).Length,0,1e-10);
        Assert.Equal(15,SolidDimensionHandles.DragValue(10,10,20,20,20,(10,20),(12,20)),10);
        Assert.Equal(20,SolidDimensionHandles.DragValue(10,10,20,20,20,(10,20),(12,20),10),10);
        Assert.Throws<ArgumentOutOfRangeException>(()=>SolidDimensionHandles.DragValue(10,0,0,5,5,(0,0),(0,0)));
        var cylinder=new CylinderRecipe(7,12,RigidTransform3d.Identity);
        Assert.Equal([SolidDimension.CylinderRadius,SolidDimension.CylinderHeight],
            SolidDimensionHandles.Describe(cylinder,RigidTransform3d.Identity).Select(h=>h.Dimension));
        var profile=new SketchProfile([new(0,0),new(4,0),new(0,4)]);
        Assert.Equal(SolidDimension.ExtrudeDistance,Assert.Single(SolidDimensionHandles.Describe(
            new ExtrudeRecipe(profile,8,RigidTransform3d.Identity),RigidTransform3d.Identity)).Dimension);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleEditUsesExistingPreviewAndUndoTransaction(bool cylinder)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Handles"),assets,kernel,new InlineSessionDispatcher());
        GeometryRecipe initial=cylinder?new CylinderRecipe(5,10,RigidTransform3d.Identity):
            new BoxRecipe(10,20,30,RigidTransform3d.Identity);
        await session.ExecuteAsync(new AddBodyCommand(initial,"Editable"));
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var feature=Assert.Single(session.Snapshot.Features.Values);
            vm.EditFeature(feature.Id);
            Assert.NotNull(vm.SolidHandleRecipe());
            double before=feature.Result.VolumeMm3;
            var dimension=cylinder?SolidDimension.CylinderRadius:SolidDimension.BoxX;
            vm.SetSolidHandleValue(dimension,15);
            Assert.False(vm.HasPreview);
            Assert.Equal(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
            await vm.PreviewCommand.ExecuteAsync(null);
            Assert.True(vm.HasPreview,vm.ToolStatus);
            Assert.Equal(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
            vm.SetSolidHandleValue(dimension,cylinder?5:10);
            Assert.False(vm.HasPreview);
            vm.SetSolidHandleValue(dimension,15);
            await vm.PreviewCommand.ExecuteAsync(null);
            await vm.ConfirmCommand.ExecuteAsync(null);
            Assert.NotEqual(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
            await session.UndoAsync();
            Assert.Equal(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
        }
        finally{await vm.StopToolsAsync();vm.Detach();}
    }
}
