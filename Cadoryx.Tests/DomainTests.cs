using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
namespace Cadoryx.Tests;
public sealed class DomainTests
{
    [Fact] public void OccurrencePathsCompareByContents()
    {
        var document=DocumentId.New();var slot=ComponentSlotId.New();
        Assert.Equal(new OccurrencePath(document,[slot]),new OccurrencePath(document,[slot]));
        Assert.NotEqual(new OccurrencePath(document,[slot]),new OccurrencePath(document,[ComponentSlotId.New()]));
    }
    [Fact] public void RigidCompositionUsesParentBeforeLocal()
    {
        var parent=new RigidTransform3d(new(10,0,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2));
        var local=RigidTransform3d.Translate(2,0,0);var world=parent*local;
        Assert.Equal(10,world.Translation.X,10);Assert.Equal(2,world.Translation.Y,10);
        var recovered=world.Inverse().Apply(world.Apply(new(3,5,7)));
        Assert.Equal(3,recovered.X,9);Assert.Equal(5,recovered.Y,9);Assert.Equal(7,recovered.Z,9);
    }
    [Fact] public void DocumentRejectsCyclesEvenInUnusedDefinitions()
    {
        var doc=DocumentSnapshot.Create("Cycles");var id=DefinitionId.New();
        doc=doc with {Definitions=doc.Definitions.Add(id,new AssemblyDefinition(id,"Loop",[new(ComponentSlotId.New(),id,"Self",RigidTransform3d.Identity)]))};
        Assert.Throws<CadValidationException>(doc.Validate);
    }
    [Fact] public void ReusedSubassemblyExpandsDifferentPaths()
    {
        var doc=DocumentSnapshot.Create("Assembly");var part=DefinitionId.New();var sub=DefinitionId.New();var slot=ComponentSlotId.New();
        doc=doc with {Definitions=doc.Definitions.Add(part,new PartDefinition(part,"Part",[],[]))
            .Add(sub,new AssemblyDefinition(sub,"Sub",[new(slot,part,"Part",RigidTransform3d.Translate(1,0,0))]))
            .SetItem(doc.RootAssemblyId,new AssemblyDefinition(doc.RootAssemblyId,"Root",[
                new(ComponentSlotId.New(),sub,"A",RigidTransform3d.Identity),new(ComponentSlotId.New(),sub,"B",RigidTransform3d.Translate(100,0,0))]))};
        doc.Validate();var parts=doc.EnumerateOccurrences().Where(x=>x.DefinitionId==part).ToArray();
        Assert.Equal(2,parts.Length);Assert.NotEqual(parts[0].Path,parts[1].Path);Assert.Equal(101,parts[1].WorldTransform.Translation.X);
    }
    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(0)][InlineData(-1)]
    public void DimensionsRejectInvalidValues(double x)=>Assert.Throws<CadValidationException>(()=>new BoxRecipe(x,1,1,RigidTransform3d.Identity).Validate());
    [Fact] public void BowTieSketchIsRejected()=>Assert.Throws<CadValidationException>(()=>new SketchProfile([new(0,0),new(1,1),new(1,0),new(0,1)]).Validate());
    [Fact] public void DefaultQuaternionIsNotAnIdentity()=>Assert.Throws<CadValidationException>(()=>default(RigidTransform3d).Validate());
    [Fact] public void AssetsAreCopiedAndReleasedOnlyAfterLastLease()
    {
        var store=new MemoryAssetStore();byte[] bytes=[1,2,3];
        using(var a=store.Stage(bytes))
        {
            bytes[0]=8;
            using var b=store.Acquire(a.Id);a.Dispose();Assert.Equal(1,b.Content.Span[0]);Assert.Equal(1,store.Count);
        }
        Assert.Equal(0,store.Count);
    }
    [Fact] public async Task LateCommandCannotReplaceNewerState()
    {
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Before"),assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late=session.ExecuteAsync(new DelayedCommand(started,release));
        await started.Task;
        await session.ExecuteAsync(new EditDocumentCommand("new",d=>d with {Name="New"}));
        release.SetResult();await Assert.ThrowsAsync<StaleDocumentException>(()=>late);
        Assert.Equal("New",session.Snapshot.Name);
    }
    [Fact] public async Task ClosingDuringPreparationCancelsPublication()
    {
        var assets=new MemoryAssetStore();var session=new CadDocumentSession(DocumentSnapshot.Create("Before"),assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late=session.ExecuteAsync(new DelayedCommand(started,release));await started.Task;
        var closing=session.DisposeAsync().AsTask();release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>late);await closing;
        Assert.Equal("Before",session.Snapshot.Name);Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ObserverExceptionDoesNotRollBackCommit()
    {
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Old"),new MemoryAssetStore(),new OcctGeometryKernel(),new InlineSessionDispatcher());
        session.Changed+=(_,_)=>throw new InvalidOperationException("observer");
        await session.ExecuteAsync(new EditDocumentCommand("rename",d=>d with{Name="New"}));
        Assert.Equal("New",session.Snapshot.Name);Assert.True(session.CanUndo);
    }
    private sealed class DelayedCommand(TaskCompletionSource started,TaskCompletionSource release):ICadDocumentCommand
    {
        public string Name=>"Late";
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {started.SetResult();await release.Task;return new((context.Snapshot with{Name="Late"}).WithNewState());}
    }
}
