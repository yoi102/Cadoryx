using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
namespace Cadoryx.Tests;
public sealed class MessagePackStorageTests
{
    [Fact] public async Task CurrentFormatWritesNumericKeyMessagePackAndRetainsEveryRecipe()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("All recipes 零件"),assets,kernel,new InlineSessionDispatcher()))
        {
            var identity=RigidTransform3d.Identity;
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,identity),"Box"));
            var source=session.Snapshot.Bodies.Values.Single().Geometry;
            GeometryRecipe[] recipes=[new CylinderRecipe(2,5,identity),new ImportedRecipe(source),new TransformRecipe(source,RigidTransform3d.Translate(50,0,0)),
                new BooleanRecipe(BooleanOperation.Fuse,[source,source]),
                new ExtrudeRecipe(new([new(0,0),new(2,0),new(2,2),new(0,2)]),3,identity),
                new RevolveRecipe(new([new(1,0),new(2,0),new(2,3),new(1,3)]),Math.PI,identity)];
            foreach(var recipe in recipes)await session.ExecuteAsync(new AddBodyCommand(recipe,recipe.GetType().Name));
            string path=files.PathFor("all.cadoryx");await session.SaveAsync(storage,path);
            using(var zip=ZipFile.OpenRead(path))
            {
                var manifest=ReadManifest(zip);Assert.All(manifest.Sections,s=>{Assert.Equal(CadSectionMigrationRegistry.CurrentFormats[s.Kind].Version,s.SchemaVersion);Assert.Equal("messagepack",s.Encoding);Assert.EndsWith(".msgpack",s.Path);});
                using var section=zip.GetEntry("sections/document.msgpack")!.Open();int header=section.ReadByte();Assert.InRange(header,0x90,0x9f);
            }
            using var loaded=await storage.LoadAsync(path,assets);Assert.Equal(7,loaded.Snapshot.Features.Count);
            foreach(var feature in session.Snapshot.Features.Values)
            {
                var restored=loaded.Snapshot.Features[feature.Id];Assert.Equal(feature.Result,restored.Result);
                Assert.Equal(JsonSerializer.Serialize(feature.Recipe,CadJson.Options),JsonSerializer.Serialize(restored.Recipe,CadJson.Options));
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LegacyJsonVersionOneLoadsThenSavesAsMessagePack()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var snapshot=DocumentSnapshot.Create("Legacy");
        string path=files.PathFor("legacy.cadoryx");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
        {
            var descriptors=ImmutableArray.CreateBuilder<SectionEntry>();
            void Write(string kind,object payload)
            {
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(payload,CadJson.Options);string entry="sections/"+kind+".json";
                WriteEntry(zip,entry,bytes);descriptors.Add(new(kind,entry,1,true,bytes.Length,Hash(bytes)));
            }
            Write("document",new{snapshot.Id,snapshot.StateId,snapshot.Name,snapshot.RootAssemblyId,snapshot.Settings});
            Write("structure",new{Definitions=snapshot.Definitions.Values.ToArray(),Bodies=snapshot.Bodies.Values.ToArray()});
            Write("features",new{Features=snapshot.Features.Values.ToArray()});
            Write("presentation",new{Layers=snapshot.Layers.Values.ToArray(),Materials=snapshot.Materials.Values.ToArray()});
            WriteEntry(zip,"manifest.json",JsonSerializer.SerializeToUtf8Bytes(new CadManifest("Cadoryx",1,snapshot.Id,snapshot.StateId,"0.0.1",descriptors.ToImmutable(),[],["cadoryx.core.1","occt.brep.1"]),CadJson.Options));
        }
        using var loaded=await storage.LoadAsync(path,assets);Assert.Equal(snapshot.Name,loaded.Snapshot.Name);
        await storage.SaveAsync(loaded.Snapshot,assets,path);
        using var saved=ZipFile.OpenRead(path);Assert.All(ReadManifest(saved).Sections,s=>Assert.Equal("messagepack",s.Encoding));
    }
    [Fact] public async Task UnknownOptionalBytesAreRetainedAndBlockEditing()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        using var payload=assets.Stage(new byte[]{0x91,0xA1,0x78});
        var snapshot=DocumentSnapshot.Create("Extension") with {Extensions=[new("vendor.future",7,payload.Id,"messagepack")]};
        string path=files.PathFor("optional.cadoryx");await storage.SaveAsync(snapshot,assets,path);
        using var loaded=await storage.LoadAsync(path,assets);
        await using var session=new CadDocumentSession(loaded.Snapshot,assets,new OcctGeometryKernel(),new InlineSessionDispatcher(),path);
        await Assert.ThrowsAsync<NotSupportedException>(()=>session.ExecuteAsync(new EditDocumentCommand("change",d=>d with{Name="Changed"})));
        await session.SaveAsync(storage,path);
        using var roundtrip=await storage.LoadAsync(path,assets);var extension=Assert.Single(roundtrip.Snapshot.Extensions);
        Assert.Equal("messagepack",extension.Encoding);Assert.Equal(7,extension.SchemaVersion);
        using var retained=assets.Acquire(extension.PayloadAssetId);Assert.Equal(payload.Content.ToArray(),retained.Content.ToArray());
    }
    [Theory][InlineData("nil")][InlineData("trailing")][InlineData("truncated")][InlineData("huge-array")]
    public async Task MalformedMessagePackRejectedEvenWithMatchingHash(string kind)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();string path=files.PathFor("bad.cadoryx");
        await storage.SaveAsync(DocumentSnapshot.Create("Input"),assets,path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var manifest=ReadManifest(zip);var descriptor=manifest.Sections.Single(s=>s.Kind=="document");
            byte[] data;using(var input=zip.GetEntry(descriptor.Path)!.Open()){using var memory=new MemoryStream();input.CopyTo(memory);data=memory.ToArray();}
            data=kind switch{"nil"=>[0xc0],"trailing"=>[..data,0xc0],"huge-array"=>[0xdd,0x7f,0xff,0xff,0xff],_=>data[..3]};
            zip.GetEntry(descriptor.Path)!.Delete();WriteEntry(zip,descriptor.Path,data);
            manifest=manifest with {Sections=manifest.Sections.Replace(descriptor,descriptor with{Length=data.Length,Sha256=Hash(data)})};
            zip.GetEntry("manifest.json")!.Delete();WriteEntry(zip,"manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest,CadJson.Options));
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task UnknownRequiredSectionOrFutureCoreVersionCannotLoadSilently()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        foreach(bool unknown in new[]{false,true})
        {
            string path=files.PathFor(unknown?"required.cadoryx":"version.cadoryx");await storage.SaveAsync(DocumentSnapshot.Create("Future"),assets,path);
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
            {
                var manifest=ReadManifest(zip);var section=manifest.Sections[0];
                manifest=manifest with{Sections=manifest.Sections.SetItem(0,unknown?section with{Kind="future.required"}:section with{SchemaVersion=99})};
                zip.GetEntry("manifest.json")!.Delete();WriteEntry(zip,"manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest,CadJson.Options));
            }
            await Assert.ThrowsAsync<NotSupportedException>(()=>storage.LoadAsync(path,assets));
        }
    }
    private static CadManifest ReadManifest(ZipArchive zip)
    {using var input=zip.GetEntry("manifest.json")!.Open();return JsonSerializer.Deserialize<CadManifest>(input,CadJson.Options)!;}
    private static void WriteEntry(ZipArchive zip,string name,byte[] data){using var output=zip.CreateEntry(name).Open();output.Write(data);}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
