using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Xunit;

namespace Cadoryx.Tests;

public sealed class LocalFeatureEditorTests
{
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
