using System.Collections.Immutable;
using System.Security.Cryptography;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class TopologyReferenceTests
{
    [Fact] public void PackageHistoryReportsInputGroupsRatherThanStableSourceSubshapeIds()
    {
        using var a=ShapeFactory.CreateBox(10,20,30);using var b=ShapeFactory.CreateBox(5,5,5);
        using var result=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[a],[b],new FeatureModelingOptions{NonDestructive=true,RunParallel=false});
        Assert.True(result.RequireShape().IsValid);Assert.NotEmpty(result.History);
        Assert.All(result.History,h=>{Assert.InRange(h.SourceIndex,0,1);Assert.True(Enum.IsDefined(h.Kind));Assert.True(h.Shape.IsValid);});
        // The installed package's public record lacks a source subshape identity. Do not invent one from SourceIndex.
        Assert.Null(typeof(FeatureHistoryItem).GetProperty("SourceSubshapeId"));
    }
    [Fact] public async Task LockedOutputRejectsRegistrationWithoutChangingHistory()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(Cadoryx.Db.DocumentSnapshot.Create("Topology"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        var r=TopologyReference.Box(session.Snapshot,session.Snapshot.Features.Keys.Single(),BoxBoundary.XMin);
        var layer=session.Snapshot.Layers.Values.Single();await session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsLocked=true}));var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertTopologyReferenceCommand(r)));Assert.Same(before,session.Snapshot);
    }
    [Fact] public async Task VerySmallBoxesAreUnsupportedRegardlessOfUserTolerance()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(Cadoryx.Db.DocumentSnapshot.Create("Topology"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(0.00005,20,30,RigidTransform3d.Identity),"Box"));
        var r=TopologyReference.Box(session.Snapshot,session.Snapshot.Features.Keys.Single(),BoxBoundary.XMin);
        var result=await kernel.ResolveAsync(session.Snapshot with{Settings=new(LinearToleranceMm:100)},r,assets);
        Assert.Equal(TopologyResolutionStatus.Unsupported,result.Status);Assert.Null(result.Target);
    }
    [Fact] public async Task AllSixFacesAndTwelveEdgesResolveAfterRigidPlacement()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(Cadoryx.Db.DocumentSnapshot.Create("Topology"),assets,kernel,new InlineSessionDispatcher()))
        {
            var placement=new RigidTransform3d(new(31,-17,4),Quaterniond.FromAxisAngle(new(1,2,3),0.7));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,placement),"Box"));
            var feature=session.Snapshot.Features.Values.Single();var faceIndices=new HashSet<int>();var edgeIndices=new HashSet<int>();
            foreach(var boundary in Enum.GetValues<BoxBoundary>())
            {
                var reference=TopologyReference.Box(session.Snapshot,feature.Id,boundary);
                var result=await kernel.ResolveAsync(session.Snapshot,reference,assets);
                Assert.Equal(TopologyResolutionStatus.Resolved,result.Status);Assert.Equal(feature.Result.Revision,result.Target!.Revision);
                Assert.True(faceIndices.Add(result.Target.Index));
                foreach(var second in Enum.GetValues<BoxBoundary>().Where(b=>(int)b/2>(int)boundary/2))
                {
                    result=await kernel.ResolveAsync(session.Snapshot,TopologyReference.Box(session.Snapshot,feature.Id,boundary,second),assets);
                    Assert.Equal(TopologyResolutionStatus.Resolved,result.Status);Assert.True(edgeIndices.Add(result.Target!.Index));
                }
            }
            Assert.Equal(6,faceIndices.Count);Assert.Equal(12,edgeIndices.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task RecomputeRebindsSemanticReferenceButStalesExactReferenceAndUndoRestoresBoth()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(Cadoryx.Db.DocumentSnapshot.Create("Topology"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var f=session.Snapshot.Features.Values.Single();
            var semantic=TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.XMax,BoxBoundary.ZMax);
            var exact=TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.XMax,policy:TopologyRebindPolicy.ExactRevision);
            await session.ExecuteAsync(new UpsertTopologyReferenceCommand(semantic));await session.ExecuteAsync(new UpsertTopologyReferenceCommand(exact));var before=session.Snapshot;
            var inspection=await TopologyReferenceInspection.InspectAsync(before,assets,kernel);Assert.True(inspection.IsCurrent(before));Assert.Equal(2,inspection.Results.Length);
            await session.ExecuteAsync(new RecomputeCommand(f.Id,new BoxRecipe(40,20,30,RigidTransform3d.Translate(10,20,30))));var after=session.Snapshot;
            Assert.False(inspection.IsCurrent(after));
            var resolved=await kernel.ResolveAsync(after,semantic,assets);Assert.Equal(TopologyResolutionStatus.Resolved,resolved.Status);
            Assert.NotEqual(semantic.OriginRevision,resolved.Target!.Revision);
            var stale=await kernel.ResolveAsync(after,exact,assets);Assert.Equal(TopologyResolutionStatus.Stale,stale.Status);Assert.Null(stale.Target);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertTopologyReferenceCommand(semantic)));Assert.Same(after,session.Snapshot);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);Assert.Equal(TopologyResolutionStatus.Resolved,(await kernel.ResolveAsync(session.Snapshot,exact,assets)).Status);
            await session.RedoAsync();Assert.Same(after,session.Snapshot);
            string path=files.PathFor("references.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                Assert.Equal(semantic,loaded.Snapshot.TopologyReferences[semantic.Id]);Assert.Equal(exact,loaded.Snapshot.TopologyReferences[exact.Id]);
                Assert.Equal(resolved,await kernel.ResolveAsync(loaded.Snapshot,semantic,assets));
                Assert.Equal(TopologyResolutionStatus.Stale,(await kernel.ResolveAsync(loaded.Snapshot,exact,assets)).Status);
                Assert.Equal(8,FormatEvolutionTests.Manifest(path).Sections.Length);
            }
            await session.ExecuteAsync(new RemoveTopologyReferenceCommand(semantic.Id));Assert.False(session.Snapshot.TopologyReferences.ContainsKey(semantic.Id));
            await session.UndoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task WrongContextMissingProducerUnsupportedHistoryAndCancellationNeverProduceTarget()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(Cadoryx.Db.DocumentSnapshot.Create("Topology"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var f=session.Snapshot.Features.Values.Single();
        var r=TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.XMin);
        foreach(var pair in new[]{(r with{DocumentId=DocumentId.New()},TopologyResolutionStatus.WrongContext),
            (r with{FeatureId=FeatureId.New()},TopologyResolutionStatus.Missing),(r with{OutputBodyId=BodyId.New()},TopologyResolutionStatus.WrongContext)})
        {var result=await kernel.ResolveAsync(session.Snapshot,pair.Item1,assets);Assert.Equal(pair.Item2,result.Status);Assert.Null(result.Target);}
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(5,5,5,RigidTransform3d.Identity),"Tool"));
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,session.Snapshot.Bodies.Keys));var boolean=session.Snapshot.Features.Values.Single(x=>x.Recipe is BooleanRecipe);
        var unsupported=r with{FeatureId=boolean.Id,OutputBodyId=boolean.OutputBodyId,OriginRevision=boolean.Result.Revision};
        Assert.Equal(TopologyResolutionStatus.Unsupported,(await kernel.ResolveAsync(session.Snapshot,unsupported,assets)).Status);
        // A consumed source reference continues to mean that source, never the downstream boolean's face.
        Assert.Equal(TopologyResolutionStatus.Resolved,(await kernel.ResolveAsync(session.Snapshot,r,assets)).Status);
        var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertTopologyReferenceCommand(unsupported)));Assert.Same(before,session.Snapshot);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.ResolveAsync(session.Snapshot,r,assets,cancel.Token));
        Assert.Same(before,session.Snapshot);
    }

    [Fact] public async Task DuplicatedAndMissingNativeCandidatesDoNotFallBackToFirstFace()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(Cadoryx.Db.DocumentSnapshot.Create("Topology"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var f=session.Snapshot.Features.Values.Single();
        var r=TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.XMin);
        using var a=ShapeFactory.CreateBox(10,20,30);using var b=ShapeFactory.CreateBox(10,20,30);using var duplicate=ShapeFactory.CreateCompound([a,b]);
        using var duplicated=OcctGeometryBridge.StoreShape(duplicate,assets);
        var snapshot=session.Snapshot with{Features=session.Snapshot.Features.SetItem(f.Id,f with{Result=duplicated.Geometry})};
        var result=await kernel.ResolveAsync(snapshot,r,assets);Assert.Equal(TopologyResolutionStatus.Ambiguous,result.Status);Assert.Equal(2,result.CandidateCount);Assert.Null(result.Target);
        using var smaller=ShapeFactory.CreateBox(5,5,5);using var missing=OcctGeometryBridge.StoreShape(smaller,assets);
        snapshot=session.Snapshot with{Features=session.Snapshot.Features.SetItem(f.Id,f with{Result=missing.Geometry})};
        result=await kernel.ResolveAsync(snapshot,r,assets);Assert.Equal(TopologyResolutionStatus.Missing,result.Status);Assert.Null(result.Target);
    }

    [Fact] public async Task FrozenS2MigratesWithoutInventingTopologyOrChangingSketchLinks()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m4s2-linked.cadoryx");
        Assert.Equal("63f441927e25171e44c7f882e0d139943495337e34cd35644224d09470c7cbd7",Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        using(var legacy=await storage.LoadAsync(path,assets))
        {
            Assert.Empty(legacy.Snapshot.TopologyReferences);Assert.Contains(legacy.Diagnostics,d=>d.Code=="IO.MIGRATED");
            Assert.Contains(legacy.Snapshot.Features.Values,f=>f.SketchSource is not null);
            string current=files.PathFor("current.cadoryx");await storage.SaveAsync(legacy.Snapshot,assets,current);
            using var loaded=await storage.LoadAsync(current,assets);Assert.Empty(loaded.Diagnostics);Assert.Equal(legacy.Snapshot.StateId,loaded.Snapshot.StateId);
            Assert.Equal(legacy.Snapshot.ReferencedAssets().OrderBy(a=>a.Sha256),loaded.Snapshot.ReferencedAssets().OrderBy(a=>a.Sha256));
        }
        Assert.Equal(0,assets.Count);
    }
}
