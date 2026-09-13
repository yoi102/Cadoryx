using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class BooleanHistoryTests
{
    [Theory][InlineData(BooleanOperation.Cut,4800)][InlineData(BooleanOperation.Fuse,6208)][InlineData(BooleanOperation.Common,1200)]
    public async Task NativeBooleanEvidenceSurvivesStorageAndUndo(BooleanOperation operation,double expectedVolume)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Native Boolean"),assets,kernel,new InlineSessionDispatcher()))
        {
            var source=await Seed(session);var before=session.Snapshot;
            await session.ExecuteAsync(new BooleanCommand(operation,source.Select(f=>f.OutputBodyId)));
            var committed=session.Snapshot;var feature=Boolean(committed);var history=Assert.IsType<TopologyHistory>(feature.TopologyHistory);
            Assert.Equal(expectedVolume,feature.Result.VolumeMm3,5);Assert.Equal(2,history.SchemaVersion);
            Assert.Equal(OcctGeometryKernel.BooleanHistoryAdapterVersion,history.AdapterVersion);
            Assert.Equal(source[1].Result.Revision,history.GetSource(1).Revision);
            Assert.All(history.Entries,e=>Assert.InRange(e.SourceArgument,0,1));
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(committed,session.Snapshot);
            string path=files.PathFor("boolean.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);var saved=Boolean(loaded.Snapshot).TopologyHistory!;
            Assert.Equal(history.Entries.ToArray(),saved.Entries.ToArray());Assert.Equal(history.AdditionalSources.ToArray(),saved.AdditionalSources.ToArray());
            Assert.Equal(history.AdapterVersion,saved.AdapterVersion);Assert.Equal(history.ResultFingerprint,saved.ResultFingerprint);
            var reference=TopologyReference.Box(loaded.Snapshot,source[0].Id,BoxBoundary.XMin);
            var trace=await kernel.TraceAsync(loaded.Snapshot,reference,feature.Id,assets);
            Assert.NotEqual(HistoryResolutionStatus.Unsupported,trace.Status);Assert.NotEqual(HistoryResolutionStatus.Stale,trace.Status);
            if(operation==BooleanOperation.Cut)
            {
                Assert.Equal(HistoryResolutionStatus.Resolved,trace.Status);
                var split=TopologyReference.Box(loaded.Snapshot,source[0].Id,BoxBoundary.YMin);
                var splitTrace=await kernel.TraceAsync(loaded.Snapshot,split,feature.Id,assets);
                Assert.Equal(HistoryResolutionStatus.Ambiguous,splitTrace.Status);Assert.Null(splitTrace.Target);
                var deleted=TopologyReference.Box(loaded.Snapshot,source[1].Id,BoxBoundary.ZMax);
                Assert.Equal(HistoryResolutionStatus.Deleted,(await kernel.TraceAsync(loaded.Snapshot,deleted,feature.Id,assets)).Status);
            }
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task NativeSharedTargetsRemainAmbiguousFromEitherInput()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Shared topology"),assets,kernel,new InlineSessionDispatcher()))
        {
            var source=await Seed(session,new BoxRecipe(10,20,30,RigidTransform3d.Identity));
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Common,source.Select(f=>f.OutputBodyId)));
            var feature=Boolean(session.Snapshot);Assert.NotNull(feature.TopologyHistory);
            foreach(var input in source)
            {
                var reference=TopologyReference.Box(session.Snapshot,input.Id,BoxBoundary.XMin);
                var trace=await kernel.TraceAsync(session.Snapshot,reference,feature.Id,assets);
                Assert.Equal(HistoryResolutionStatus.Ambiguous,trace.Status);Assert.Null(trace.Target);
            }
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task RecomputeReplacesEveryOperandIdentityAndUndoRestoresEvidence()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Recompute Boolean"),assets,kernel,new InlineSessionDispatcher()))
        {
            var source=await Seed(session);await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,source.Select(f=>f.OutputBodyId)));
            var before=session.Snapshot;var old=Boolean(before);
            await session.ExecuteAsync(new RecomputeCommand(source[1].Id,new BoxRecipe(3,22,32,RigidTransform3d.Translate(4,-1,-1))));
            var changed=session.Snapshot;var current=Boolean(changed);Assert.Equal(4200,current.Result.VolumeMm3,5);
            Assert.NotEqual(old.TopologyHistory!.GetSource(1).Revision,current.TopologyHistory!.GetSource(1).Revision);
            Assert.Equal(old.TopologyHistory.SourceRevision,current.TopologyHistory.SourceRevision);
            var reference=TopologyReference.Box(changed,source[0].Id,BoxBoundary.XMin);
            Assert.Equal(HistoryResolutionStatus.Resolved,(await kernel.TraceAsync(changed,reference,current.Id,assets)).Status);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(changed,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task OtherOperandFingerprintAndMissingCoverageCannotBeIgnored()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Evidence integrity"),assets,kernel,new InlineSessionDispatcher()))
        {
            var source=await Seed(session);await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,source.Select(f=>f.OutputBodyId)));
            var snapshot=session.Snapshot;var feature=Boolean(snapshot);var history=feature.TopologyHistory!;
            var reference=TopologyReference.Box(snapshot,source[0].Id,BoxBoundary.XMin);
            var foreign=history with{AdditionalSources=[history.AdditionalSources[0] with{Fingerprint=new string('a',64)}]};
            var corrupt=snapshot with{Features=snapshot.Features.SetItem(feature.Id,feature with{TopologyHistory=foreign})};
            Assert.Equal(HistoryResolutionStatus.Stale,(await kernel.TraceAsync(corrupt,reference,feature.Id,assets)).Status);
            int slot=history.Entries.First(e=>e.SourceArgument==1).SourceIndex;
            var missing=history with{Entries=history.Entries.Where(e=>e.SourceArgument!=1||e.SourceIndex!=slot).ToImmutableArray()};
            missing.ValidateFor(feature);corrupt=snapshot with{Features=snapshot.Features.SetItem(feature.Id,feature with{TopologyHistory=missing})};
            Assert.Equal(HistoryResolutionStatus.Unsupported,(await kernel.TraceAsync(corrupt,reference,feature.Id,assets)).Status);
            corrupt=snapshot with{Features=snapshot.Features.SetItem(feature.Id,feature with{TopologyHistory=history with{AdapterVersion=OcctGeometryKernel.HistoryAdapterVersion}})};
            Assert.Equal(HistoryResolutionStatus.Unsupported,(await kernel.TraceAsync(corrupt,reference,feature.Id,assets)).Status);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task ThreeInputsKeepDistinctNativeArgumentSlots()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Three input cut"),assets,kernel,new InlineSessionDispatcher()))
        {
            var source=await Seed(session,new BoxRecipe(2,22,32,RigidTransform3d.Translate(2,-1,-1)));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,22,32,RigidTransform3d.Translate(6,-1,-1)),"Tool 2",source[0].PartId));
            var third=session.Snapshot.Features.Values.Single(f=>f.Name=="Tool 2");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[source[0].OutputBodyId,source[1].OutputBodyId,third.OutputBodyId]));
            var feature=Boolean(session.Snapshot);Assert.Equal(3600,feature.Result.VolumeMm3,5);Assert.Equal(3,feature.TopologyHistory!.ArgumentCount);
            Assert.Equal(third.Result.Revision,feature.TopologyHistory.GetSource(2).Revision);
            var reference=TopologyReference.Box(session.Snapshot,third.Id,BoxBoundary.ZMax);
            Assert.Equal(HistoryResolutionStatus.Deleted,(await kernel.TraceAsync(session.Snapshot,reference,feature.Id,assets)).Status);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task EmptyCommonRetainsDeletedRelationsAndNoVisibleBody()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Empty intersection"),assets,kernel,new InlineSessionDispatcher()))
        {
            var source=await Seed(session,new BoxRecipe(2,2,2,RigidTransform3d.Translate(100,0,0)));
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Common,source.Select(f=>f.OutputBodyId)));
            var feature=Boolean(session.Snapshot);Assert.Equal(BodyKind.Empty,feature.Result.Kind);Assert.Empty(session.Snapshot.Bodies);
            var reference=TopologyReference.Box(session.Snapshot,source[0].Id,BoxBoundary.XMin);
            Assert.Equal(HistoryResolutionStatus.Deleted,(await kernel.TraceAsync(session.Snapshot,reference,feature.Id,assets)).Status);
        }
        Assert.Equal(0,assets.Count);
    }

    private static FeatureDefinition Boolean(DocumentSnapshot snapshot)=>snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
    private static async Task<FeatureDefinition[]> Seed(CadDocumentSession session,BoxRecipe? tool=null)
    {
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var first=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new AddBodyCommand(tool??new BoxRecipe(2,22,32,RigidTransform3d.Translate(4,-1,-1)),"Tool",first.PartId));
        return [first,session.Snapshot.Features.Values.Single(f=>f.Id!=first.Id)];
    }
}
