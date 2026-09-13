using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class LocalFeatureTests
{
    public static IEnumerable<object[]> Edges()=>from first in Enum.GetValues<BoxBoundary>()
        from second in Enum.GetValues<BoxBoundary>() where (int)first/2<(int)second/2
        from operation in Enum.GetValues<LocalFeatureOperation>() select new object[]{first,second,operation};
    [Theory][MemberData(nameof(Edges))]
    public async Task EverySemanticBoxEdgeUsesItsActualNativeEdge(BoxBoundary first,BoxBoundary second,LocalFeatureOperation operation)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var box=new BoxRecipe(10,20,30,new(new(4,5,6),Quaterniond.FromAxisAngle(new(2,3,1),0.8)));
        using(var source=await kernel.EvaluateAsync(box,assets))
        using(var result=await kernel.EvaluateAsync(new LocalFeatureRecipe(source.Geometry,box,first,second,operation,1),assets))
        {
            int axis=3-(int)first/2-(int)second/2;double length=new[]{10d,20,30}[axis];
            double removed=length*(operation==LocalFeatureOperation.Chamfer?0.5:1-Math.PI/4);
            Assert.Equal(6000-removed,result.Geometry.VolumeMm3,4);
            Assert.NotNull(result.TopologyHistory);
            using var native=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(native.IsValid);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LockedOutputStaleSelectionAndUnsupportedSourceLeaveHistoryUnchanged()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var f=session.Snapshot.Features.Values.Single();
        var edge=TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.YMin,BoxBoundary.ZMax);var layer=session.Snapshot.Layers.Values.Single();
        await session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsLocked=true}));var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new LocalFeatureCommand(edge,LocalFeatureOperation.Fillet,1)));Assert.Same(before,session.Snapshot);
        await session.UndoAsync();await session.ExecuteAsync(new RecomputeCommand(f.Id,new BoxRecipe(20,20,30,RigidTransform3d.Identity)));before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new LocalFeatureCommand(edge,LocalFeatureOperation.Fillet,1)));Assert.Same(before,session.Snapshot);
        edge=TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.YMin,BoxBoundary.ZMax);
        await session.ExecuteAsync(new LocalFeatureCommand(edge,LocalFeatureOperation.Fillet,1));var output=session.Snapshot.Bodies.Values.Single();before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new LocalFeatureCommand(edge with{FeatureId=output.Producer!.Value,OutputBodyId=output.Id,OriginRevision=output.Geometry.Revision},LocalFeatureOperation.Fillet,1)));
        Assert.Same(before,session.Snapshot);
    }
    [Theory][InlineData(LocalFeatureOperation.Fillet)][InlineData(LocalFeatureOperation.Chamfer)]
    public async Task SelectedEdgeChangesOnlyOneEdgeAndRecomputesWithExactHistory(LocalFeatureOperation operation)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher()))
        {
            var box=new BoxRecipe(10,20,30,new(new(7,8,9),Quaterniond.FromAxisAngle(new(1,2,3),0.6)));
            await session.ExecuteAsync(new AddBodyCommand(box,"Box"));var source=session.Snapshot.Features.Values.Single();
            var edge=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.XMax,BoxBoundary.ZMax);var before=session.Snapshot;
            await session.ExecuteAsync(new LocalFeatureCommand(edge,operation,2));var after=session.Snapshot;var body=Assert.Single(after.Bodies.Values);
            double removed=operation==LocalFeatureOperation.Chamfer?40:20*4*(1-Math.PI/4);
            Assert.Equal(6000-removed,body.Geometry.VolumeMm3,4);after.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            await session.ExecuteAsync(new RecomputeCommand(source.Id,box with{Y=40}));
            Assert.Equal(12000-removed*2,Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3,4);
            var path=files.PathFor("local.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();
            Assert.IsType<LocalFeatureRecipe>(loaded.Snapshot.Features[body.Producer!.Value].Recipe);
        }
        Assert.Equal(0,assets.Count);
    }
}
