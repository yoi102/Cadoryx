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
    [Fact] public async Task TwoSeparateEdgesAndLinearRadiusProduceValidHistoryAndSave()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Multi edge"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var source=session.Snapshot.Features.Values.Single();var edge=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.XMin,BoxBoundary.YMin);
            int other=LocalFeatureRecipe.EdgeBit(BoxBoundary.XMax,BoxBoundary.YMax);var before=session.Snapshot;
            await session.ExecuteAsync(new LocalFeatureCommand(edge,LocalFeatureOperation.Fillet,.5,additionalEdges:other));
            var multi=session.Snapshot;var local=multi.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            Assert.Equal(other,((LocalFeatureRecipe)local.Recipe).AdditionalEdges);
            Assert.Equal(6000-2*30*(1-Math.PI/4)*.25,local.Result.VolumeMm3,3);
            Assert.NotNull(local.TopologyHistory);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(multi,session.Snapshot);
            await session.ExecuteAsync(new RecomputeCommand(local.Id,((LocalFeatureRecipe)local.Recipe) with{AdditionalEdges=0,EndRadius=2}));
            var variable=session.Snapshot.Features[local.Id];Assert.NotNull(variable.TopologyHistory);
            Assert.InRange(variable.Result.VolumeMm3,5800,6000);
            using(var native=OcctGeometryBridge.ReadShape(variable.Result,assets))Assert.True(native.IsValid);
            var path=files.PathFor("local-variable.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
            Assert.Equal(2,((LocalFeatureRecipe)loaded.Snapshot.Features[local.Id].Recipe).EndRadius);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task InvalidMultiEdgeAndVariableRadiusCombinationsAreRejected()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var box=new BoxRecipe(10,20,30,RigidTransform3d.Identity);
        using(var source=await kernel.EvaluateAsync(box,assets))
        {
            LocalFeatureRecipe Recipe(int mask=0,double? end=null,LocalFeatureOperation operation=LocalFeatureOperation.Fillet)
                =>new(source.Geometry,box,BoxBoundary.XMin,BoxBoundary.YMin,operation,1,AdditionalEdges:mask,EndRadius:end);
            Assert.Throws<CadValidationException>(()=>Recipe(LocalFeatureRecipe.EdgeBit(BoxBoundary.XMin,BoxBoundary.YMin)).Validate());
            Assert.Throws<CadValidationException>(()=>Recipe(0x1000).Validate());
            Assert.Throws<CadValidationException>(()=>Recipe(-1).Validate());
            Assert.Throws<CadValidationException>(()=>Recipe(0,double.NaN).Validate());
            Assert.Throws<CadValidationException>(()=>Recipe(0,2,LocalFeatureOperation.Chamfer).Validate());
            Assert.Throws<CadValidationException>(()=>Recipe(LocalFeatureRecipe.EdgeBit(BoxBoundary.XMax,BoxBoundary.YMax),2).Validate());
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task TwoDistanceChamferUsesBothValuesAndPreservesHistory()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var box=new BoxRecipe(10,20,30,RigidTransform3d.Identity);
        using(var source=await kernel.EvaluateAsync(box,assets))
        using(var result=await kernel.EvaluateAsync(new LocalFeatureRecipe(source.Geometry,box,BoxBoundary.YMin,BoxBoundary.ZMax,LocalFeatureOperation.Chamfer,2,3),assets))
        {
            Assert.Equal(5970,result.Geometry.VolumeMm3,4);
            Assert.NotNull(result.TopologyHistory);
            using var native=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(native.IsValid);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task TwoSeparatedChamfersUseBothEdges()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var box=new BoxRecipe(10,20,30,RigidTransform3d.Identity);
        using(var source=await kernel.EvaluateAsync(box,assets))
        using(var result=await kernel.EvaluateAsync(new LocalFeatureRecipe(source.Geometry,box,BoxBoundary.XMin,BoxBoundary.YMin,
            LocalFeatureOperation.Chamfer,1,AdditionalEdges:LocalFeatureRecipe.EdgeBit(BoxBoundary.XMax,BoxBoundary.YMax)),assets))
        {
            Assert.Equal(5970,result.Geometry.VolumeMm3,3);
            Assert.NotNull(result.TopologyHistory);
            using var native=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(native.IsValid);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(LocalFeatureOperation.Fillet,2)][InlineData(LocalFeatureOperation.Chamfer,0)]
    [InlineData(LocalFeatureOperation.Chamfer,-1)][InlineData(LocalFeatureOperation.Chamfer,double.NaN)]
    public async Task InvalidSecondDistanceIsRejectedBeforeNativeEvaluation(LocalFeatureOperation operation,double second)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Invalid chamfer"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();var before=session.Snapshot;var count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new LocalFeatureCommand(
                TopologyReference.Box(before,box.Id,BoxBoundary.YMin,BoxBoundary.ZMax),operation,2,second)));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
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
