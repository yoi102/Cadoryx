using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class FeatureSuppressionTests
{
    [Fact]
    public async Task SuppressionRestoresInputsAndRoundTripsWithUndoAndRedo()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();
        var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Suppression"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,14,RigidTransform3d.Identity),"Source"));
            var source=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,5,6,RigidTransform3d.Translate(2,2,2)),"Tool",source.PartId));
            var tool=session.Snapshot.Features.Values.Single(f=>f.Name=="Tool");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[source.OutputBodyId,tool.OutputBodyId]));
            var cut=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            Assert.Single(session.Snapshot.Bodies);

            await session.ExecuteAsync(new SetFeatureSuppressionCommand(cut.Id,true));
            Assert.True(session.Snapshot.Features[cut.Id].IsSuppressed);
            Assert.True(session.Snapshot.Features[cut.Id].IsStale);
            Assert.Equal(2,session.Snapshot.Bodies.Count);
            Assert.Contains(source.OutputBodyId,session.Snapshot.Bodies.Keys);
            Assert.Contains(tool.OutputBodyId,session.Snapshot.Bodies.Keys);
            session.Snapshot.Validate();
            await kernel.ExportAsync(session.Snapshot,assets,files.PathFor("suppressed.step"));
            Assert.True(new FileInfo(files.PathFor("suppressed.step")).Length>0);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
                new RecomputeCommand(cut.Id,cut.Recipe)));

            var path=files.PathFor("suppressed.cadoryx");
            await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                Assert.True(loaded.Snapshot.Features[cut.Id].IsSuppressed);
                Assert.Equal(2,loaded.Snapshot.Bodies.Count);
                loaded.Snapshot.Validate();
            }
            await session.UndoAsync();
            Assert.False(session.Snapshot.Features[cut.Id].IsSuppressed);
            Assert.Single(session.Snapshot.Bodies);
            await session.RedoAsync();
            Assert.True(session.Snapshot.Features[cut.Id].IsSuppressed);
            await session.ExecuteAsync(new SetFeatureSuppressionCommand(cut.Id,false));
            Assert.False(session.Snapshot.Features[cut.Id].IsStale);
            Assert.False(session.Snapshot.Features[cut.Id].IsSuppressed);
            Assert.Single(session.Snapshot.Bodies);
            Assert.Contains(cut.OutputBodyId,session.Snapshot.Bodies.Keys);
            session.Snapshot.Validate();

            await session.SaveAsync(storage,path);
            FormatEvolutionTests.RewriteManifest(path,m=>m with
            {
                Sections=[..m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=20}:s)]
            });
            using var previous=await storage.LoadAsync(path,assets);
            Assert.False(previous.Snapshot.Features[cut.Id].IsSuppressed);
            Assert.Contains(previous.Diagnostics,d=>d.Code=="IO.MIGRATED");
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task SuppressingAnIntermediateFeatureBlocksItsDescendantsAndRestoresIndependentInputs()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Dependency closure"),assets,
            kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"A"));
            var a=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,4,4,RigidTransform3d.Translate(1,1,1)),"B",a.PartId));
            var b=session.Snapshot.Features.Values.Single(f=>f.Name=="B");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[a.OutputBodyId,b.OutputBodyId]));
            var cut=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Translate(15,0,0)),"C",a.PartId));
            var c=session.Snapshot.Features.Values.Single(f=>f.Name=="C");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,[cut.OutputBodyId,c.OutputBodyId]));
            var fuse=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe r&&r.Operation==BooleanOperation.Fuse);
            Assert.Single(session.Snapshot.Bodies);

            await session.ExecuteAsync(new SetFeatureSuppressionCommand(cut.Id,true));
            Assert.True(session.Snapshot.Features[fuse.Id].IsStale);
            Assert.False(session.Snapshot.Features[fuse.Id].IsSuppressed);
            Assert.Equal(3,session.Snapshot.Bodies.Count);
            Assert.All(new[]{a,b,c},f=>Assert.Contains(f.OutputBodyId,session.Snapshot.Bodies.Keys));
            Assert.DoesNotContain(cut.OutputBodyId,session.Snapshot.Bodies.Keys);
            Assert.DoesNotContain(fuse.OutputBodyId,session.Snapshot.Bodies.Keys);
            session.Snapshot.Validate();

            await session.ExecuteAsync(new SetFeatureSuppressionCommand(cut.Id,false));
            Assert.False(session.Snapshot.Features[fuse.Id].IsStale);
            Assert.Single(session.Snapshot.Bodies);
            Assert.Contains(fuse.OutputBodyId,session.Snapshot.Bodies.Keys);
            session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task SuppressedStateCannotMasqueradeAsPreviousFeatureFormat()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Suppressed schema"),assets,
            new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(3,4,5,RigidTransform3d.Identity),"Box"));
            var feature=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,true));
            var path=files.PathFor("wrong-version.cadoryx");await session.SaveAsync(storage,path);
            FormatEvolutionTests.RewriteManifest(path,m=>m with
            {
                Sections=[..m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=20}:s)]
            });
            await Assert.ThrowsAnyAsync<Exception>(()=>storage.LoadAsync(path,assets));
        }
        Assert.Equal(0,assets.Count);
    }
}
