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
