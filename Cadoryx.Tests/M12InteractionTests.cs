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

public sealed class M12InteractionTests
{
    [Fact]
    public void WorldDragUsesParentRotationAndRejectsInvalidContext()
    {
        var parent=new RigidTransform3d(new(5,3,0),
            Quaterniond.FromAxisAngle(new(0,0,1),Math.PI/2));
        var local=RigidTransform3d.Translate(2,3,4);
        var moved=OccurrenceDrag.MoveLocal(parent,local,new(10,0,0));
        Assert.InRange(moved.Translation.X,1.999999,2.000001);
        Assert.InRange(moved.Translation.Y,-7.000001,-6.999999);
        Assert.Equal(local.Rotation,moved.Rotation);
        Assert.Throws<CadValidationException>(()=>OccurrenceDrag.MoveLocal(parent,local,new(double.NaN,0,0)));
    }

    [Fact]
    public async Task DirectPlacementIsOneUndoableEdit()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Drag"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,5,6,RigidTransform3d.Identity),"Part"));
        var path=Assert.Single(session.Snapshot.EnumerateOccurrences()).Path;
        Assert.True(OccurrenceDrag.CanMove(session.Snapshot,path));
        var occurrence=Assert.Single(session.Snapshot.EnumerateOccurrences());
        var fixedMate=new AssemblyConstraint(AssemblyConstraintId.New(),"Fixed",
            AssemblyConstraintKind.Fixed,path,occurrence.DefinitionId,FixedWorld:occurrence.WorldTransform);
        var constrained=session.Snapshot with{AssemblyConstraints=session.Snapshot.AssemblyConstraints.Add(fixedMate.Id,fixedMate)};
        Assert.False(OccurrenceDrag.CanMove(constrained,path));
        Assert.True(OccurrenceDrag.CanMove(constrained with{AssemblyConstraints=constrained.AssemblyConstraints.SetItem(
            fixedMate.Id,fixedMate with{IsEnabled=false})},path));
        var old=OccurrencePlacement.Resolve(session.Snapshot,path).Slot.LocalTransform;
        var next=OccurrenceDrag.MoveLocal(RigidTransform3d.Identity,old,new(13,0,0));
        await session.ExecuteAsync(DocumentEdits.MoveOccurrence(path,next));
        Assert.Equal(next,OccurrencePlacement.Resolve(session.Snapshot,path).Slot.LocalTransform);
        await session.UndoAsync();
        Assert.Equal(old,OccurrencePlacement.Resolve(session.Snapshot,path).Slot.LocalTransform);
    }

    [Fact]
    public void RevolveAngleHandleUsesWorldTangent()
    {
        var profile=new SketchProfile([new(2,0),new(8,0),new(2,5)]);
        var recipe=new RevolveRecipe(profile,Math.PI/2,RigidTransform3d.Identity);
        var handle=Assert.Single(SolidDimensionHandles.Describe(recipe,RigidTransform3d.Identity));
        Assert.Equal(SolidDimension.RevolveAngle,handle.Dimension);
        Assert.InRange((handle.WorldPoint-new Vector3d(0,8,0)).Length,0,1e-10);
        Assert.InRange((handle.WorldAxis-new Vector3d(-8,0,0)).Length,0,1e-10);
        Assert.Equal(Math.PI/2,handle.Value,10);
    }

    [Fact]
    public async Task RevolveDragChangesOnlyCandidateUntilConfirmed()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Revolve"),assets,kernel,new InlineSessionDispatcher());
        var profile=new SketchProfile([new(2,0),new(8,0),new(2,5)]);
        await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(profile,Math.PI/2,RigidTransform3d.Identity),"Turn"));
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var feature=Assert.Single(session.Snapshot.Features.Values);
            double before=feature.Result.VolumeMm3;
            vm.EditFeature(feature.Id);
            Assert.Equal(SolidDimension.RevolveAngle,
                Assert.Single(SolidDimensionHandles.Describe(vm.SolidHandleRecipe()!,RigidTransform3d.Identity)).Dimension);
            vm.SetSolidHandleValue(SolidDimension.RevolveAngle,Math.PI);
            Assert.Equal(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
            await vm.PreviewCommand.ExecuteAsync(null);
            Assert.True(vm.HasPreview,vm.ToolStatus);
            Assert.Equal(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
            await vm.ConfirmCommand.ExecuteAsync(null);
            Assert.NotEqual(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
            await session.UndoAsync();
            Assert.Equal(before,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
        }
        finally{await vm.StopToolsAsync();vm.Detach();}
    }
}
