using System.Collections.Immutable;
using System.Security.Cryptography;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

/// <summary>Synthetic history tests prove the protocol and transaction plumbing,
/// not native Boolean correspondence. The ordinary kernel still fails closed.</summary>
public sealed class MultiInputHistoryTests
{
    [Fact] public async Task FrozenAxisHistoryMigratesWithoutChangingEvidenceOrGeometry()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m4t1h2a-history.cadoryx");
        Assert.Equal("cc4722b54c3a96dc9fbd65f9bd470c24ab2fd785d66e89f89dc856240da17528",Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();using var files=new TestFiles();var kernel=new OcctGeometryKernel();
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
            var local=loaded.Snapshot.Features.Values.Single(f=>f.TopologyHistory is not null);var h=local.TopologyHistory!;
            Assert.Equal(1,h.SchemaVersion);Assert.Empty(h.AdditionalSources);Assert.Equal(OcctGeometryKernel.PreviousAxisHistoryAdapterVersion,h.AdapterVersion);
            var reference=TopologyReference.Box(loaded.Snapshot,local.Inputs[0],BoxBoundary.YMin);
            var expected=await kernel.TraceAsync(loaded.Snapshot,reference,local.Id,assets);Assert.Equal(HistoryResolutionStatus.Resolved,expected.Status);
            string current=files.PathFor("migrated.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,current);
            using var reopened=await storage.LoadAsync(current,assets);
            Assert.Equal(loaded.Snapshot.Id,reopened.Snapshot.Id);Assert.Equal(loaded.Snapshot.StateId,reopened.Snapshot.StateId);
            Assert.Equal(local.Result,reopened.Snapshot.Features[local.Id].Result);
            Assert.Equal(h.Entries.ToArray(),reopened.Snapshot.Features[local.Id].TopologyHistory!.Entries.ToArray());
            Assert.Equal(expected,await kernel.TraceAsync(reopened.Snapshot,reference,local.Id,assets));
        }
        Assert.Equal(0,assets.Count);
    }
    private static TopologyHistory Example()=>new(GeometryRevisionId.New(),new(new string('a',64)),
        GeometryRevisionId.New(),new(new string('b',64)),"synthetic-protocol-test",new string('c',64),new string('d',64),10,10,
        [new(2,HistoryShapeKind.Face,TopologyEvolution.Modified,5,HistoryShapeKind.Face),
         new(2,HistoryShapeKind.Edge,TopologyEvolution.Unchanged,6,HistoryShapeKind.Edge,1)],2)
        {AdditionalSources=[new(GeometryRevisionId.New(),new(new string('e',64)),new string('f',64),4)]};

    [Fact] public void SameSlotInDifferentOperandsRemainsDistinct()
    {
        var h=Example();h.Validate();
        Assert.Equal(5,TopologyHistoryReduction.Resolve(h,0,2,HistoryShapeKind.Face).Target!.FullTopologyIndex);
        Assert.Equal(6,TopologyHistoryReduction.Resolve(h,1,2,HistoryShapeKind.Edge).Target!.FullTopologyIndex);
        Assert.Equal(HistoryResolutionStatus.WrongContext,TopologyHistoryReduction.Resolve(h,1,5,HistoryShapeKind.Edge).Status);
        Assert.Equal(HistoryResolutionStatus.WrongContext,TopologyHistoryReduction.Resolve(h,2,2,HistoryShapeKind.Edge).Status);
    }
    [Fact] public void SharedResultAcrossOperandsIsAmbiguousEvenWhenSourceSlotsMatch()
    {
        var h=Example();h=h with{Entries=[h.Entries[0],h.Entries[0] with{SourceArgument=1}]};
        foreach(int argument in new[]{0,1})
        {
            var result=TopologyHistoryReduction.Resolve(h,argument,2,HistoryShapeKind.Face);
            Assert.Equal(HistoryResolutionStatus.Ambiguous,result.Status);Assert.Null(result.Target);
        }
    }
    [Theory][InlineData("argument")][InlineData("range")][InlineData("old-map")][InlineData("duplicate")][InlineData("source-kind")]
    public void MalformedMultiInputMapCannotValidate(string mutation)
    {
        var h=Example();h=mutation switch
        {
            "argument"=>h with{Entries=[..h.Entries,h.Entries[0] with{SourceArgument=2}]},
            "range"=>h with{Entries=[h.Entries[0],h.Entries[1] with{SourceIndex=8}]},
            "old-map"=>h with{SchemaVersion=1},
            "duplicate"=>h with{Entries=[..h.Entries,h.Entries[0]]},
            _=>h with{Entries=[..h.Entries,h.Entries[1] with{SourceArgument=0}]}
        };
        Assert.Throws<CadValidationException>(h.Validate);
    }

