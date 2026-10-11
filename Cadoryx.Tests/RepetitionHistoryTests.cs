using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class RepetitionHistoryTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();
        internal CadDocumentSession Session {get;}internal FeatureDefinition Source {get;private set;}=null!;
        internal Fixture()=>Session=new(DocumentSnapshot.Create("Copy history"),Assets,Kernel,new InlineSessionDispatcher());
        internal async Task Init(bool compound=false)
        {
            if(compound)
            {
                using var a=ShapeFactory.CreateBox(2,3,4);using var b=ShapeFactory.CreateBox(1,2,3);
                using var shift=GpTrsf.Create(0,8,0);using var placed=b.Transformed(shift);using var shape=ShapeFactory.CreateCompound([a,placed]);
                using var stored=OcctGeometryBridge.StoreShape(shape,Assets);
                await Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Two solids"));
            }
            else await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Box"));
            Source=Session.Snapshot.Features.Values.Single();
        }
        internal RepetitionRecipe Recipe(RepetitionKind kind=RepetitionKind.Linear,int count=3,double step=7,
            RepetitionOutput output=RepetitionOutput.Compound,bool include=true,bool full=false,RigidTransform3d? frame=null)=>
            new(Source.Result,kind,frame??RigidTransform3d.Identity,count,step,Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToImmutableArray(),output,include,full);
        internal async Task<FeatureDefinition> Repeat(RepetitionRecipe recipe)
        {
            await Session.ExecuteAsync(new AddRepetitionCommand(Source.OutputBodyId,recipe,"Copies"));
            return Session.Snapshot.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);
        }
        internal ExactTopologySelection Pick(FeatureDefinition feature,HistoryShapeKind kind,int? index=null)
        {
            using var shape=OcctGeometryBridge.ReadShape(feature.Result,Assets);using var map=RepairSnapshot.Create(shape);
            int slot=index??map.Topology.First(t=>t.Kind==(kind==HistoryShapeKind.Face?ShapeKind.Face:ShapeKind.Edge)).Selection.Index;
            return new(Session.Snapshot.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,slot,kind,OcctGeometryKernel.HistoryAdapterVersion);
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }

    [Theory][InlineData(false,RepetitionKind.Linear)][InlineData(true,RepetitionKind.Linear)]
    [InlineData(false,RepetitionKind.Circular)][InlineData(true,RepetitionKind.Mirror)]
    public async Task CompleteFaceEdgeVertexPathsRemainDistinctForEveryCopyAndReopen(bool compound,RepetitionKind kind)
    {
        await using var f=new Fixture();await f.Init(compound);using var files=new TestFiles();
        var frame=kind==RepetitionKind.Mirror?new RigidTransform3d(new(9,-2,1),Quaterniond.FromAxisAngle(new(1,2,3),.317)):
            kind==RepetitionKind.Circular?RigidTransform3d.Translate(-8,0,0):new(new(7,2,1),Quaterniond.FromAxisAngle(new(1,2,3),.317));
        var recipe=f.Recipe(kind,kind==RepetitionKind.Mirror?2:3,kind==RepetitionKind.Mirror?0:kind==RepetitionKind.Circular?0:15,
            full:kind==RepetitionKind.Circular,frame:frame);
        var source=f.Source;var selected=f.Pick(source,HistoryShapeKind.Edge);byte[] bytes;
        using(var hold=f.Assets.Acquire(source.Result.AssetId))bytes=hold.Content.ToArray();
        var before=f.Session.Snapshot;var result=await f.Repeat(recipe);var after=f.Session.Snapshot;
        var history=Assert.IsType<TopologyHistory>(result.TopologyHistory);Assert.Equal(4,history.SchemaVersion);history.ValidateFor(result);
        Assert.Equal(OcctGeometryKernel.RepetitionHistoryAdapterVersion,history.AdapterVersion);
        Assert.Equal((compound?30:24)*recipe.Count,result.Result.VolumeMm3,6);
        using(var shape=OcctGeometryBridge.ReadShape(source.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
        {
            var slots=map.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex).ToArray();
            foreach(var copyId in recipe.CopyIds)foreach(var slot in slots)
            {
                var type=slot.Kind==ShapeKind.Face?HistoryShapeKind.Face:slot.Kind==ShapeKind.Edge?HistoryShapeKind.Edge:HistoryShapeKind.Vertex;
                var row=Assert.Single(history.Entries,e=>e.CopyId==copyId&&e.SourceIndex==slot.Selection.Index);
                Assert.False(row.PathAmbiguous);Assert.Equal(type,row.SourceKind);Assert.Equal(type,row.ResultKind);
                Assert.Equal(HistoryResolutionStatus.Ambiguous,TopologyHistoryReduction.Resolve(history,slot.Selection.Index,type).Status);
                Assert.Equal(HistoryResolutionStatus.Resolved,TopologyHistoryReduction.ResolveCopy(history,copyId,slot.Selection.Index,type).Status);
            }
        }
        Assert.Equal(HistoryResolutionStatus.Ambiguous,(await f.Kernel.TraceExactAsync(after,selected,result.Id,f.Assets)).Status);
        var resolved=await f.Kernel.TraceCopyAsync(after,selected,result.Id,result.Id,recipe.CopyIds[1],f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,resolved.Status);Assert.Equal(1,resolved.CompletedSteps);
        Assert.NotNull(resolved.VerifiedSelection);await f.Kernel.ConfirmAsync(after,resolved.VerifiedSelection,f.Assets);
        Assert.Equal(TopologyHistoryReduction.ResolveCopy(history,recipe.CopyIds[1],selected.FullTopologyIndex,selected.Kind).Target,resolved.Target);
        using(var hold=f.Assets.Acquire(source.Result.AssetId))Assert.Equal(bytes,hold.Content.ToArray());
        string path=files.PathFor("copies.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["document"].Version,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        Assert.Equal(4,manifest.Sections.Single(s=>s.Kind=="history").SchemaVersion);Assert.Contains("cadoryx.multicopy-history.1",manifest.RequiredCapabilities);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets))
        {
            Assert.Equal(history.Entries.ToArray(),loaded.Snapshot.Features[result.Id].TopologyHistory!.Entries.ToArray());
            Assert.Equal(resolved,await f.Kernel.TraceCopyAsync(loaded.Snapshot,selected,result.Id,result.Id,recipe.CopyIds[1],f.Assets));
        }
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SingleObliqueReflectionCanContinueThroughAnotherReflectionAndExactFillet(bool imported)
    {
        await using var f=new Fixture();await f.Init(imported);
        var selected=f.Pick(f.Source,HistoryShapeKind.Edge);
        var frame=new RigidTransform3d(new(7,3,-1),Quaterniond.FromAxisAngle(new(2,3,1),.429));
        var first=await f.Repeat(f.Recipe(RepetitionKind.Mirror,1,0,include:false,frame:frame));
        var ids=Assert.IsType<RepetitionRecipe>(first.Recipe).CopyIds;
        var trace=await f.Kernel.TraceExactAsync(f.Session.Snapshot,selected,first.Id,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,trace.Status);
        var secondRecipe=new RepetitionRecipe(first.Result,RepetitionKind.Mirror,frame,1,0,[Guid.NewGuid()],IncludeSource:false);
        await f.Session.ExecuteAsync(new AddRepetitionCommand(first.OutputBodyId,secondRecipe,"Twice"));
        var second=f.Session.Snapshot.Features.Values.Single(v=>v.Name=="Twice");
        Assert.Equal(HistoryResolutionStatus.Resolved,(await f.Kernel.TraceExactAsync(f.Session.Snapshot,selected,second.Id,f.Assets)).Status);
        Assert.Equal(imported?30:24,second.Result.VolumeMm3,6);
        using var original=OcctGeometryBridge.ReadShape(f.Source.Result,f.Assets);using var actual=OcctGeometryBridge.ReadShape(second.Result,f.Assets);
        using var excess=actual.Cut(original);using var missing=original.Cut(actual);
        Assert.Equal(0,excess.GetTopologySummary().UniqueCounts.SolidCount);Assert.Equal(0,missing.GetTopologySummary().UniqueCounts.SolidCount);
        Assert.Equal(ids.ToArray(),((RepetitionRecipe)first.Recipe).CopyIds.ToArray());
        var finalSelection=f.Pick(second,HistoryShapeKind.Edge);
        await f.Session.ExecuteAsync(new ExactLocalFeatureCommand(finalSelection,Cadoryx.Db.LocalFeatureOperation.Fillet,.1));
        var local=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is HistoryFilletRecipe);Assert.NotNull(local.TopologyHistory);
        Assert.True(local.Result.VolumeMm3<second.Result.VolumeMm3);
    }

    [Theory][InlineData(1,48)][InlineData(2,72)][InlineData(7,72)]
    public async Task FusionPreservesAllCopyOwnersAndStopsAtNativeSharedOrSplitTargets(double step,double volume)
    {
        await using var f=new Fixture();await f.Init();var recipe=f.Recipe(step:step,output:RepetitionOutput.Fused);
        var result=await f.Repeat(recipe);var history=Assert.IsType<TopologyHistory>(result.TopologyHistory);
        Assert.Equal(volume,result.Result.VolumeMm3,6);history.ValidateFor(result);
        Assert.Equal(recipe.CopyIds.Order(),history.Entries.Select(e=>e.CopyId!.Value).Distinct().Order());
        var shared=history.Entries.Where(e=>e.ResultIndex.HasValue).GroupBy(e=>e.ResultIndex)
            .Where(g=>g.Select(e=>(e.SourceIndex,e.CopyId)).Distinct().Count()>1).ToArray();
        if(step<=2)
        {
            Assert.NotEmpty(shared);
            foreach(var row in shared.SelectMany(g=>g))
            {
                Assert.True(row.PathAmbiguous);
                var resolved=TopologyHistoryReduction.ResolveCopy(history,row.CopyId!.Value,row.SourceIndex,row.SourceKind);
                Assert.NotEqual(HistoryResolutionStatus.Resolved,resolved.Status);Assert.Null(resolved.Target);
            }
        }
        else Assert.Empty(shared);
        var edge=f.Pick(f.Source,HistoryShapeKind.Edge);
        Assert.NotEqual(HistoryResolutionStatus.Resolved,(await f.Kernel.TraceExactAsync(f.Session.Snapshot,edge,result.Id,f.Assets)).Status);
    }

    [Fact] public async Task CopyIdentityIsAnExplicitPathConstraintAndTamperedSameKindMapFailsNativeReplay()
    {
        await using var f=new Fixture();await f.Init();var selected=f.Pick(f.Source,HistoryShapeKind.Face);var recipe=f.Recipe();var result=await f.Repeat(recipe);
        var doc=f.Session.Snapshot;int count=f.Assets.Count;
        Assert.Equal(HistoryResolutionStatus.WrongContext,(await f.Kernel.TraceCopyAsync(doc,selected,result.Id,result.Id,Guid.NewGuid(),f.Assets)).Status);
        Assert.Equal(HistoryResolutionStatus.WrongContext,(await f.Kernel.TraceCopyAsync(doc,selected,result.Id,f.Source.Id,recipe.CopyIds[0],f.Assets)).Status);
        var history=result.TopologyHistory!;var faces=history.Entries.Where(e=>e.CopyId==recipe.CopyIds[1]&&e.SourceKind==HistoryShapeKind.Face).Take(2).ToArray();
        var forged=history with{Entries=history.Entries.Select(e=>e==faces[0]?e with{ResultIndex=faces[1].ResultIndex}:
            e==faces[1]?e with{ResultIndex=faces[0].ResultIndex}:e).ToImmutableArray()};
        var fake=doc with{Features=doc.Features.SetItem(result.Id,result with{TopologyHistory=forged})};fake.Validate();
        var failed=await f.Kernel.TraceCopyAsync(fake,selected,result.Id,result.Id,recipe.CopyIds[1],f.Assets);
        Assert.Equal(HistoryResolutionStatus.Unsupported,failed.Status);Assert.Equal("HISTORY.NATIVE_REPLAY_MISMATCH",failed.Diagnostic);Assert.Null(failed.Target);
        Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task OlderHistoryEnvelopeCannotSmuggleCopyWitnesses(int version)
    {
        await using var f=new Fixture();await f.Init();await f.Repeat(f.Recipe());using var files=new TestFiles();string path=files.PathFor("smuggle.cadoryx");
        await f.Session.SaveAsync(new CadDocumentStorage(),path);int count=f.Assets.Count;
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="history"?s with{SchemaVersion=version}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }

    [Fact] public async Task LegacyNoHistoryMigratesWithoutInventingWitnessesAndNewRecipeRecomputeRetainsCopyIds()
    {
        await using var f=new Fixture();await f.Init();var recipe=f.Recipe();var feature=await f.Repeat(recipe);using var files=new TestFiles();
        var old=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,feature with{TopologyHistory=null})};
        string path=files.PathFor("legacy.cadoryx");await new CadDocumentStorage().SaveAsync(old,f.Assets,path);
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=43}:s.Kind=="history"?s with{SchemaVersion=3}:s).ToImmutableArray()});
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets))
        {Assert.Null(loaded.Snapshot.Features[feature.Id].TopologyHistory);Assert.Equal(recipe.CopyIds.ToArray(),Assert.IsType<RepetitionRecipe>(loaded.Snapshot.Features[feature.Id].Recipe).CopyIds.ToArray());}
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(3,3,4,RigidTransform3d.Identity)));
        var updated=f.Session.Snapshot.Features[feature.Id];Assert.Equal(recipe.CopyIds,Assert.IsType<RepetitionRecipe>(updated.Recipe).CopyIds);
        Assert.Equal(108,updated.Result.VolumeMm3,6);Assert.NotNull(updated.TopologyHistory);updated.TopologyHistory!.ValidateFor(updated);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,true));Assert.Null(f.Session.Snapshot.Features[feature.Id].TopologyHistory);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,false));Assert.NotNull(f.Session.Snapshot.Features[feature.Id].TopologyHistory);
        var resized=Assert.IsType<RepetitionRecipe>(f.Session.Snapshot.Features[feature.Id].Recipe).Resize(2);
        await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,resized));Assert.Equal(recipe.CopyIds.Take(2),((RepetitionRecipe)f.Session.Snapshot.Features[feature.Id].Recipe).CopyIds);
        int count=f.Assets.Count;using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Kernel.EvaluateAsync(resized,f.Assets,cancelled.Token));Assert.Equal(count,f.Assets.Count);
    }
}
