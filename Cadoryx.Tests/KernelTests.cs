using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
namespace Cadoryx.Tests;
public sealed class KernelTests
{
    [Fact] public async Task RecomputeRestoresTerminalBodyAfterEmptyCut()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Recompute"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(5,5,5,RigidTransform3d.Identity),"A"));
            var featureA=session.Snapshot.Features.Values.Single().Id;
            var bodyA=session.Snapshot.Bodies.Keys.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"B"));
            var bodyB=session.Snapshot.Bodies.Keys.Single(id=>id!=bodyA);
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[bodyA,bodyB]));
            var boolean=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);Assert.Empty(session.Snapshot.Bodies);
            await session.ExecuteAsync(new RecomputeCommand(featureA,new BoxRecipe(20,5,5,RigidTransform3d.Identity)));
            var result=Assert.Single(session.Snapshot.Bodies.Values);Assert.Equal(boolean.OutputBodyId,result.Id);Assert.Equal(250,result.Geometry.VolumeMm3,5);
            await session.ExecuteAsync(DocumentEdits.RenameBody(result.Id,"Retained name"));
            await session.ExecuteAsync(DocumentEdits.SetAppearance(result.Id,new(0xFFAABBCC)));
            result=session.Snapshot.Bodies[result.Id];
            await session.ExecuteAsync(new RecomputeCommand(featureA,new BoxRecipe(5,5,5,RigidTransform3d.Identity)));Assert.Empty(session.Snapshot.Bodies);
            using var files=new TestFiles();var storage=new Cadoryx.IO.CadDocumentStorage();
            await session.SaveAsync(storage,files.PathFor("empty.cadoryx"));
            using(var loaded=await storage.LoadAsync(files.PathFor("empty.cadoryx"),assets))
            await using(var restored=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
            {
                await restored.ExecuteAsync(new RecomputeCommand(featureA,new BoxRecipe(20,5,5,RigidTransform3d.Identity)));
                var body=Assert.Single(restored.Snapshot.Bodies.Values);Assert.Equal(result.Name,body.Name);Assert.Equal(result.Appearance,body.Appearance);Assert.Equal(result.Id,body.Id);
            }
            await session.UndoAsync();Assert.Equal(result,Assert.Single(session.Snapshot.Bodies.Values));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task BoxAndBooleanProduceMeasuredNativeGeometry()
    {
        var store=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var box=await kernel.EvaluateAsync(new BoxRecipe(10,10,10,RigidTransform3d.Identity),store))
        using(var tool=await kernel.EvaluateAsync(new BoxRecipe(5,10,10,RigidTransform3d.Identity),store))
        using(var cut=await kernel.EvaluateAsync(new BooleanRecipe(BooleanOperation.Cut,[box.Geometry,tool.Geometry]),store))
        {
            Assert.Equal(1000,box.Geometry.VolumeMm3,6);Assert.Equal(500,cut.Geometry.VolumeMm3,6);
            Assert.Equal(BodyKind.Solid,cut.Geometry.Kind);
        }
        Assert.Equal(0,store.Count);
    }
    [Fact] public async Task NativeExtrudeAndRevolveUseValidProfiles()
    {
        var store=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var extrude=await kernel.EvaluateAsync(new ExtrudeRecipe(new([new(0,0),new(10,0),new(10,10),new(0,10)]),5,RigidTransform3d.Identity),store))
            Assert.Equal(500,extrude.Geometry.VolumeMm3,6);
        using(var revolve=await kernel.EvaluateAsync(new RevolveRecipe(new([new(1,0),new(2,0),new(2,5),new(1,5)]),2*Math.PI,RigidTransform3d.Identity),store))
            Assert.Equal(15*Math.PI,revolve.Geometry.VolumeMm3,5);
        Assert.Equal(0,store.Count);
    }
    [Fact] public async Task SessionUndoRedoRetainsExactAssets()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Test"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
            var state=session.Snapshot;long generation=session.Generation;
            await session.UndoAsync();Assert.Empty(session.Snapshot.Bodies);Assert.True(assets.Count>0);
            await session.RedoAsync();Assert.Same(state,session.Snapshot);Assert.Equal(generation+2,session.Generation);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task EmptyCutIsAValidFeatureWithNoVisibleBody()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Cut"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(5,5,5,RigidTransform3d.Identity),"A"));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"B"));
            var bodies=session.Snapshot.Bodies.Values.OrderBy(x=>x.Geometry.VolumeMm3).Select(x=>x.Id).ToArray();
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,bodies));
            Assert.Empty(session.Snapshot.Bodies);Assert.Equal(3,session.Snapshot.Features.Count);
            session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
