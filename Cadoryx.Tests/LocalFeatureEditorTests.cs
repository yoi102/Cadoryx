using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Xunit;

namespace Cadoryx.Tests;

public sealed class LocalFeatureEditorTests
{
    [Fact] public async Task AddRemoveEdgesAndVariableRadiusPreviewRemainAtomic()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Multi editor"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var original=session.Snapshot;var count=assets.Count;
            await using(var editor=new LocalFeatureViewModel(session,kernel))
            {
                editor.Pick(BoxBoundary.XMin,BoxBoundary.YMin);editor.Size=.5;editor.AddEdgesOnPick=true;
                editor.Pick(BoxBoundary.XMax,BoxBoundary.YMax);
                Assert.Equal(LocalFeatureRecipe.EdgeBit(BoxBoundary.XMax,BoxBoundary.YMax),editor.AdditionalEdges);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                Assert.Same(original,session.Snapshot);
                editor.UseEndRadius=true;editor.EndRadius=2;
                Assert.False(editor.CanConfirm);Assert.Equal(count,assets.Count);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);
                editor.Pick(BoxBoundary.XMax,BoxBoundary.YMax);Assert.Equal(0,editor.AdditionalEdges);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            Assert.Equal(2,((LocalFeatureRecipe)local.Recipe).EndRadius);
            await using(var edit=new LocalFeatureViewModel(session,kernel,local.Id))
            {
                Assert.True(edit.UseEndRadius);Assert.Equal(2,edit.EndRadius);
                edit.UseEndRadius=false;
                edit.EdgeChoices.Single(c=>c.First==BoxBoundary.XMax&&c.Second==BoxBoundary.YMax).IsSelected=true;
                await edit.PreviewCommand.ExecuteAsync(null);Assert.True(edit.CanConfirm,edit.Status);
                edit.CancelCommand.Execute(null);
            }
            Assert.Equal(0,((LocalFeatureRecipe)session.Snapshot.Features[local.Id].Recipe).AdditionalEdges);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task TwoDistancesCanBeCreatedEditedAndCanceledWithoutLeakingCandidates()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Two distances"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await using(var create=new LocalFeatureViewModel(session,kernel))
            {
                create.Pick(BoxBoundary.YMin,BoxBoundary.ZMax);create.Operation=LocalFeatureOperation.Chamfer;
                create.Size=2;create.UseTwoDistances=true;create.SecondDistance=3;
                await create.PreviewCommand.ExecuteAsync(null);Assert.True(create.CanConfirm,create.Status);
                Assert.Equal(5970,create.Scene!.Items.Single().Geometry.VolumeMm3,4);
                await create.ConfirmCommand.ExecuteAsync(null);
            }
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            Assert.Equal(3,((LocalFeatureRecipe)local.Recipe).SecondDistance);
            var before=session.Snapshot;var count=assets.Count;
            await using(var edit=new LocalFeatureViewModel(session,kernel,local.Id))
            {
                Assert.True(edit.UseTwoDistances);Assert.Equal(3,edit.SecondDistance);
                edit.SecondDistance=4;await edit.PreviewCommand.ExecuteAsync(null);
                Assert.True(edit.CanConfirm,edit.Status);Assert.Equal(5960,edit.Scene!.Items.Single().Geometry.VolumeMm3,4);
                edit.UseTwoDistances=false;Assert.False(edit.CanConfirm);Assert.Equal(count,assets.Count);
                await edit.PreviewCommand.ExecuteAsync(null);Assert.True(edit.CanConfirm,edit.Status);
                Assert.Equal(5980,edit.Scene!.Items.Single().Geometry.VolumeMm3,4);
                edit.CancelCommand.Execute(null);
            }
            Assert.Same(before,session.Snapshot);
            await session.ExecuteAsync(new RecomputeCommand(box.Id,new BoxRecipe(11,20,30,RigidTransform3d.Identity)));
            Assert.Equal(6567,session.Snapshot.Features[local.Id].Result.VolumeMm3,4);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task EditPreviewCancelConfirmUndoRedoAndStoragePreserveFeatureIdentity()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Edit local"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var source=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Fillet,1));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var before=session.Snapshot;var count=assets.Count;
            await using(var editor=new LocalFeatureViewModel(session,kernel,local.Id))
            {
                Assert.True(editor.IsEditing);Assert.False(editor.CanChooseSource);
                Assert.Equal(LocalFeatureOperation.Fillet,editor.Operation);Assert.Equal(1,editor.Size);
                editor.Operation=LocalFeatureOperation.Chamfer;editor.Size=2;
                await editor.PreviewCommand.ExecuteAsync(null);
                Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
                Assert.Equal(5980,editor.Scene!.Items.Single().Geometry.VolumeMm3,4);
                Assert.True(assets.Count>count);
                editor.Size=3;Assert.False(editor.CanConfirm);Assert.Equal(count,assets.Count);
                editor.CancelCommand.Execute(null);Assert.Same(before,session.Snapshot);
            }
            await using(var editor=new LocalFeatureViewModel(session,kernel,local.Id))
            {
                editor.Operation=LocalFeatureOperation.Chamfer;editor.Size=2;
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var after=session.Snapshot;var edited=after.Features[local.Id];
            Assert.Equal(local.OutputBodyId,edited.OutputBodyId);
            Assert.Equal(2,((LocalFeatureRecipe)edited.Recipe).Size);
            Assert.Equal(LocalFeatureOperation.Chamfer,((LocalFeatureRecipe)edited.Recipe).Operation);
            Assert.Equal(5980,edited.Result.VolumeMm3,4);
            Assert.NotEqual(local.Result.Revision,edited.Result.Revision);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var path=files.PathFor("edited-local.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
            loaded.Snapshot.Validate();Assert.Equal(edited.Recipe,loaded.Snapshot.Features[local.Id].Recipe);
            Assert.Equal(edited.Result.VolumeMm3,loaded.Snapshot.Features[local.Id].Result.VolumeMm3,4);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task EditingConsumedLocalFeatureRecomputesBooleanDescendant()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Local descendant"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Fillet,1));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Translate(200,0,0)),"Side",local.PartId));
            var side=session.Snapshot.Features.Values.Single(f=>f.Name=="Side");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,[local.OutputBodyId,side.OutputBodyId]));
            var before=session.Snapshot;var last=before.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            Assert.False(before.Bodies.ContainsKey(local.OutputBodyId));
            await using(var editor=new LocalFeatureViewModel(session,kernel,local.Id))
            {
                Assert.Equal(local.Result,editor.Scene!.Items.Single().Geometry);
                editor.Operation=LocalFeatureOperation.Chamfer;editor.Size=2;
                await editor.PreviewCommand.ExecuteAsync(null);
                Assert.True(editor.CanConfirm,editor.Status);
                Assert.Equal(5980,editor.Scene!.Items.Single().Geometry.VolumeMm3,4);
                Assert.Same(before,session.Snapshot);
                await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var after=session.Snapshot;
            Assert.Equal(5980,after.Features[local.Id].Result.VolumeMm3,4);
            Assert.Equal(6004,after.Features[last.Id].Result.VolumeMm3,4);
            Assert.Equal(last.OutputBodyId,after.Features[last.Id].OutputBodyId);
            Assert.False(after.Features[last.Id].IsStale);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task InvalidEditAndExternalChangeLeaveOriginalOutputUntouched()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Rejected local edit"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Fillet,1));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var before=session.Snapshot;var count=assets.Count;
            await using(var editor=new LocalFeatureViewModel(session,kernel,local.Id))
            {
                editor.Size=1000;await editor.PreviewCommand.ExecuteAsync(null);
                Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
                editor.Size=2;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await session.ExecuteAsync(new EditDocumentCommand("Rename",d=>d with{Name="Changed"}));
                var changed=session.Snapshot;
                Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);
                await editor.ConfirmCommand.ExecuteAsync(null);Assert.Same(changed,session.Snapshot);
                Assert.Equal(before.Features[local.Id].Result,changed.Features[local.Id].Result);
            }
            var layer=session.Snapshot.Layers.Values.Single();
            await session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsLocked=true}));
            var locked=session.Snapshot;count=assets.Count;
            await using(var editor=new LocalFeatureViewModel(session,kernel,local.Id))
            {
                editor.Size=2;await editor.PreviewCommand.ExecuteAsync(null);
                Assert.False(editor.CanConfirm);Assert.Same(locked,session.Snapshot);Assert.Equal(count,assets.Count);
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task PreviewChangesAreUncommittedAndInputChangeCancelsCandidate()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var before=session.Snapshot;int count=assets.Count;
            await using(var editor=new LocalFeatureViewModel(session,kernel))
            {
                editor.Pick(BoxBoundary.YMin,BoxBoundary.ZMax);await editor.PreviewCommand.ExecuteAsync(null);
                Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);Assert.True(assets.Count>count);
                editor.Size=3;Assert.False(editor.CanConfirm);Assert.Equal(count,assets.Count);
                await editor.PreviewCommand.ExecuteAsync(null);editor.CancelCommand.Execute(null);
                Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task DocumentChangeInvalidatesCandidateAndCannotConfirmOldResult()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        await using var editor=new LocalFeatureViewModel(session,kernel);editor.Pick(BoxBoundary.YMin,BoxBoundary.ZMax);await editor.PreviewCommand.ExecuteAsync(null);
        await session.ExecuteAsync(new EditDocumentCommand("Rename",s=>s with{Name="Changed"}));var changed=session.Snapshot;
        Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);await editor.ConfirmCommand.ExecuteAsync(null);Assert.Same(changed,session.Snapshot);
    }
    [Fact] public async Task ReferenceInspectionAndExplicitReselectionPreserveReferenceIdentity()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var f=session.Snapshot.Features.Values.Single();
        var reference=TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.XMin,policy:TopologyRebindPolicy.ExactRevision);
        await session.ExecuteAsync(new UpsertTopologyReferenceCommand(reference));await session.ExecuteAsync(new RecomputeCommand(f.Id,new BoxRecipe(20,20,30,RigidTransform3d.Identity)));
        await using var editor=new LocalFeatureViewModel(session,kernel);editor.SelectedReference=editor.References.Single();await editor.InspectReferenceCommand.ExecuteAsync(null);
        Assert.Equal(Cadoryx.Lang.Strings.Strings.ReferenceStale,editor.Status);editor.Pick(BoxBoundary.ZMax,null);Assert.False(editor.CanPreview);
        await editor.SaveReferenceCommand.ExecuteAsync(null);var updated=session.Snapshot.TopologyReferences[reference.Id];
        Assert.Equal(BoxBoundary.ZMax,updated.Boundary);Assert.NotEqual(reference.OriginRevision,updated.OriginRevision);
        Assert.Equal(TopologyResolutionStatus.Resolved,(await kernel.ResolveAsync(session.Snapshot,updated,assets)).Status);
        await session.UndoAsync();Assert.Equal(reference,session.Snapshot.TopologyReferences[reference.Id]);
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(double.NaN)][InlineData(1000)]
    public async Task InvalidSizeOrNativeFailureLeavesDocumentAndAssetsUnchanged(double size)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var before=session.Snapshot;int count=assets.Count;
        await using var editor=new LocalFeatureViewModel(session,kernel);editor.Pick(BoxBoundary.YMin,BoxBoundary.ZMax);editor.Size=size;
        await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
    }
    [Fact] public async Task PreviewCanceledDuringNativeWorkCannotPublishAndReleasesCandidate()
    {
        var assets=new MemoryAssetStore();var kernel=new DelayedKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var before=session.Snapshot;int count=assets.Count;
        await using var editor=new LocalFeatureViewModel(session,kernel);editor.Pick(BoxBoundary.YMin,BoxBoundary.ZMax);
        var task=editor.PreviewCommand.ExecuteAsync(null);await kernel.Started.Task;editor.CancelCommand.Execute(null);kernel.Release.SetResult();await task;
        Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
    }
    private sealed class DelayedKernel:IGeometryKernel
    {
        private readonly OcctGeometryKernel inner=new();public string Version=>inner.Version;public bool Supports(GeometryRecipe r)=>inner.Supports(r);
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken token=default)
        {if(r is LocalFeatureRecipe){Started.SetResult();await Release.Task;return await inner.EvaluateAsync(r,a,CancellationToken.None);}return await inner.EvaluateAsync(r,a,token);}
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
