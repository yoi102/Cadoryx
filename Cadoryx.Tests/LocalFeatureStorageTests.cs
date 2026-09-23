using System.Collections.Immutable;
using System.Security.Cryptography;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class LocalFeatureStorageTests
{
    [Fact] public async Task V7LocalRecipeMigratesWithZeroExtraEdgesAndNoEndRadius()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("V7 local"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,1));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var path=files.PathFor("v7-local.cadoryx");await session.SaveAsync(storage,path);
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
                return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(f=>f.Recipe.Kind=="local-box-edge"
                    ?f with{Recipe=f.Recipe with{Numbers=f.Recipe.Numbers.Take(7).ToArray()}}:f).ToArray()});
            });
            FormatEvolutionTests.RewriteManifest(path,m=>m with{ApplicationVersion="0.4.13",
                Sections=[..m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=7}:s)],
                RequiredCapabilities=[..m.RequiredCapabilities.Where(c=>c is not ("cadoryx.local-multi-edge.1" or "cadoryx.local-variable-radius.1"))]});
            using var loaded=await storage.LoadAsync(path,assets);
            Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
            var recipe=Assert.IsType<LocalFeatureRecipe>(loaded.Snapshot.Features[local.Id].Recipe);
            Assert.Equal(0,recipe.AdditionalEdges);Assert.Null(recipe.EndRadius);
            Assert.Equal(local.Result,loaded.Snapshot.Features[local.Id].Result);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(4096,0)][InlineData(-1,0)][InlineData(1.5,0)][InlineData(1,0)]
    [InlineData(0,-1)][InlineData(0,double.NaN)]
    public async Task CorruptMultiEdgeOrRadiusCannotLoad(double mask,double endRadius)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Corrupt multi"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,1));
            var path=files.PathFor("corrupt-multi.cadoryx");await session.SaveAsync(storage,path);var count=assets.Count;
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
                return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(f=>
                {
                    if(f.Recipe.Kind!="local-box-edge")return f;
                    var numbers=f.Recipe.Numbers.ToArray();numbers[7]=mask;numbers[8]=endRadius;
                    return f with{Recipe=f.Recipe with{Numbers=numbers}};
                }).ToArray()});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task TwoDistanceRoundtripAndSixNumberLegacyMigrationKeepTheRecipe()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Chamfer storage"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Chamfer,2,3));
            var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var current=files.PathFor("two-distances.cadoryx");await session.SaveAsync(storage,current);
            var manifest=FormatEvolutionTests.Manifest(current);
            Assert.Equal(8,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);
            Assert.Contains("cadoryx.local-chamfer-two-distances.1",manifest.RequiredCapabilities);
            using(var loaded=await storage.LoadAsync(current,assets))
            {
                Assert.Equal(local.Recipe,loaded.Snapshot.Features[local.Id].Recipe);
                Assert.Equal(5970,loaded.Snapshot.Features[local.Id].Result.VolumeMm3,4);
            }
            await session.ExecuteAsync(new RecomputeCommand(local.Id,((LocalFeatureRecipe)local.Recipe) with{SecondDistance=null}));
            var symmetric=session.Snapshot.Features[local.Id];
            var legacy=files.PathFor("six-number.cadoryx");await session.SaveAsync(storage,legacy);
            FormatEvolutionTests.RewriteSection(legacy,"features",bytes=>
            {
                var features=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
                return MessagePackSerializer.Serialize(features with{Features=features.Features.Select(f=>
                    f.Recipe.Kind=="local-box-edge"?f with{Recipe=f.Recipe with{Numbers=f.Recipe.Numbers.Take(6).ToArray()}}:f).ToArray()});
            });
            FormatEvolutionTests.RewriteManifest(legacy,m=>m with
            {
                ApplicationVersion="0.4.12",
                Sections=[..m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=6}:s)],
                RequiredCapabilities=[..m.RequiredCapabilities.Where(c=>c!="cadoryx.local-chamfer-two-distances.1")]
            });
            using(var loaded=await storage.LoadAsync(legacy,assets))
            {
                Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
                var old=Assert.IsType<LocalFeatureRecipe>(loaded.Snapshot.Features[local.Id].Recipe);
                Assert.Null(old.SecondDistance);Assert.Equal(2,old.Size);
                Assert.Equal(symmetric.Result,loaded.Snapshot.Features[local.Id].Result);
            }
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(-1,false)][InlineData(double.NaN,false)][InlineData(3,true)]
    public async Task CorruptSecondDistanceOrFilletModeCannotLoad(double distance,bool fillet)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Corrupt chamfer"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Chamfer,2,3));
            var path=files.PathFor("bad-chamfer.cadoryx");await session.SaveAsync(storage,path);int count=assets.Count;
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var features=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
                return MessagePackSerializer.Serialize(features with{Features=features.Features.Select(f=>
                {
                    if(f.Recipe.Kind!="local-box-edge")return f;
                    var numbers=f.Recipe.Numbers.ToArray();numbers[6]=distance;
                    return f with{Recipe=f.Recipe with{Numbers=numbers,Operation=fillet?(int)LocalFeatureOperation.Fillet:f.Recipe.Operation}};
                }).ToArray()});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task CurrentFeatureSchemaRejectsLegacyLengthWithoutMigration()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Malformed current chamfer"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,box.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Chamfer,2,3));
            var path=files.PathFor("short-current.cadoryx");await session.SaveAsync(storage,path);int count=assets.Count;
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var features=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
                return MessagePackSerializer.Serialize(features with{Features=features.Features.Select(f=>
                    f.Recipe.Kind=="local-box-edge"?f with{Recipe=f.Recipe with{Numbers=f.Recipe.Numbers.Take(6).ToArray()}}:f).ToArray()});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("fraction")][InlineData("same-axis")][InlineData("operation")][InlineData("box-cache")][InlineData("dependency")][InlineData("old-schema")]
    public async Task CorruptLocalRecipeCannotLoadOrLeakAssets(string mutation)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Local"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var f=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(session.Snapshot,f.Id,BoxBoundary.YMin,BoxBoundary.ZMax),LocalFeatureOperation.Fillet,2));
        string path=files.PathFor("bad.cadoryx");await storage.SaveAsync(session.Snapshot,assets,path);int count=assets.Count;
        if(mutation=="old-schema")FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=4}:s).ToImmutableArray()});
        else FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);var local=data.Features.Single(f=>f.Recipe.Kind=="local-box-edge");var r=local.Recipe;var n=r.Numbers.ToArray();
            if(mutation=="fraction")n[4]=1.5;if(mutation=="same-axis")n[5]=3;if(mutation=="box-cache")n[0]+=1;
            var changed=local with{Recipe=r with{Numbers=n,Operation=mutation=="operation"?99:r.Operation},Inputs=mutation=="dependency"?[]:local.Inputs};
            return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(f=>f.Id==local.Id?changed:f).ToArray()});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);Assert.Equal(session.Snapshot.Settings,await storage.ReadSettingsAsync(path));
    }
    [Fact] public async Task FrozenT1MigratesWithoutLosingReferencesOrChangingAssets()
    {
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m4t1-topology.cadoryx");
        Assert.Equal("738eba3cbcb0ced469f1b472b34c24f00bd2b72a7824017569427bc4e642debf",Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        using(var loaded=await storage.LoadAsync(path,assets))
        {Assert.Equal(2,loaded.Snapshot.TopologyReferences.Count);Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");loaded.Snapshot.Validate();}
        Assert.Equal(0,assets.Count);
    }
}
