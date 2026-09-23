using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using MessagePack;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Cadoryx.Tests;

public sealed class FeatureBindingTests
{
    [Fact] public async Task PreviousQueryFileMigratesToEmptyBindingsWithoutChangingGeometry()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        var snapshot=DocumentSnapshot.Create("Previous query file");
        await using(var session=new CadDocumentSession(snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));
            snapshot=session.Snapshot;
            var feature=snapshot.Features.Values.Single();
            var source=TopologyReference.Box(snapshot,feature.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            await session.ExecuteAsync(new UpsertHistoryQueryCommand(new(HistoryQueryId.New(),"Edge",source,feature.Id)));
            snapshot=session.Snapshot;
            await session.SaveAsync(storage,files.PathFor("previous.cadoryx"));
        }
        var path=files.PathFor("previous.cadoryx");
        var manifest=FormatEvolutionTests.Manifest(path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var bindings=manifest.Sections.Single(s=>s.Kind=="feature-bindings");
            zip.GetEntry(bindings.Path)!.Delete();zip.GetEntry("manifest.json")!.Delete();
            var previous=manifest with{ApplicationVersion="0.4.6",
                Sections=[..manifest.Sections.Where(s=>s.Kind!="feature-bindings").Select(s=>s.Kind switch
                {
                    "document"=>s with{SchemaVersion=6},"features"=>s with{SchemaVersion=5},_=>s
                })],
                RequiredCapabilities=[..manifest.RequiredCapabilities.Where(c=>c!="cadoryx.feature-bindings.1")]};
            using var output=zip.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(output,previous,CadJson.Options);
        }
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
            Assert.Equal(snapshot.Features.Values.Single().Result,loaded.Snapshot.Features.Values.Single().Result);
            Assert.Single(loaded.Snapshot.HistoryQueries);
            Assert.All(loaded.Snapshot.Features.Values,f=>Assert.Null(f.TopologyBinding));
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task QueryWindowRequiresFreshExactEdgeAnalysisForCreateAndExplicitRebind()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Window binding"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,1));
            var first=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var origin=TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            var query=new HistoryQuery(HistoryQueryId.New(),"Cross-feature edge",origin,first.Id);
            await session.ExecuteAsync(new UpsertHistoryQueryCommand(query));
            await using var editor=new HistoryQueryViewModel(session,kernel);
            await editor.CreateFilletCommand.ExecuteAsync(null);
            Assert.DoesNotContain(session.Snapshot.Features.Values,f=>f.Recipe is HistoryFilletRecipe);
            await editor.AnalyzeCommand.ExecuteAsync(null);
            Assert.Contains("#",editor.ResultSummary);
            await editor.CreateFilletCommand.ExecuteAsync(null);
            var bound=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
            Assert.False(bound.IsStale);
            await session.ExecuteAsync(new RecomputeCommand(box.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));
            Assert.True(session.Snapshot.Features[bound.Id].IsStale);
            editor.SelectedStaleConsumer=editor.StaleConsumers.Single(c=>c.Id==bound.Id);
            await editor.RebindFilletCommand.ExecuteAsync(null);
            Assert.True(session.Snapshot.Features[bound.Id].IsStale);
            await editor.AnalyzeCommand.ExecuteAsync(null);
            editor.SelectedStaleConsumer=editor.StaleConsumers.Single(c=>c.Id==bound.Id);
            await editor.RebindFilletCommand.ExecuteAsync(null);
            Assert.False(session.Snapshot.Features[bound.Id].IsStale);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task CrossFeatureEdgeFilletFreezesOnUpstreamChangeThenRequiresExactReselection()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Bound fillet"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,1));
            var first=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var origin=TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            var before=await kernel.TraceAsync(session.Snapshot,origin,first.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Resolved,before.Status);
            await session.ExecuteAsync(new HistoryFilletCommand(origin,first.Id,1,before.Target!));
            var second=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
            Assert.NotNull(second.TopologyHistory);Assert.False(second.IsStale);
            Assert.Equal(first.Id,second.TopologyBinding!.TargetFeatureId);
            Assert.Equal(before.Target!.FullTopologyIndex,second.TopologyBinding.FullTopologyIndex);
            Assert.True(second.Result.VolumeMm3<first.Result.VolumeMm3);
            var secondHistory=await kernel.TraceAsync(session.Snapshot,origin,second.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Generated,secondHistory.Status);
            Assert.Null(secondHistory.Target);
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Translate(4,4,4)),"Tool",box.PartId));
            var tool=session.Snapshot.Features.Values.Single(f=>f.Name=="Tool");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[second.OutputBodyId,tool.OutputBodyId]));
            var downstream=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            var intact=session.Snapshot;
            await session.ExecuteAsync(new RecomputeCommand(box.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));
            var stale=session.Snapshot.Features[second.Id];Assert.True(stale.IsStale);Assert.Null(stale.TopologyHistory);
            Assert.True(session.Snapshot.Features[downstream.Id].IsStale);
            Assert.Null(session.Snapshot.Features[downstream.Id].TopologyHistory);
            Assert.Equal(second.Result,stale.Result);
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ExportAsync(session.Snapshot,assets,files.PathFor("stale.step")));
            string stalePath=files.PathFor("stale.cadoryx");await session.SaveAsync(storage,stalePath);
            using(var loaded=await storage.LoadAsync(stalePath,assets))
            {
                var saved=loaded.Snapshot.Features[second.Id];Assert.True(saved.IsStale);
                Assert.Equal(stale.TopologyBinding,saved.TopologyBinding);
                Assert.Equal(stale.Result,saved.Result);
            }
            var current=await kernel.TraceAsync(session.Snapshot,origin,first.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Resolved,current.Status);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RebindHistoryFilletCommand(second.Id,origin,before.Target)));
            Assert.Same(stale,session.Snapshot.Features[second.Id]);
            await session.ExecuteAsync(new RebindHistoryFilletCommand(second.Id,origin,current.Target!));
            var rebound=session.Snapshot.Features[second.Id];Assert.False(rebound.IsStale);Assert.NotNull(rebound.TopologyHistory);
            Assert.False(session.Snapshot.Features[downstream.Id].IsStale);
            Assert.NotNull(session.Snapshot.Features[downstream.Id].TopologyHistory);
            Assert.Equal(session.Snapshot.Features[first.Id].Result.Revision,rebound.TopologyBinding!.TargetRevision);
            Assert.NotEqual(stale.Result.Revision,rebound.Result.Revision);
            await session.UndoAsync();Assert.True(session.Snapshot.Features[second.Id].IsStale);
            Assert.True(session.Snapshot.Features[downstream.Id].IsStale);
            await session.RedoAsync();Assert.False(session.Snapshot.Features[second.Id].IsStale);
            Assert.False(session.Snapshot.Features[downstream.Id].IsStale);
            string path=files.PathFor("rebound.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                Assert.Equal(rebound.TopologyBinding,loaded.Snapshot.Features[second.Id].TopologyBinding);
                var reopenedHistory=await kernel.TraceAsync(loaded.Snapshot,origin,second.Id,assets);
                Assert.Equal(HistoryResolutionStatus.Generated,reopenedHistory.Status);
                Assert.Null(reopenedHistory.Target);
                Assert.Equal(10,FormatEvolutionTests.Manifest(path).Sections.Length);
            }
            FormatEvolutionTests.RewriteSection(path,"feature-bindings",bytes=>
            {
                var data=MessagePackSerializer.Deserialize<PackFeatureBindings>(bytes);
                return MessagePackSerializer.Serialize(data with{Bindings=[data.Bindings[0],data.Bindings[0]]});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
            Assert.NotEqual(intact.StateId,session.Snapshot.StateId);
        }
        Assert.Equal(0,assets.Count);
    }
}
