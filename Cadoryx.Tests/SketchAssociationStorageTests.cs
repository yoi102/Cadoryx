using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchAssociationStorageTests
{
    [Theory]
    [InlineData("sketch")][InlineData("revision")][InlineData("target-kind")][InlineData("cache")]
    [InlineData("sketch-revision")][InlineData("legacy-features")][InlineData("legacy-sketches")]
    public async Task CorruptAssociationsCannotLoadOrLeakAssets(string mutation)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var s=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(s,new ManagedSketchConstraintSolver()));s=session.Snapshot.Sketches[s.Id];
        await session.ExecuteAsync(SketchAssociationTests.Extrude(s,10));string path=files.PathFor("bad.cadoryx");await storage.SaveAsync(session.Snapshot,assets,path);int count=assets.Count;
        if(mutation is "legacy-features" or "legacy-sketches")
        {
            string kind=mutation=="legacy-features"?"features":"sketches";
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(e=>e.Kind==kind?e with{SchemaVersion=kind=="features"?3:1}:e).ToImmutableArray()});
        }
        else if(mutation=="sketch-revision")FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackSketches>(bytes);return MessagePackSerializer.Serialize(data with{Sketches=data.Sketches.Select(x=>x with{Revision=Guid.Empty}).ToArray()});
        });
        else FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);var f=data.Features.Single();var source=f.SketchSource!;
            var changed=mutation switch
            {
                "sketch"=>f with{SketchSource=source with{Sketch=Guid.NewGuid()}},
                "revision"=>f with{SketchSource=source with{Revision=Guid.NewGuid()}},
                "target-kind"=>f with{SketchSource=source with{Lines=[s.Points[0].Id.Value,..source.Lines.Skip(1)]}},
                _=>f with{Recipe=f.Recipe with{Profile=f.Recipe.Profile.Select((p,i)=>i==0?new[]{p[0]-1,p[1]}:p).ToArray()}}
            };
            return MessagePackSerializer.Serialize(data with{Features=[changed]});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        Assert.Equal(session.Snapshot.Settings,await storage.ReadSettingsAsync(path));
    }
}
