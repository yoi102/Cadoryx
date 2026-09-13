using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class HistoryChainTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task NativeChainsCrossOperandSlotsAndSurviveRecomputeUndoAndStorage(bool local)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Native chain"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,local);var before=session.Snapshot;
            var reference=TopologyReference.Box(before,chain.Source.Id,BoxBoundary.XMin);var exact=reference with{Policy=TopologyRebindPolicy.ExactRevision};
            var original=await kernel.TraceAsync(before,reference,chain.Last.Id,assets);Resolved(original,local?3:2,chain.Last.Result);
            var plan=TopologyHistoryPaths.Plan(before,chain.Source.Id,chain.Last.Id);Assert.Equal(1,plan.Steps[^1].SourceArgument);
            foreach(var root in new[]{chain.Source,chain.Tool,chain.Side})
            {
                var start=session.Snapshot;var recipe=(BoxRecipe)start.Features[root.Id].Recipe;
                await session.ExecuteAsync(new RecomputeCommand(root.Id,recipe with{X=recipe.X+1}));var changed=session.Snapshot;
                var result=await kernel.TraceAsync(changed,reference,chain.Last.Id,assets);Resolved(result,local?3:2,changed.Features[chain.Last.Id].Result);
                Assert.NotEqual(start.Features[chain.Last.Id].Result.Revision,changed.Features[chain.Last.Id].Result.Revision);
                if(root.Id==chain.Source.Id)Assert.Equal(HistoryResolutionStatus.Stale,(await kernel.TraceAsync(changed,exact,chain.Last.Id,assets)).Status);
                await session.UndoAsync();Assert.Same(start,session.Snapshot);await session.RedoAsync();Assert.Same(changed,session.Snapshot);
            }
            var final=session.Snapshot;var expected=await kernel.TraceAsync(final,reference,chain.Last.Id,assets);
            string path=files.PathFor("chain.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);Assert.Equal(expected,await kernel.TraceAsync(loaded.Snapshot,reference,chain.Last.Id,assets));
            foreach(var f in final.Features.Values.Where(f=>f.TopologyHistory is not null))
            {
                var saved=loaded.Snapshot.Features[f.Id];Assert.Equal(f.Result,saved.Result);
                Assert.Equal(f.TopologyHistory!.Entries.ToArray(),saved.TopologyHistory!.Entries.ToArray());
                Assert.Equal(f.TopologyHistory.AdditionalSources.ToArray(),saved.TopologyHistory.AdditionalSources.ToArray());
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task SplitDeletedAndGeneratedSegmentsStopBeforeTheTerminalFeature()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Terminal statuses"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,true);var doc=session.Snapshot;
            var split=await kernel.TraceAsync(doc,TopologyReference.Box(doc,chain.Source.Id,BoxBoundary.YMin),chain.Last.Id,assets);
            Stopped(split,HistoryResolutionStatus.Ambiguous,chain.Cut.Id,1,3);Assert.True(split.CandidateCount>1);
            var generated=await kernel.TraceAsync(doc,TopologyReference.Box(doc,chain.Source.Id,BoxBoundary.YMin,BoxBoundary.ZMax),chain.Last.Id,assets);
            Stopped(generated,HistoryResolutionStatus.Generated,chain.Local!.Id,0,3);
            var deleted=await kernel.TraceAsync(doc,TopologyReference.Box(doc,chain.Tool.Id,BoxBoundary.ZMax),chain.Last.Id,assets);
            Stopped(deleted,HistoryResolutionStatus.Deleted,chain.Cut.Id,0,2);
            // The stable bottom edge remains an edge throughout all three full-map reductions.
            var edge=await kernel.TraceAsync(doc,TopologyReference.Box(doc,chain.Source.Id,BoxBoundary.XMin,BoxBoundary.ZMin),chain.Last.Id,assets);
            Resolved(edge,3,chain.Last.Result);Assert.Equal(HistoryShapeKind.Edge,edge.Target!.Kind);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task NativeMergeOnTheSecondSegmentDoesNotSelectTheSharedFace()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Chain merge"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,false);await session.UndoAsync(); // Restore Cut and Side before Fuse.
            await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(chain.Cut.Result),"Copy",chain.Source.PartId));
            var copy=session.Snapshot.Features.Values.Single(f=>f.Name=="Copy");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Common,[chain.Cut.OutputBodyId,copy.OutputBodyId]));
            var last=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe b&&b.Operation==BooleanOperation.Common);
            var result=await kernel.TraceAsync(session.Snapshot,TopologyReference.Box(session.Snapshot,chain.Source.Id,BoxBoundary.XMin),last.Id,assets);
            Stopped(result,HistoryResolutionStatus.Ambiguous,last.Id,1,2);Assert.Equal(1,result.CandidateCount);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("history",false)][InlineData("adapter",false)][InlineData("fingerprint",false)][InlineData("recipe",false)]
    [InlineData("history",true)][InlineData("adapter",true)][InlineData("fingerprint",true)]
    [InlineData("coverage",true)][InlineData("kind",true)][InlineData("side-revision",true)][InlineData("order",true)][InlineData("result",true)]
    public async Task EverySegmentAndSideOperandMustPassEvidenceChecks(string mutation,bool terminal)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Chain integrity"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,false);var doc=session.Snapshot;var feature=terminal?chain.Last:chain.Cut;var h=feature.TopologyHistory!;
            var expected=HistoryResolutionStatus.Unsupported;
            if(mutation=="history")feature=feature with{TopologyHistory=null};
            if(mutation=="recipe")feature=feature with{Recipe=new TransformRecipe(chain.Source.Result,RigidTransform3d.Identity)};
            if(mutation=="adapter")feature=feature with{TopologyHistory=h with{AdapterVersion="future"}};
            if(mutation=="fingerprint")
            {feature=feature with{TopologyHistory=h with{SourceFingerprint=new string('a',64)}};expected=HistoryResolutionStatus.Stale;}
            if(mutation=="coverage")
            {
                int slot=h.Entries.First(e=>e.SourceArgument==0).SourceIndex;
                feature=feature with{TopologyHistory=h with{Entries=h.Entries.Where(e=>e.SourceArgument!=0||e.SourceIndex!=slot).ToImmutableArray()}};
            }
            if(mutation=="kind")
            {
                int slot=h.Entries.First(e=>e.SourceKind==HistoryShapeKind.Face&&e.SourceArgument==0).SourceIndex;
                feature=feature with{TopologyHistory=h with{Entries=h.Entries.Select(e=>e.SourceArgument==0&&e.SourceIndex==slot?e with{SourceKind=HistoryShapeKind.Edge}:e).ToImmutableArray()}};
            }
            if(mutation=="side-revision")
            {doc=doc with{Features=doc.Features.SetItem(chain.Side.Id,chain.Side with{Result=chain.Side.Result with{Revision=GeometryRevisionId.New()}})};expected=HistoryResolutionStatus.Stale;}
            if(mutation=="order"){feature=feature with{Inputs=[..feature.Inputs.Reverse()]};expected=HistoryResolutionStatus.Stale;}
            if(mutation=="result"){feature=feature with{TopologyHistory=h with{ResultRevision=GeometryRevisionId.New()}};expected=HistoryResolutionStatus.Stale;}
            doc=doc with{Features=doc.Features.SetItem(feature.Id,feature)};
            var result=await kernel.TraceAsync(doc,TopologyReference.Box(doc,chain.Source.Id,BoxBoundary.XMin),chain.Last.Id,assets);
            Stopped(result,expected,feature.Id,terminal?1:0,2);
            Resolved(await kernel.TraceAsync(session.Snapshot,TopologyReference.Box(session.Snapshot,chain.Source.Id,BoxBoundary.XMin),chain.Last.Id,assets),2,chain.Last.Result);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task PendingInspectionHoldsConsumedAssetsAndReportsTheOriginalState()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var gate=new GatedResolver(kernel);
        Task<TopologyHistoryInspection> pending;DocumentSnapshot snapshot;
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Queued chain"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,false);snapshot=session.Snapshot;
            pending=TopologyHistoryInspection.InspectAsync(snapshot,TopologyReference.Box(snapshot,chain.Source.Id,BoxBoundary.XMin),chain.Last.Id,assets,gate);
            await session.ExecuteAsync(new EditDocumentCommand("Rename",d=>d with{Name="Changed"}));gate.Ready.SetResult();
            var inspection=await pending;Assert.False(inspection.IsCurrent(session.Snapshot));
            await session.UndoAsync();Assert.True(inspection.IsCurrent(session.Snapshot));
            gate=new GatedResolver(kernel);
            pending=TopologyHistoryInspection.InspectAsync(snapshot,TopologyReference.Box(snapshot,chain.Source.Id,BoxBoundary.XMin),chain.Last.Id,assets,gate);
        }
        Assert.True(assets.Count>0);gate.Ready.SetResult();var final=await pending;
        Assert.Equal(HistoryResolutionStatus.Resolved,final.Result.Status);Assert.True(final.IsCurrent(snapshot));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("m4t1h1-history.cadoryx")][InlineData("m4t1h2a-history.cadoryx")]
    public async Task FrozenLocalAdaptersCanFeedVerifiedCurrentBooleanHistory(string fixture)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        using(var loaded=await storage.LoadAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage",fixture),assets))
        await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Translate(200,0,0)),"Side",local.PartId));
            var side=session.Snapshot.Features.Values.Single(f=>f.Name=="Side");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,[side.OutputBodyId,local.OutputBodyId]));
            var last=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            var result=await kernel.TraceAsync(session.Snapshot,TopologyReference.Box(session.Snapshot,local.Inputs[0],BoxBoundary.YMin),last.Id,assets);
            Resolved(result,2,last.Result);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task EvidenceBudgetStopsBeforeLoadingAnOversizedSegment(bool slotBudget)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Evidence budget"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,false);var doc=session.Snapshot;var f=chain.Last;var h=f.TopologyHistory!;
            int extra=slotBudget?10:254;
            var clones=Enumerable.Range(0,extra).Select(_=>chain.Side with{Id=FeatureId.New(),OutputBodyId=BodyId.New()}).ToArray();
            var side=h.GetSource(0);if(slotBudget)side=side with{TopologyCount=100000};
            // Synthetic declarations only exercise pre-load budgets, never native correspondence.
            h=h with{AdditionalSources=[..h.AdditionalSources,..Enumerable.Repeat(side,extra)],
                Entries=[..h.Entries,..Enumerable.Range(2,extra).SelectMany(a=>h.Entries.Where(e=>e.SourceArgument==0).Select(e=>e with{SourceArgument=a}))]};
            f=f with{Inputs=[..f.Inputs,..clones.Select(c=>c.Id)],Recipe=((BooleanRecipe)f.Recipe) with{Inputs=[..((BooleanRecipe)f.Recipe).Inputs,..clones.Select(c=>c.Result)]},TopologyHistory=h};
            h.ValidateFor(f);doc=doc with{Features=doc.Features.SetItem(f.Id,f).AddRange(clones.Select(c=>KeyValuePair.Create(c.Id,c)))};
            var result=await kernel.TraceAsync(doc,TopologyReference.Box(doc,chain.Source.Id,BoxBoundary.XMin),f.Id,assets);
            Stopped(result,HistoryResolutionStatus.LimitExceeded,f.Id,1,2);Assert.Equal("HISTORY.EVIDENCE_BUDGET",result.Diagnostic);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task RecomputeWithoutIntermediateEvidenceClearsHistoryAndUndoRestoresTheWholeChain()
    {
        var assets=new MemoryAssetStore();var kernel=new DroppingKernel();var resolver=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Lost history"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,true);var before=session.Snapshot;var reference=TopologyReference.Box(before,chain.Source.Id,BoxBoundary.XMin);
            kernel.DropLocal=true;
            await session.ExecuteAsync(new RecomputeCommand(chain.Source.Id,new BoxRecipe(11,20,30,RigidTransform3d.Identity)));
            var after=session.Snapshot;Assert.Null(after.Features[chain.Local!.Id].TopologyHistory);Assert.NotNull(after.Features[chain.Last.Id].TopologyHistory);
            Stopped(await resolver.TraceAsync(after,reference,chain.Last.Id,assets),HistoryResolutionStatus.Unsupported,chain.Local.Id,0,3);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);Resolved(await resolver.TraceAsync(before,reference,chain.Last.Id,assets),3,chain.Last.Result);
            await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task CancellingAPendingInspectionReleasesItsAssetHold()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var cts=new CancellationTokenSource();Task<TopologyHistoryInspection> pending;
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Cancelled chain"),assets,kernel,new InlineSessionDispatcher()))
        {
            var chain=await Seed(session,false);var doc=session.Snapshot;
            pending=TopologyHistoryInspection.InspectAsync(doc,TopologyReference.Box(doc,chain.Source.Id,BoxBoundary.XMin),chain.Last.Id,assets,new GatedResolver(kernel),cts.Token);
        }
        Assert.True(assets.Count>0);cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Equal(0,assets.Count);
    }
    private sealed class DroppingKernel:IGeometryKernel
    {
        private readonly OcctGeometryKernel inner=new();public bool DropLocal;
        public string Version=>inner.Version;public bool Supports(GeometryRecipe recipe)=>inner.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {
            using var result=await inner.EvaluateAsync(recipe,assets,token);
            return new(result.Geometry,assets.Acquire(result.Geometry.AssetId)){TopologyHistory=DropLocal&&recipe is LocalFeatureRecipe?null:result.TopologyHistory};
        }
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
    private sealed class GatedResolver(ITopologyHistoryResolver inner):ITopologyHistoryResolver
    {
        public readonly TaskCompletionSource Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HistoryResolution> TraceAsync(DocumentSnapshot s,TopologyReference r,FeatureId t,IAssetStore a,CancellationToken token=default)
        {await Ready.Task.WaitAsync(token);return await inner.TraceAsync(s,r,t,a,token);}
    }
    private static void Resolved(HistoryResolution r,int steps,GeometryAssetRef geometry)
    {Assert.Equal(HistoryResolutionStatus.Resolved,r.Status);Assert.Equal(steps,r.PathLength);Assert.Equal(steps,r.CompletedSteps);Assert.Null(r.StoppedAt);Assert.Equal(geometry.AssetId,r.Target!.Asset);Assert.Equal(geometry.Revision,r.Target.Revision);}
    private static void Stopped(HistoryResolution r,HistoryResolutionStatus status,FeatureId stopped,int completed,int length)
    {Assert.Equal(status,r.Status);Assert.Null(r.Target);Assert.Equal(stopped,r.StoppedAt);Assert.Equal(completed,r.CompletedSteps);Assert.Equal(length,r.PathLength);}
    private sealed record Chain(FeatureDefinition Source,FeatureDefinition? Local,FeatureDefinition Tool,FeatureDefinition Cut,FeatureDefinition Side,FeatureDefinition Last);
    private static async Task<Chain> Seed(CadDocumentSession session,bool local)
    {
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Features.Values.Single();FeatureDefinition? rounded=null;
        if(local)
        {
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Fillet,1));
            rounded=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);Assert.NotNull(rounded.TopologyHistory);
        }
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,22,32,RigidTransform3d.Translate(4,-1,-1)),"Tool",source.PartId));
        var tool=session.Snapshot.Features.Values.Single(f=>f.Name=="Tool");
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[(rounded??source).OutputBodyId,tool.OutputBodyId]));
        var cut=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);Assert.NotNull(cut.TopologyHistory);
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Translate(40,0,0)),"Side",source.PartId));
        var side=session.Snapshot.Features.Values.Single(f=>f.Name=="Side");
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,[side.OutputBodyId,cut.OutputBodyId]));
        var last=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe b&&b.Operation==BooleanOperation.Fuse);Assert.NotNull(last.TopologyHistory);
        return new(source,rounded,tool,cut,side,last);
    }
}
