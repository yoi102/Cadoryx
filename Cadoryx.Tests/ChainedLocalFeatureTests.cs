using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ChainedLocalFeatureTests
{
    [Fact] public async Task AThirdFilletCanContinueThroughTwoVerifiedHistoryStepsAndFreezesOnUpstreamChange()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Three local steps"),assets,kernel,new InlineSessionDispatcher()))
        {
            var boxRecipe=new BoxRecipe(10,20,30,RigidTransform3d.Identity);
            await session.ExecuteAsync(new AddBodyCommand(boxRecipe,"Box"));var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,.5));
            var first=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var secondOrigin=TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            var secondTarget=await kernel.TraceAsync(session.Snapshot,secondOrigin,first.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Resolved,secondTarget.Status);
            await session.ExecuteAsync(new HistoryFilletCommand(secondOrigin,first.Id,.5,secondTarget.Target!));
            var second=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
            await using(var editor=new LocalFeatureViewModel(session,kernel))
            {
                Assert.Equal(second.Id,editor.SelectedBox!.FeatureId);
                editor.Pick(BoxBoundary.XMin,BoxBoundary.YMax);editor.Size=.5;
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var third=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe&&f.Id!=second.Id);
            Assert.Equal(second.Id,third.Inputs.Single());Assert.NotNull(third.TopologyHistory);
            Assert.True(third.Result.VolumeMm3<second.Result.VolumeMm3);
            var completed=session.Snapshot;
            await session.ExecuteAsync(new RecomputeCommand(box.Id,boxRecipe with{X=11}));
            Assert.True(session.Snapshot.Features[second.Id].IsStale);
            Assert.True(session.Snapshot.Features[third.Id].IsStale);
            await session.UndoAsync();Assert.Same(completed,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LocalResultCanBeSelectedAndContinuedOnlyThroughVerifiedOriginalEdge()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Chain"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,1));
            var first=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var before=session.Snapshot;var count=assets.Count;
            await using(var editor=new LocalFeatureViewModel(session,kernel))
            {
                Assert.Single(editor.Boxes);Assert.True(editor.SelectedBox!.IsChained);
                Assert.Equal(first.Id,editor.SelectedBox.FeatureId);
                Assert.Equal(box.Id,editor.SelectedBox.OriginBoxFeatureId);
                Assert.True(editor.CanChangeOperation);Assert.False(editor.CanSaveReference);
                editor.Pick(BoxBoundary.XMin,BoxBoundary.YMin);
                await editor.PreviewCommand.ExecuteAsync(null);
                Assert.False(editor.CanConfirm);
                Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
                editor.Pick(BoxBoundary.XMax,BoxBoundary.YMax);
                Assert.Equal(box.Id,editor.Selection!.FeatureId);
                Assert.Equal(first.Result,editor.Scene!.Items.Single().Geometry);
                editor.Size=.5;await editor.PreviewCommand.ExecuteAsync(null);
                Assert.True(editor.CanConfirm,editor.Status);
                Assert.Same(before,session.Snapshot);
                editor.Size=.75;Assert.False(editor.CanConfirm);Assert.Equal(count,assets.Count);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var second=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
            Assert.Equal(first.Id,second.Inputs.Single());Assert.NotNull(second.TopologyBinding);Assert.NotNull(second.TopologyHistory);
            Assert.True(second.Result.VolumeMm3<first.Result.VolumeMm3);
            var path=files.PathFor("chain.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
            Assert.Equal(second.Recipe,loaded.Snapshot.Features[second.Id].Recipe);
            Assert.Equal(second.TopologyBinding,loaded.Snapshot.Features[second.Id].TopologyBinding);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task BoundFilletCanBeEditedToVariableRadiusWithoutChangingIdentity()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Bound variable"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,.5));
            var first=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var origin=TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            var traced=await kernel.TraceAsync(session.Snapshot,origin,first.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Resolved,traced.Status);
            await session.ExecuteAsync(new HistoryFilletCommand(origin,first.Id,.5,traced.Target!));
            var bound=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
            var before=session.Snapshot;var count=assets.Count;
            await using(var editor=new LocalFeatureViewModel(session,kernel,bound.Id))
            {
                Assert.True(editor.IsBoundEditing);Assert.False(editor.CanChangeOperation);
                editor.UseEndRadius=true;editor.EndRadius=1.5;
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                Assert.Same(before,session.Snapshot);
                editor.EndRadius=2;Assert.False(editor.CanConfirm);Assert.Equal(count,assets.Count);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var edited=session.Snapshot.Features[bound.Id];
            Assert.Equal(bound.OutputBodyId,edited.OutputBodyId);Assert.Equal(2,((HistoryFilletRecipe)edited.Recipe).EndRadius);
            Assert.NotNull(edited.TopologyHistory);Assert.True(edited.Result.VolumeMm3<first.Result.VolumeMm3);
            var changed=session.Snapshot;
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(changed,session.Snapshot);
            var path=files.PathFor("bound-variable.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
            Assert.Equal(2,((HistoryFilletRecipe)loaded.Snapshot.Features[bound.Id].Recipe).EndRadius);
            Assert.Equal(edited.TopologyBinding,loaded.Snapshot.Features[bound.Id].TopologyBinding);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task V8BoundFilletMigratesToConstantAndBadEndRadiusIsRejected()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Bound migration"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,.5));
            var first=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var origin=TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            var target=(await kernel.TraceAsync(session.Snapshot,origin,first.Id,assets)).Target!;
            await session.ExecuteAsync(new HistoryFilletCommand(origin,first.Id,.5,target));
            var bound=session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
            var path=files.PathFor("v8-bound.cadoryx");await session.SaveAsync(storage,path);
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
                return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(f=>f.Recipe.Kind=="history-edge-fillet"
                    ?f with{Recipe=f.Recipe with{Numbers=f.Recipe.Numbers.Take(2).ToArray()}}:f).ToArray()});
            });
            FormatEvolutionTests.RewriteManifest(path,m=>m with{ApplicationVersion="0.4.14",
                Sections=[..m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=8}:s)],
                RequiredCapabilities=[..m.RequiredCapabilities.Where(c=>c!="cadoryx.bound-variable-radius.1")]});
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
                Assert.Null(((HistoryFilletRecipe)loaded.Snapshot.Features[bound.Id].Recipe).EndRadius);
                Assert.Equal(bound.Result,loaded.Snapshot.Features[bound.Id].Result);
            }
            var current=files.PathFor("bad-bound.cadoryx");await session.SaveAsync(storage,current);var count=assets.Count;
            FormatEvolutionTests.RewriteSection(current,"features",bytes=>
            {
                var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
                return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(f=>
                {
                    if(f.Recipe.Kind!="history-edge-fillet")return f;
                    var numbers=f.Recipe.Numbers.ToArray();numbers[2]=double.NaN;
                    return f with{Recipe=f.Recipe with{Numbers=numbers}};
                }).ToArray()});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(current,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
}
