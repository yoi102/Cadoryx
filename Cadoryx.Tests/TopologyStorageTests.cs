using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class TopologyStorageTests
{
    [Theory]
    [InlineData("version")][InlineData("kind")][InlineData("policy")][InlineData("boundary")][InlineData("edge")]
    [InlineData("document")][InlineData("body")][InlineData("revision")][InlineData("duplicate")][InlineData("tail")]
    public async Task CorruptTopologyCannotLoadOrLeakAssetsAndSettingsStayIndependent(string mutation)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Topology"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        var r=TopologyReference.Box(session.Snapshot,session.Snapshot.Features.Keys.Single(),BoxBoundary.XMax);
        await session.ExecuteAsync(new UpsertTopologyReferenceCommand(r));string path=files.PathFor("bad.cadoryx");await storage.SaveAsync(session.Snapshot,assets,path);int count=assets.Count;
        FormatEvolutionTests.RewriteSection(path,"topology",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackTopologyReferences>(bytes);var item=data.References.Single();
            item=mutation switch
            {
                "version"=>item with{Version=2},"kind"=>item with{Kind=42},"policy"=>item with{Policy=42},
                "boundary"=>item with{Boundary=42},"edge"=>item with{Kind=1,SecondBoundary=0},
                "document"=>item with{Document=Guid.NewGuid()},"body"=>item with{Body=Guid.NewGuid()},
                "revision"=>item with{OriginRevision=Guid.Empty},_=>item
            };
            byte[] result=MessagePackSerializer.Serialize(data with{References=mutation=="duplicate"?[item,item]:[item]});
            return mutation=="tail"?[..result,0]:result;
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        Assert.Equal(session.Snapshot.Settings,await storage.ReadSettingsAsync(path));
    }

    [Theory][InlineData("missing")][InlineData("future")][InlineData("legacy-collision")]
    public async Task TopologySectionContractCannotBeOmittedDowngradedOrSilentlySkipped(string mutation)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var snapshot=DocumentSnapshot.Create("Topology");
        string path=files.PathFor("bad.cadoryx");await storage.SaveAsync(snapshot,assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=mutation=="missing"?m.Sections.Where(s=>s.Kind!="topology").ToImmutableArray():
            m.Sections.Select(s=>s.Kind==(mutation=="future"?"topology":"document")?s with{SchemaVersion=mutation=="future"?99:3}:s).ToImmutableArray()});
        if(mutation=="missing")using(var zip=System.IO.Compression.ZipFile.Open(path,System.IO.Compression.ZipArchiveMode.Update))zip.GetEntry("sections/topology.msgpack")!.Delete();
        if(mutation=="legacy-collision")await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        else await Assert.ThrowsAsync<NotSupportedException>(()=>storage.LoadAsync(path,assets));
        Assert.Equal(snapshot.Settings,await storage.ReadSettingsAsync(path));Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task DeletedProducerReferenceRemainsPersistedAndDiagnosable()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var snapshot=DocumentSnapshot.Create("Topology");
        var reference=new TopologyReference(TopologyReferenceId.New(),snapshot.Id,FeatureId.New(),BodyId.New(),GeometryRevisionId.New(),TopologyKind.Face,BoxBoundary.ZMax);
        snapshot=snapshot with{TopologyReferences=snapshot.TopologyReferences.Add(reference.Id,reference)};snapshot.Validate();
        string path=files.PathFor("missing.cadoryx");await storage.SaveAsync(snapshot,assets,path);
        using var loaded=await storage.LoadAsync(path,assets);Assert.Equal(reference,loaded.Snapshot.TopologyReferences[reference.Id]);
        var result=await new OcctGeometryKernel().ResolveAsync(loaded.Snapshot,reference,assets);
        Assert.Equal(TopologyResolutionStatus.Missing,result.Status);Assert.Null(result.Target);Assert.Equal(0,assets.Count);
    }
}
