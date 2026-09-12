using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchCommandTests
{
    [Fact] public async Task SolveEditUndoRedoRemoveAndSavepointUseExactCommittedState()
    {
        using var files=new TestFiles();var (initial,part)=Document();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var sketch=SketchTestData.Rectangle(part);DocumentChangeSet? change=null;session.Changed+=(_,c)=>change=c;
        await session.ExecuteAsync(new UpsertSketchCommand(sketch,solver));var committed=session.Snapshot;Assert.Contains(sketch.Id,change!.ChangedSketches);
        var storage=new CadDocumentStorage();await session.SaveAsync(storage,files.PathFor("saved.cadoryx"));Assert.False(session.IsDirty);
        var next=Resize(committed.Sketches[sketch.Id],50);await session.ExecuteAsync(new UpsertSketchCommand(next,solver));var edited=session.Snapshot;
        Assert.True(session.IsDirty);await session.UndoAsync();Assert.Same(committed,session.Snapshot);Assert.False(session.IsDirty);
        await session.RedoAsync();Assert.Same(edited,session.Snapshot);Assert.True(session.IsDirty);
        await session.ExecuteAsync(new RemoveSketchCommand(sketch.Id));Assert.Empty(session.Snapshot.Sketches);
        await session.UndoAsync();Assert.Same(edited,session.Snapshot);Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task FailedSolvePreservesStateHistoryAndSavepoint()
    {
        using var files=new TestFiles();var (initial,part)=Document();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());var solver=new ManagedSketchConstraintSolver();
        var sketch=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(sketch,solver));
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("saved.cadoryx"));var before=session.Snapshot;long generation=session.Generation;
        var conflict=before.Sketches[sketch.Id];conflict=conflict with{Constraints=conflict.Constraints.Add(new OffsetXConstraint(SketchConstraintId.New(),conflict.Points[0].Id,conflict.Points[1].Id,99))};
        var error=await Assert.ThrowsAsync<SketchSolveException>(()=>session.ExecuteAsync(new UpsertSketchCommand(conflict,solver)));
        Assert.Equal(SketchSolveStatus.Inconsistent,error.Report.Status);Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.False(session.IsDirty);
        await session.UndoAsync();Assert.Same(initial,session.Snapshot);Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LateOrCanceledSolveCannotPublish(bool cancel)
    {
        var (initial,part)=Document();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),new OcctGeometryKernel(),new InlineSessionDispatcher());
        var solver=new GateSolver();using var source=new CancellationTokenSource();
        var pending=session.ExecuteAsync(new UpsertSketchCommand(SketchTestData.Rectangle(part),solver),source.Token);await solver.Started.Task;
        if(cancel)source.Cancel();else await session.ExecuteAsync(new EditDocumentCommand("rename",d=>d with{Name="Newer"}));
        var before=session.Snapshot;solver.Release.SetResult();
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);else await Assert.ThrowsAsync<StaleDocumentException>(()=>pending);
        Assert.Same(before,session.Snapshot);Assert.Empty(session.Snapshot.Sketches);
    }
    [Fact] public async Task SolverCannotChangeIdentityAndSketchCannotMoveToAnotherPartImplicitly()
    {
        var (initial,part)=Document();var other=DefinitionId.New();initial=initial with{Definitions=initial.Definitions.Add(other,new PartDefinition(other,"Other",[],[]))};
        await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),new OcctGeometryKernel(),new InlineSessionDispatcher());var sketch=SketchTestData.Rectangle(part);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(sketch,new IdentityChangingSolver())));Assert.Same(initial,session.Snapshot);
        await session.ExecuteAsync(new UpsertSketchCommand(sketch,new ManagedSketchConstraintSolver()));var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(before.Sketches[sketch.Id] with{PartId=other},new ManagedSketchConstraintSolver())));
        Assert.Same(before,session.Snapshot);
    }
    [Fact] public async Task SolvedPolygonFeedsRealOcctInItsPartPlaneAsAnExplicitFrozenProfile()
    {
        var (initial,part)=Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var sketch=SketchTestData.Rectangle(part) with{Plane=new(new(5,6,7),Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2))};
        await session.ExecuteAsync(new UpsertSketchCommand(sketch,new ManagedSketchConstraintSolver()));var solved=session.Snapshot.Sketches[sketch.Id];
        var profile=SketchProfileBuilder.Polygon(solved,solved.Lines.Select(l=>l.Id));
        await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(profile,5,solved.Plane),"Sketch extrusion",part));var body=Assert.Single(session.Snapshot.Bodies.Values);
        Assert.Equal(6000,body.Geometry.VolumeMm3,4);Assert.InRange(body.Geometry.Bounds.Min.X,4.999,5.001);Assert.InRange(body.Geometry.Bounds.Max.X,9.999,10.001);
        await session.ExecuteAsync(new UpsertSketchCommand(Resize(solved,50),new ManagedSketchConstraintSolver()));
        // S1's bridge is a copied profile. S2 will add explicit live feature dependencies.
        Assert.Same(body.Geometry,session.Snapshot.Bodies[body.Id].Geometry);
    }
    internal static (DocumentSnapshot,DefinitionId) Document()
    {
        var d=DocumentSnapshot.Create("Sketch document");var part=DefinitionId.New();var root=(AssemblyDefinition)d.Definitions[d.RootAssemblyId];
        return(d with{Definitions=d.Definitions.Add(part,new PartDefinition(part,"Part",[],[])).SetItem(root.Id,root with{Children=[new(ComponentSlotId.New(),part,"Part",RigidTransform3d.Identity)]})},part);
    }
    internal static CadSketch Resize(CadSketch sketch,double width)
    {
        int index=sketch.Constraints.IndexOf(sketch.Constraints.OfType<OffsetXConstraint>().Single());
        return sketch with{Constraints=sketch.Constraints.SetItem(index,((OffsetXConstraint)sketch.Constraints[index]) with{Offset=width})};
    }
    private sealed class GateSolver : ISketchConstraintSolver
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>"test";
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {Started.SetResult();await Release.Task;return await new ManagedSketchConstraintSolver().SolveAsync(sketch,options);}
    }
    private sealed class IdentityChangingSolver : ISketchConstraintSolver
    {
        public string Version=>"test";
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {var result=await new ManagedSketchConstraintSolver().SolveAsync(sketch,options,cancellationToken);return result with{Solution=result.Solution! with{Id=SketchId.New()}};}
    }
}
