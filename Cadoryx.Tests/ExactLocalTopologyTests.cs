using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using OcctSharp;
using MessagePack;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using LocalFeatureOperation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.Tests;

public sealed class ExactLocalTopologyTests
{
    [Fact] public async Task GeneratedEdgeRequiresExactAssetAndFreezesAfterUpstreamChange()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Generated edge"),assets,kernel,new InlineSessionDispatcher()))
        {
            var boxRecipe=new BoxRecipe(10,20,30,RigidTransform3d.Identity);
            await session.ExecuteAsync(new AddBodyCommand(boxRecipe,"Box"));var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),
                LocalFeatureOperation.Fillet,1));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            using var shape=OcctGeometryBridge.ReadShape(local.Result,assets);using var map=RepairSnapshot.Create(shape);
            var generated=map.Topology.Where(t=>t.Kind==ShapeKind.Edge).Where(t=>
            {
                using var candidate=map.CopySubshape(t.Selection);
                return BoxTopology.Classify(candidate,boxRecipe,TopologyKind.Edge).Count==0;
            }).Select(t=>t.Selection.Index).ToArray();
            Assert.NotEmpty(generated);
            ExactTopologySelection Pick(int index)=>new(session.Snapshot.Id,local.Id,local.Result.Revision,local.Result.AssetId,
                map.Fingerprint,index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
            var before=session.Snapshot;var count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>new ExactLocalFeatureCommand(Pick(generated[0]) with{Fingerprint=new string('0',64)},
                LocalFeatureOperation.Fillet,.2).PrepareAsync(new(before,session.Generation,assets,kernel),default));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            FeatureDefinition? created=null;
            foreach(var index in generated)
            {
                try
                {
                    await session.ExecuteAsync(new ExactLocalFeatureCommand(Pick(index),LocalFeatureOperation.Fillet,.2));
                    created=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);break;
                }
                catch(CadValidationException){ }
            }
            Assert.NotNull(created);
            Assert.Null(created.TopologyBinding!.Origin);Assert.NotNull(created.TopologyBinding.ExactEdge);
            Assert.Equal(local.Id,created.Inputs.Single());Assert.NotNull(created.TopologyHistory);
            var current=session.Snapshot;
            var path=files.PathFor("generated.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
                Assert.Equal(created.TopologyBinding,loaded.Snapshot.Features[created.Id].TopologyBinding);
            var bad=files.PathFor("bad-generated.cadoryx");await session.SaveAsync(new CadDocumentStorage(),bad);
            FormatEvolutionTests.RewriteSection(bad,"feature-bindings",bytes=>
            {
                var data=MessagePackSerializer.Deserialize<PackFeatureBindings>(bytes);
                return MessagePackSerializer.Serialize(data with{Bindings=data.Bindings.Select(b=>b with
                    {ExactEdge=b.ExactEdge! with{Fingerprint="bad"}}).ToArray()});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(bad,assets));
            await session.ExecuteAsync(new RecomputeCommand(box.Id,boxRecipe with{X=11}));
            Assert.True(session.Snapshot.Features[created.Id].IsStale);
            var changed=session.Snapshot;var refreshed=changed.Features[local.Id];
            using(var currentShape=OcctGeometryBridge.ReadShape(refreshed.Result,assets))
            using(var currentMap=RepairSnapshot.Create(currentShape))
            {
                var reselected=currentMap.Topology.Where(t=>t.Kind==ShapeKind.Edge).Single(t=>
                {
                    using var subshape=currentMap.CopySubshape(t.Selection);
                    return BoxTopology.Matches(subshape,boxRecipe with{X=11},TopologyKind.Edge,BoxBoundary.XMax,BoxBoundary.YMax);
                });
                var exact=new ExactTopologySelection(changed.Id,local.Id,refreshed.Result.Revision,refreshed.Result.AssetId,
                    currentMap.Fingerprint,reselected.Selection.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
                await Assert.ThrowsAsync<CadValidationException>(()=>new RebindExactLocalFeatureCommand(created.Id,
                    exact with{Revision=local.Result.Revision},null,.2).PrepareAsync(new(changed,session.Generation,assets,kernel),default));
                Assert.Same(changed,session.Snapshot);
                await using var editor=new LocalFeatureViewModel(session,kernel,created.Id);
                Assert.True(editor.IsReselecting);
                Assert.Equal(refreshed.Result,editor.Scene!.Items.Single().Geometry);
                editor.PickExact(exact);
                await editor.PreviewCommand.ExecuteAsync(null);
                Assert.True(editor.CanConfirm,editor.Status);
                Assert.Same(changed,session.Snapshot);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            Assert.False(session.Snapshot.Features[created.Id].IsStale);
            Assert.Equal(session.Snapshot.Features[local.Id].Result.Revision,session.Snapshot.Features[created.Id].TopologyBinding!.TargetRevision);
            await session.UndoAsync();Assert.Same(changed,session.Snapshot);
            await session.UndoAsync();Assert.Same(current,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task ChainedChamferUsesReviewedAdjacentFaceAndPersistsBothSelections()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Bound chamfer"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),
                LocalFeatureOperation.Fillet,.5));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var origin=TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            var traced=await kernel.TraceAsync(session.Snapshot,origin,local.Id,assets);
            Assert.Equal(Cadoryx.Kernel.Abstractions.HistoryResolutionStatus.Resolved,traced.Status);
            using var shape=OcctGeometryBridge.ReadShape(local.Result,assets);using var map=RepairSnapshot.Create(shape);
            using var adjacent=shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
            var edgeInMap=Enumerable.Range(0,adjacent.Items.Count).Single(i=>
                RepairSnapshot.FindTopologyIndex(shape,adjacent.Items[i])==traced.Target!.FullTopologyIndex);
            var candidateFaces=adjacent.GetAncestorIndices(edgeInMap).Span.ToArray().Select(i=>adjacent.Ancestors[i]).ToArray();
            Assert.NotEmpty(candidateFaces);
            var before=session.Snapshot;
            FeatureDefinition? created=null;
            foreach(var face in candidateFaces)
            {
                int index=RepairSnapshot.FindTopologyIndex(shape,face);
                if(index<0||map.Topology[index].Kind!=ShapeKind.Face)continue;
                var support=new ExactTopologySelection(before.Id,local.Id,local.Result.Revision,local.Result.AssetId,map.Fingerprint,
                    index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
                try
                {
                    await session.ExecuteAsync(new HistoryChamferCommand(origin,local.Id,traced.Target!,support,.2,.3));
                    created=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryChamferRecipe);break;
                }
                catch(CadValidationException){ }
            }
            Assert.NotNull(created);
            Assert.Equal(local.Id,created.Inputs.Single());Assert.NotNull(created.TopologyBinding!.SupportFace);
            Assert.NotNull(created.TopologyHistory);
            var saved=session.Snapshot;
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(saved,session.Snapshot);
            var path=files.PathFor("bound-chamfer.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
            {
                Assert.Equal(created.TopologyBinding,loaded.Snapshot.Features[created.Id].TopologyBinding);
                Assert.Equal(created.Recipe,loaded.Snapshot.Features[created.Id].Recipe);
            }
            await using(var editor=new LocalFeatureViewModel(session,kernel,created.Id))
            {
                Assert.True(editor.IsBoundEditing);Assert.True(editor.IsChamfer);Assert.False(editor.CanChangeOperation);
                editor.Size=.3;editor.SecondDistance=.4;
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                Assert.Same(saved,session.Snapshot);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            Assert.Equal(.3,((HistoryChamferRecipe)session.Snapshot.Features[created.Id].Recipe).Distance);
            await session.UndoAsync();Assert.Same(saved,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
}