    [Fact] public async Task BooleanCommandRecomputeAndStorageKeepOperandOrderAndExactUndo()
    {
        var assets=new MemoryAssetStore();var kernel=new ProtocolKernel();var storage=new CadDocumentStorage();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Multi-source protocol"),assets,kernel,new InlineSessionDispatcher()))
        {
            var source=await Seed(session);var before=session.Snapshot;
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,source.Select(s=>s.OutputBodyId)));
            var after=session.Snapshot;var feature=after.Features.Values.Single(f=>f.Recipe is BooleanRecipe);var history=feature.TopologyHistory!;
            Assert.Equal(2,history.ArgumentCount);Assert.Equal(source[1].Result.Revision,history.GetSource(1).Revision);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            await session.ExecuteAsync(new RecomputeCommand(source[1].Id,new BoxRecipe(3,22,32,RigidTransform3d.Translate(4,-1,-1))));
            var recomputed=session.Snapshot;Assert.NotEqual(history.GetSource(1).Revision,recomputed.Features[feature.Id].TopologyHistory!.GetSource(1).Revision);
            Assert.Equal(history.SourceRevision,recomputed.Features[feature.Id].TopologyHistory!.SourceRevision);
            await session.UndoAsync();Assert.Same(after,session.Snapshot);await session.RedoAsync();Assert.Same(recomputed,session.Snapshot);
            string path=files.PathFor("multi.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);var saved=loaded.Snapshot.Features[feature.Id].TopologyHistory!;
            Assert.Equal(recomputed.Features[feature.Id].TopologyHistory!.Entries.ToArray(),saved.Entries.ToArray());
            Assert.Equal(recomputed.Features[feature.Id].TopologyHistory!.AdditionalSources.ToArray(),saved.AdditionalSources.ToArray());
            Assert.Equal(2,FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="history").SchemaVersion);
            var reference=TopologyReference.Box(loaded.Snapshot,source[0].Id,BoxBoundary.YMin);
            Assert.Equal(HistoryResolutionStatus.Unsupported,(await new OcctGeometryKernel().TraceAsync(loaded.Snapshot,reference,feature.Id,assets)).Status);
            kernel.EmitHistory=false;
            await session.ExecuteAsync(new RecomputeCommand(source[0].Id,source[0].Recipe));Assert.Null(session.Snapshot.Features[feature.Id].TopologyHistory);
            await session.UndoAsync();Assert.Same(recomputed,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task RecipeAndEvidenceCannotBypassFeatureDependencyOrder()
    {
        var assets=new MemoryAssetStore();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Operand identity"),assets,new ProtocolKernel(),new InlineSessionDispatcher()))
        {
            var source=await Seed(session);await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,source.Select(s=>s.OutputBodyId)));
            var snapshot=session.Snapshot;var feature=snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            var history=feature.TopologyHistory!;var first=history.GetSource(0);var second=history.GetSource(1);
            var swapped=feature with
            {
                Recipe=((BooleanRecipe)feature.Recipe) with{Inputs=[source[1].Result,source[0].Result]},
                TopologyHistory=history with
                {
                    SourceRevision=second.Revision,SourceAsset=second.Asset,SourceFingerprint=second.Fingerprint,SourceCount=second.TopologyCount,
                    AdditionalSources=[first],Entries=history.Entries.Select(e=>e with{SourceArgument=1-e.SourceArgument}).ToImmutableArray()
                }
            };
            // The recipe and evidence agree, but disagree with the actual upstream slots.
            swapped.TopologyHistory!.ValidateFor(swapped);
            var corrupt=snapshot with{Features=snapshot.Features.SetItem(feature.Id,swapped)};
            Assert.Equal("History operand order differs from its upstream features.",Assert.Throws<CadValidationException>(corrupt.Validate).Message);
            // Reordering only the dependency IDs must fail at the same boundary.
            corrupt=snapshot with{Features=snapshot.Features.SetItem(feature.Id,feature with{Inputs=[source[1].Id,source[0].Id]})};
            Assert.Equal("History operand order differs from its upstream features.",Assert.Throws<CadValidationException>(corrupt.Validate).Message);
            Assert.Same(snapshot,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("missing-sources")][InlineData("revision")][InlineData("argument")][InlineData("index")][InlineData("order")][InlineData("old-section")]
    public async Task CorruptMultiInputStorageDoesNotLeakOrLoad(string mutation)
    {
        var assets=new MemoryAssetStore();var kernel=new ProtocolKernel();var storage=new CadDocumentStorage();using var files=new TestFiles();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Corrupt protocol"),assets,kernel,new InlineSessionDispatcher());
        var source=await Seed(session);await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,source.Select(s=>s.OutputBodyId)));
        string path=files.PathFor("bad.cadoryx");await session.SaveAsync(storage,path);int count=assets.Count;
        if(mutation=="old-section")FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="history"?s with{SchemaVersion=1}:s).ToImmutableArray()});
        else FormatEvolutionTests.RewriteSection(path,"history",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackHistories>(bytes);var h=Assert.Single(data.Histories);var second=h.AdditionalSources![0];
            h=mutation switch
            {
                "missing-sources"=>h with{AdditionalSources=null},
                "revision"=>h with{AdditionalSources=[second with{Revision=Guid.NewGuid()}]},
                "argument"=>h with{Entries=[h.Entries[0] with{SourceArgument=2}]},
                "index"=>h with{Entries=[h.Entries[1] with{Source=second.TopologyCount}]},
                _=>h with{SourceRevision=second.Revision,SourceAsset=second.Asset,SourceFingerprint=second.Fingerprint,
                    AdditionalSources=[new(h.SourceRevision,h.SourceAsset,h.SourceFingerprint,h.SourceCount)]}
            };
            return MessagePackSerializer.Serialize(new PackHistories([h]));
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        Assert.Equal(session.Snapshot.Settings,await storage.ReadSettingsAsync(path));
    }

    private static async Task<FeatureDefinition[]> Seed(CadDocumentSession session)
    {
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var first=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,22,32,RigidTransform3d.Translate(4,-1,-1)),"Tool",first.PartId));
        return [first,session.Snapshot.Features.Values.Single(f=>f.Id!=first.Id)];
    }
    private sealed class ProtocolKernel:IGeometryKernel
    {
        private readonly OcctGeometryKernel inner=new();public bool EmitHistory=true;
        public string Version=>"synthetic-protocol-test";public bool Supports(GeometryRecipe recipe)=>inner.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {
            using var result=await inner.EvaluateAsync(recipe,assets,token);
            TopologyHistory? history=recipe is BooleanRecipe?null:result.TopologyHistory;
            if(EmitHistory&&recipe is BooleanRecipe b)
            {
                history=Example() with{SourceRevision=b.Inputs[0].Revision,SourceAsset=b.Inputs[0].AssetId,
                    ResultRevision=result.Geometry.Revision,ResultAsset=result.Geometry.AssetId,
                    AdditionalSources=[new(b.Inputs[1].Revision,b.Inputs[1].AssetId,new string('e',64),4)]};
            }
            return new(result.Geometry,assets.Acquire(result.Geometry.AssetId)){TopologyHistory=history};
        }
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
