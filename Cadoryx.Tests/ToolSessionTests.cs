using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Xunit;
namespace Cadoryx.Tests;
public sealed class ToolSessionTests
{
    [Theory]
    [InlineData(DocumentWorkPlaneKind.XZ)]
    [InlineData(DocumentWorkPlaneKind.YZ)]
    public async Task MouseBoxUsesSelectedPlaneForSizeAndNormal(DocumentWorkPlaneKind kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Plane"),assets,kernel,new InlineSessionDispatcher());
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var plane=new DocumentWorkPlaneSettings(kind,12);
            await vm.SetWorkPlaneAsync(plane);
            vm.StartTool("Box");
            vm.ConstructionPointer(plane.ToWorld(10,20),100,100,2,true);
            var basePoint=vm.ConstructionPointer(plane.ToWorld(-5,35),150,120,2,true);
            var box=Assert.IsType<BoxRecipe>(basePoint.Ghost);
            Assert.Equal(15,box.X,10);Assert.Equal(15,box.Y,10);
            Assert.True((plane.ToWorld(-5,20)-box.Placement.Translation).Length<1e-9);
            Assert.Equal(1,box.Placement.Rotation.Rotate(Vector3d.UnitZ).Dot(plane.Normal),10);
            Assert.Equal(1,box.Placement.Rotation.Rotate(new Vector3d(1,0,0)).Dot(plane.Rotation.Rotate(new Vector3d(1,0,0))),10);
            var height=Assert.IsType<BoxRecipe>(vm.ConstructionPointer(plane.ToWorld(-5,35),150,100,2,false).Ghost);
            Assert.Equal(10,height.Z);
            Assert.True(vm.ConstructionPointer(plane.ToWorld(-5,35),150,100,2,true).PreviewNow);
            Assert.Empty(session.Snapshot.Features);
            vm.StartTool("Cylinder");
            vm.ConstructionPointer(plane.ToWorld(1,2),100,100,2,true);
            var cylinder=Assert.IsType<CylinderRecipe>(vm.ConstructionPointer(plane.ToWorld(4,6),120,120,2,false).Ghost);
            Assert.Equal(5,cylinder.Radius,10);
            Assert.Equal(1,cylinder.Placement.Rotation.Rotate(Vector3d.UnitZ).Dot(plane.Normal),10);
            vm.StartTool("Extrude");
            vm.ConstructionPointer(plane.ToWorld(2,3),100,100,2,true);
            var extrusion=Assert.IsType<ExtrudeRecipe>(vm.ConstructionPointer(plane.ToWorld(2,3),100,60,2,false).Ghost);
            Assert.Equal(1,extrusion.Placement.Rotation.Rotate(Vector3d.UnitZ).Dot(plane.Normal),10);
            vm.StartTool("Revolve");
            vm.ConstructionPointer(plane.ToWorld(2,3),100,100,2,true);
            var revolution=Assert.IsType<RevolveRecipe>(vm.ConstructionPointer(plane.ToWorld(2,3),280,100,2,false).Ghost);
            Assert.Equal(1,revolution.Placement.Rotation.Rotate(Vector3d.UnitZ).Dot(plane.Normal),10);
        }
        finally{await vm.StopToolsAsync();vm.Detach();}
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task MouseConstructionMapsClicksToPrimitiveDimensionsAndViewportSettings()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Mouse"),assets,kernel,new InlineSessionDispatcher());
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await vm.SetGridAsync(visible:false,spacingMm:5,snap:true);
            Assert.False(vm.GridVisible);Assert.True(vm.SnapToGrid);Assert.Equal(5,vm.GridSpacingMm);
            vm.StartTool("Box");Assert.True(vm.IsViewportConstructing);
            vm.ConstructionPointer(new(10,20,0),100,100,4,true);
            var baseBox=vm.ConstructionPointer(new(-5,35,0),160,140,4,true);
            Assert.IsType<BoxRecipe>(baseBox.Ghost);Assert.Equal(-5,vm.PositionX);Assert.Equal(15,vm.SizeX);Assert.Equal(15,vm.SizeY);
            var height=vm.ConstructionPointer(new(-5,35,0),160,100,4,false);
            Assert.IsType<BoxRecipe>(height.Ghost);Assert.Equal(10,vm.SizeZ);
            Assert.True(vm.ConstructionPointer(new(-5,35,0),160,100,4,true).PreviewNow);
            Assert.False(vm.IsViewportConstructing);Assert.Empty(session.Snapshot.Features);
            vm.StartTool("Cylinder");vm.ConstructionPointer(new(30,40,0),100,100,2,true);
            vm.ConstructionPointer(new(33,44,0),130,120,2,true);
            Assert.Equal(5,vm.SizeX);Assert.Equal(30,vm.PositionX);Assert.Equal(40,vm.PositionY);
            vm.CancelViewportConstruction();Assert.False(vm.IsViewportConstructing);
            vm.StartTool("Extrude");vm.ConstructionPointer(new(2,3,0),100,100,2,true);
            var extrusionGhost=vm.ConstructionPointer(new(2,3,0),100,60,2,false);
            Assert.Equal(20,vm.SizeZ);Assert.False(extrusionGhost.PreviewNow);
            var extrusion=Assert.IsType<ExtrudeRecipe>(extrusionGhost.Ghost);
            Assert.Equal(20,extrusion.Distance);Assert.Equal(new Vector3d(2,3,0),extrusion.Placement.Translation);
            Assert.True(vm.ConstructionPointer(new(2,3,0),100,60,2,true).PreviewNow);
            vm.StartTool("Revolve");vm.ConstructionPointer(new(2,3,0),100,100,2,true);
            var rotationGhost=vm.ConstructionPointer(new(2,3,0),280,100,2,false);
            Assert.Equal(90,vm.AngleDegrees);Assert.False(rotationGhost.PreviewNow);
            var rotation=Assert.IsType<RevolveRecipe>(rotationGhost.Ghost);
            Assert.Equal(Math.PI/2,rotation.AngleRadians,10);
            vm.ProfilePoints.Clear();
            Assert.Null(vm.ConstructionPointer(new(2,3,0),285,100,2,false).Ghost);
            Assert.True(vm.IsViewportConstructing);
        }
        finally{await vm.StopToolsAsync();vm.Detach();}
        Assert.Equal(0,assets.Count);
    }
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
