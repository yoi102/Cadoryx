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

public sealed class TopologyHistoryTests
{
    [Fact] public async Task ActualSplitBooleanProducesAmbiguousHistoryInsteadOfChoosingOneFace()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Boolean gate"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();var reference=TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.YMin);
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,22,32,RigidTransform3d.Translate(4,-1,-1)),"Tool",box.PartId));
            var tool=session.Snapshot.Bodies.Values.Single(b=>b.Name=="Tool");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[box.OutputBodyId,tool.Id]));
            var boolean=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            Assert.NotNull(boolean.TopologyHistory);Assert.Equal(4800,boolean.Result.VolumeMm3,5);
            var result=await kernel.TraceAsync(session.Snapshot,reference,boolean.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Ambiguous,result.Status);Assert.Null(result.Target);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(0.2,LocalFeatureOperation.Fillet,true)][InlineData(1.3,LocalFeatureOperation.Fillet,true)]
    [InlineData(2.1,LocalFeatureOperation.Chamfer,true)][InlineData(3.0,LocalFeatureOperation.Chamfer,false)]
    public async Task AdditionalRigidPlacementsResolveOrFailClosedAfterReload(double angle,LocalFeatureOperation operation,bool verified)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Rotation"),assets,kernel,new InlineSessionDispatcher()))
        {
            var box=new BoxRecipe(10,20,30,new(new(-40,50,-60),Quaterniond.FromAxisAngle(new(1,2,3),angle)));
            await session.ExecuteAsync(new AddBodyCommand(box,"Box"));var source=session.Snapshot.Features.Values.Single();
            var face=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin);
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin,BoxBoundary.ZMax),operation,1));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            Assert.Equal(verified,local.TopologyHistory is not null);
            Assert.InRange(local.Result.VolumeMm3,5994,6000);
            string path=files.PathFor("rotation.cadoryx");await storage.SaveAsync(session.Snapshot,assets,path);
            using var loaded=await storage.LoadAsync(path,assets);
            var result=await kernel.TraceAsync(loaded.Snapshot,face,local.Id,assets);
            Assert.Equal(verified?HistoryResolutionStatus.Resolved:HistoryResolutionStatus.Unsupported,result.Status);
            if(!verified)Assert.Null(result.Target);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task FrozenH1HistoryRetainsItsOriginalAdapterAndResolves()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m4t1h1-history.cadoryx");
        Assert.Equal("b007b5576dac2622911eeac9e224278795be605494bccc6d7014842048f4dad7",Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();using var files=new TestFiles();
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            var local=loaded.Snapshot.Features.Values.Single(f=>f.TopologyHistory is not null);
            Assert.Equal(OcctGeometryKernel.LegacyHistoryAdapterVersion,local.TopologyHistory!.AdapterVersion);
            var reference=TopologyReference.Box(loaded.Snapshot,local.Inputs[0],BoxBoundary.YMin);
            var old=await kernel.TraceAsync(loaded.Snapshot,reference,local.Id,assets);Assert.Equal(HistoryResolutionStatus.Resolved,old.Status);
            string saved=files.PathFor("h1.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,saved);
            using var reopened=await storage.LoadAsync(saved,assets);Assert.Equal(old,await kernel.TraceAsync(reopened.Snapshot,reference,local.Id,assets));
            await using var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher());
            await session.ExecuteAsync(new RecomputeCommand(local.Id,local.Recipe));
            Assert.Equal(OcctGeometryKernel.HistoryAdapterVersion,session.Snapshot.Features[local.Id].TopologyHistory!.AdapterVersion);
            await session.UndoAsync();Assert.Same(loaded.Snapshot,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ForeignMissingAndUnverifiedMapsCannotProduceTargets()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("History"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var source=session.Snapshot.Features.Values.Single();
            var face=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin);
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Fillet,2));
            var doc=session.Snapshot;var local=doc.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);var h=local.TopologyHistory!;
            Assert.Equal(HistoryResolutionStatus.WrongContext,(await kernel.TraceAsync(doc,face with{DocumentId=DocumentId.New()},local.Id,assets)).Status);
            Assert.Equal(HistoryResolutionStatus.Missing,(await kernel.TraceAsync(doc,face,FeatureId.New(),assets)).Status);
            Assert.Equal(HistoryResolutionStatus.Unsupported,(await kernel.TraceAsync(doc,face,source.Id,assets)).Status);
            foreach(var changed in new[]{h with{AdapterVersion="future"},h with{ResultFingerprint=new string('0',64)}})
            {
                var snapshot=doc with{Features=doc.Features.SetItem(local.Id,local with{TopologyHistory=changed})};snapshot.Validate();
                var result=await kernel.TraceAsync(snapshot,face,local.Id,assets);
                Assert.Null(result.Target);Assert.Equal(changed.AdapterVersion=="future"?HistoryResolutionStatus.Unsupported:HistoryResolutionStatus.Stale,result.Status);
            }
            using var cts=new CancellationTokenSource();cts.Cancel();int count=assets.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.TraceAsync(doc,face,local.Id,assets,cts.Token));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(LocalFeatureOperation.Fillet,false)][InlineData(LocalFeatureOperation.Chamfer,false)]
    [InlineData(LocalFeatureOperation.Fillet,true)][InlineData(LocalFeatureOperation.Chamfer,true)]
    public async Task NativeHistorySurvivesRecomputeUndoAndStorage(LocalFeatureOperation operation,bool rotated)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("History"),assets,kernel,new InlineSessionDispatcher()))
        {
            var box=new BoxRecipe(10,20,30,rotated?new(new(4,5,6),Quaterniond.FromAxisAngle(new(2,3,1),0.8)):RigidTransform3d.Identity);
            await session.ExecuteAsync(new AddBodyCommand(box,"Box"));var source=session.Snapshot.Features.Values.Single();
            var face=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin);
            var exact=face with{Policy=TopologyRebindPolicy.ExactRevision};
            var edge=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin,BoxBoundary.ZMax);
            await session.ExecuteAsync(new LocalFeatureCommand(edge,operation,2));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var history=Assert.IsType<TopologyHistory>(feature.TopologyHistory);history.Validate();
            Assert.Contains(history.Entries,e=>e.Evolution==TopologyEvolution.Modified);
            Assert.Contains(history.Entries,e=>e.Evolution==TopologyEvolution.Unchanged);
            Assert.Contains(history.Entries,e=>e.Evolution==TopologyEvolution.Generated);
            Assert.Contains(history.Entries,e=>e.Evolution==TopologyEvolution.Deleted);
            var inspection=await TopologyHistoryInspection.InspectAsync(session.Snapshot,face,feature.Id,assets,kernel);
            Assert.Equal(HistoryResolutionStatus.Resolved,inspection.Result.Status);Assert.True(inspection.IsCurrent(session.Snapshot));
            Assert.Equal(HistoryResolutionStatus.Generated,(await kernel.TraceAsync(session.Snapshot,edge,feature.Id,assets)).Status);
            var before=session.Snapshot;
            for(int i=0;i<3;i++)
            {
                await session.ExecuteAsync(new RecomputeCommand(source.Id,box with{X=20+i*10}));
                Assert.Equal(HistoryResolutionStatus.Resolved,(await kernel.TraceAsync(session.Snapshot,face,feature.Id,assets)).Status);
                Assert.Equal(HistoryResolutionStatus.Stale,(await kernel.TraceAsync(session.Snapshot,exact,feature.Id,assets)).Status);
                Assert.NotEqual(history.ResultRevision,session.Snapshot.Features[feature.Id].TopologyHistory!.ResultRevision);
            }
            Assert.False(inspection.IsCurrent(session.Snapshot));var after=session.Snapshot;
            for(int i=0;i<3;i++)await session.UndoAsync();Assert.Same(before,session.Snapshot);Assert.Same(history,session.Snapshot.Features[feature.Id].TopologyHistory);
            for(int i=0;i<3;i++)await session.RedoAsync();Assert.Same(after,session.Snapshot);
            string path=files.PathFor("history.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);var saved=loaded.Snapshot.Features[feature.Id].TopologyHistory!;
            Assert.Equal(after.Features[feature.Id].TopologyHistory!.Entries.ToArray(),saved.Entries.ToArray());
            Assert.Equal(await kernel.TraceAsync(after,face,feature.Id,assets),await kernel.TraceAsync(loaded.Snapshot,face,feature.Id,assets));
            Assert.Equal(8,FormatEvolutionTests.Manifest(path).Sections.Length);
            Assert.Equal(session.Snapshot.Settings,await storage.ReadSettingsAsync(path));
            await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new RecomputeCommand(feature.Id,(LocalFeatureRecipe)feature.Recipe with{Size=1000})));
            Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("split",HistoryResolutionStatus.Ambiguous)][InlineData("merge",HistoryResolutionStatus.Ambiguous)]
    [InlineData("deleted",HistoryResolutionStatus.Deleted)][InlineData("unmapped",HistoryResolutionStatus.Unsupported)]
    [InlineData("generated",HistoryResolutionStatus.Generated)][InlineData("contradiction",HistoryResolutionStatus.Ambiguous)]
    public void AmbiguousAndMissingRelationsNeverChooseFirst(string scenario,HistoryResolutionStatus status)
    {
        var e=new TopologyHistoryEntry(2,HistoryShapeKind.Edge,TopologyEvolution.Modified,5,HistoryShapeKind.Edge);
        ImmutableArray<TopologyHistoryEntry> entries=scenario switch
        {
            "split"=>[e,e with{ResultIndex=6}],"merge"=>[e,e with{SourceIndex=3}],
            "deleted"=>[e with{Evolution=TopologyEvolution.Deleted,ResultIndex=null,ResultKind=null}],
            "unmapped"=>[e,e with{Evolution=TopologyEvolution.Unmapped,ResultIndex=null,ResultKind=null}],
            "generated"=>[e with{Evolution=TopologyEvolution.Generated,ResultKind=HistoryShapeKind.Face}],
            _=>[e,e with{Evolution=TopologyEvolution.Deleted,ResultIndex=null,ResultKind=null}]
        };
        var history=new TopologyHistory(GeometryRevisionId.New(),new(new string('a',64)),GeometryRevisionId.New(),new(new string('b',64)),"test",new string('c',64),new string('d',64),10,10,entries);
        var result=TopologyHistoryReduction.Resolve(history,2,HistoryShapeKind.Edge);Assert.Equal(status,result.Status);Assert.Null(result.Target);
    }

    [Fact] public async Task RotatedBrepAxisRoundoffRetainsVerifiedHistory()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var box=new BoxRecipe(10,20,30,new(new(4,5,6),Quaterniond.FromAxisAngle(new(2,3,1),0.8)));
        using(var source=await kernel.EvaluateAsync(box,assets))
        using(var result=await kernel.EvaluateAsync(new LocalFeatureRecipe(source.Geometry,box,BoxBoundary.YMax,BoxBoundary.ZMin,LocalFeatureOperation.Fillet,1),assets))
        {
            Assert.NotNull(result.TopologyHistory);Assert.DoesNotContain(result.Diagnostics,d=>d.Code=="HISTORY.UNSUPPORTED");
            Assert.InRange(result.Geometry.VolumeMm3,5997,6000);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task FrozenT2LoadsWithoutInventingHistory()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m4t2-local.cadoryx");
        Assert.Equal("c5b9347bae6e5b46e7d4109781503c221617758846c4ba211f33f3c0e53cbe02",Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var kernel=new OcctGeometryKernel();
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.All(loaded.Snapshot.Features.Values,f=>Assert.Null(f.TopologyHistory));Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
            var target=loaded.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            Assert.Equal(HistoryResolutionStatus.Unsupported,(await kernel.TraceAsync(loaded.Snapshot,loaded.Snapshot.TopologyReferences.Values.First(),target.Id,assets)).Status);
            await using var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher());
            await session.ExecuteAsync(new RecomputeCommand(target.Id,target.Recipe));Assert.NotNull(session.Snapshot.Features[target.Id].TopologyHistory);
            await session.UndoAsync();Assert.Null(session.Snapshot.Features[target.Id].TopologyHistory);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("duplicate")][InlineData("orphan")][InlineData("revision")][InlineData("index")][InlineData("kind")][InlineData("null-target")][InlineData("version")][InlineData("trailing")]
    public async Task MalformedHistoryIsRejectedWithoutAssetLeaks(string mutation)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();using var files=new TestFiles();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("History"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var source=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Fillet,2));
        string path=files.PathFor("bad.cadoryx");await session.SaveAsync(storage,path);int count=assets.Count;
        FormatEvolutionTests.RewriteSection(path,"history",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackHistories>(bytes);var h=Assert.Single(data.Histories);var e=h.Entries.First(e=>e.Result is not null);
            h=mutation switch
            {
                "orphan"=>h with{Feature=Guid.NewGuid()},"revision"=>h with{ResultRevision=Guid.NewGuid()},"version"=>h with{Version=99},
                "index"=>h with{Entries=[e with{Result=h.ResultCount}]},"kind"=>h with{Entries=[e with{SourceKind=99}]},
                "null-target"=>h with{Entries=[e with{Result=null,ResultKind=null}]},_=>h
            };
            var output=MessagePackSerializer.Serialize(new PackHistories(mutation=="duplicate"?[h,h]:[h]));
            return mutation=="trailing"?[..output,0]:output;
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        Assert.Equal(session.Snapshot.Settings,await storage.ReadSettingsAsync(path));
    }
}
