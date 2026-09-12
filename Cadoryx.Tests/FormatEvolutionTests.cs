using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Commands;
using Cadoryx.Editor;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class FormatEvolutionTests
{
    private static string Fixture(string name)=>Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage",name);
    [Theory][InlineData("v1-box.cadoryx",24000)][InlineData("v2-box.cadoryx",24000)][InlineData("v2-colored-assembly.cadoryx",6000)]
    public async Task FrozenLegacyFilesMigrateWithoutChangingIdentityOrAssetBytes(string name,double volume)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        string path=Fixture(name);string hash=Hash(File.ReadAllBytes(path));
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.LEGACY_ASSET_FORMAT");
            var body=Assert.Single(loaded.Snapshot.Bodies.Values);
            Assert.Equal(volume,body.Geometry.VolumeMm3,5);Assert.NotNull(body.Geometry.Format);Assert.Null(body.Geometry.Format.KernelVersion);
            using var native=OcctGeometryBridge.ReadShape(body.Geometry,assets);Assert.True(native.IsValid);
            if(body.Geometry.Source is {} source){Assert.Null(source.Format!.KernelVersion);using var context=OcctGeometryBridge.ReadContext(source.ContextAssetId,assets);Assert.NotEmpty(context.GetFreeShapes());}
            string current=files.PathFor("migrated.cadoryx");
            await using(var session=new CadDocumentSession(loaded.Snapshot,assets,new OcctGeometryKernel(),new InlineSessionDispatcher(),path))
            {
                Assert.False(session.IsDirty);await session.SaveAsync(storage,current);Assert.False(session.IsDirty);
            }
            using var roundtrip=await storage.LoadAsync(current,assets);
            Assert.Equal(loaded.Snapshot.Id,roundtrip.Snapshot.Id);Assert.Equal(loaded.Snapshot.StateId,roundtrip.Snapshot.StateId);
            Assert.Equal(body,roundtrip.Snapshot.Bodies[body.Id]);Assert.Empty(roundtrip.Diagnostics);
            Assert.Equal(loaded.Snapshot.ReferencedAssets().OrderBy(x=>x.Sha256),roundtrip.Snapshot.ReferencedAssets().OrderBy(x=>x.Sha256));
            var manifest=Manifest(current);Assert.Equal(1,manifest.AssetCatalogVersion);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats.Count,manifest.Sections.Length);
            Assert.All(manifest.Assets,a=>Assert.NotNull(a.Format));
        }
        Assert.Equal(hash,Hash(File.ReadAllBytes(path)));Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task CurrentWriterCatalogAndGeometryTableAreCompleteAndShared()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        using(var source=await kernel.ImportAsync(ExchangeFixtureTests.Fixture("rotated-colors.step"),assets))
        {
            var path=files.PathFor("current.cadoryx");await storage.SaveAsync(source.Snapshot,assets,path);
            var manifest=Manifest(path);Assert.Contains("cadoryx.asset-catalog.1",manifest.RequiredCapabilities);Assert.Contains("cadoryx.geometry-table.1",manifest.RequiredCapabilities);
            Assert.Equal(AssetFormatPolicy.CurrentBRep,manifest.Assets.Single(a=>a.Format!.MediaType==AssetFormat.BRepMediaType).Format);
            Assert.Equal(AssetFormatPolicy.CurrentXde,manifest.Assets.Single(a=>a.Format!.MediaType==AssetFormat.XdeMediaType).Format);
            using var zip=ZipFile.OpenRead(path);using var stream=zip.GetEntry("sections/geometry.msgpack")!.Open();
            var table=MessagePackSerializer.Deserialize<PackGeometryTable>(stream);Assert.Single(table.Items);
            using var restored=await storage.LoadAsync(path,assets);var body=restored.Snapshot.Bodies.Values.Single();
            Assert.Same(body.Geometry,restored.Snapshot.Features.Values.Single().Result);
            Assert.Same(body.Geometry,Assert.IsType<ImportedRecipe>(restored.Snapshot.Features.Values.Single().Recipe).Source);
            Assert.Equal(source.Snapshot.Bodies.Values.Single(),body);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("missing")][InlineData("media")][InlineData("encoding")][InlineData("version")][InlineData("kernel")][InlineData("header")]
    public async Task InvalidOrUnsupportedAssetFormatFailsBeforeNativeDecode(string mutation)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();string path=files.PathFor("bad.cadoryx");
        using(var old=await storage.LoadAsync(Fixture("v2-box.cadoryx"),assets))await storage.SaveAsync(old.Snapshot,assets,path);
        RewriteManifest(path,m=>m with{Assets=m.Assets.Select(a=>a with{Format=mutation switch
        {
            "missing"=>null,"media"=>a.Format! with{MediaType="application/vendor.unknown"},"encoding"=>a.Format! with{Encoding="future"},
            "version"=>a.Format! with{FormatVersion=99},"kernel"=>a.Format! with{KernelVersion="9.0.0"},_=>a.Format! with{FormatVersion=2}
        }}).ToImmutableArray()});
        if(mutation is "missing" or "header")await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        else await Assert.ThrowsAsync<NotSupportedException>(()=>storage.LoadAsync(path,assets));
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("missing")][InlineData("duplicate")][InlineData("future")][InlineData("conflicting-legacy")]
    public async Task InvalidCrossSectionGeometryFailsWithoutLeases(string mutation)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();string path=files.PathFor("bad.cadoryx");
        if(mutation=="conflicting-legacy")
        {
            File.Copy(Fixture("v2-box.cadoryx"),path);
            RewriteSection(path,"structure",bytes=>
            {
                var s=MessagePackSerializer.Deserialize<PackStructure>(bytes);var b=s.Bodies[0];
                s.Bodies[0]=b with{Geometry=b.Geometry with{Volume=b.Geometry.Volume+1}};return MessagePackSerializer.Serialize(s);
            });
        }
        else
        {
            using(var old=await storage.LoadAsync(Fixture("v2-box.cadoryx"),assets))await storage.SaveAsync(old.Snapshot,assets,path);
            if(mutation=="future")RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="geometry"?s with{SchemaVersion=99}:s).ToImmutableArray()});
            else RewriteSection(path,"geometry",bytes=>
            {
                var g=MessagePackSerializer.Deserialize<PackGeometryTable>(bytes);return MessagePackSerializer.Serialize(g with{Items=mutation=="missing"?[]:[..g.Items,g.Items[0]]});
            });
        }
        if(mutation=="future")await Assert.ThrowsAsync<NotSupportedException>(()=>storage.LoadAsync(path,assets));
        else await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task SettingsReadDoesNotReadGeometryOrRequireAMigrationForIt()
    {
        using var files=new TestFiles();string path=files.PathFor("metadata.cadoryx");File.Copy(Fixture("v1-box.cadoryx"),path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var m=ReadManifest(zip);var s=m.Sections.Single(s=>s.Kind=="features");
            zip.GetEntry(s.Path)!.Delete();using var stream=zip.CreateEntry(s.Path).Open();stream.Write(new byte[s.Length]);
        }
        var storage=new CadDocumentStorage();Assert.Equal(LengthUnit.Millimeter,(await storage.ReadSettingsAsync(path)).DisplayUnit);
        var assets=new MemoryAssetStore();await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
        Assert.DoesNotContain(typeof(CadDocumentStorage).Assembly.GetReferencedAssemblies(),a=>a.Name=="OcctSharp");
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task UnknownOptionalAssetFormatsArePreservedWithoutRelabeling(bool futureOcct)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        using(var extension=assets.Stage("extension"u8))using(var payload=assets.Stage("future payload"u8))
        {
            var format=new AssetFormat("application/vnd.example.mesh","vendor-binary",42,"OtherKernel","10","VendorCAD","2");
            if(futureOcct)format=new(AssetFormat.BRepMediaType,"brep-ascii",99,"OCCT","9.0.0");
            var snapshot=DocumentSnapshot.Create("Preserve") with{Extensions=[new("vendor.data",4,extension.Id,"vendor")],RetainedAssets=[payload.Id],RetainedAssetFormats=ImmutableDictionary<AssetId,AssetFormat>.Empty.Add(payload.Id,format)};
            string path=files.PathFor("optional.cadoryx");await storage.SaveAsync(snapshot,assets,path);
            using var loaded=await storage.LoadAsync(path,assets);Assert.Equal(format,loaded.Snapshot.RetainedAssetFormats![payload.Id]);
            await storage.SaveAsync(loaded.Snapshot,assets,path);Assert.Equal(format,Manifest(path).Assets.Single().Format);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task RetainedAssetWithoutAnExtensionCannotBeSilentlyDropped()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        using(var payload=assets.Stage("orphan"u8))
        {
            var snapshot=DocumentSnapshot.Create("Orphan") with{RetainedAssets=[payload.Id]};
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.SaveAsync(snapshot,assets,files.PathFor("orphan.cadoryx")));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(files.PathFor("orphan.cadoryx"))!));
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task PreCanceledSavePreservesTargetWithoutAcquiringAssets()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var path=files.PathFor("original.cadoryx");File.WriteAllText(path,"original");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new CadDocumentStorage().SaveAsync(DocumentSnapshot.Create("Canceled"),assets,path,cancel.Token));
        Assert.Equal("original",File.ReadAllText(path));Assert.Equal(0,assets.Count);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact] public async Task CatalogAndSectionLimitFailurePreservesExistingFile()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();string path=files.PathFor("target.cadoryx");File.WriteAllText(path,"original");
        using(var loaded=await new CadDocumentStorage().LoadAsync(Fixture("v2-box.cadoryx"),assets))
        {
            await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage(limits:new(MaxManifestBytes:64)).SaveAsync(loaded.Snapshot,assets,path));
            await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage(limits:new(MaxJsonBytes:64)).SaveAsync(loaded.Snapshot,assets,path));
            Assert.Equal("original",File.ReadAllText(path));Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        Assert.Equal(0,assets.Count);
    }
    internal static CadManifest Manifest(string path){using var zip=ZipFile.OpenRead(path);return ReadManifest(zip);}
    private static CadManifest ReadManifest(ZipArchive zip){using var s=zip.GetEntry("manifest.json")!.Open();return JsonSerializer.Deserialize<CadManifest>(s,CadJson.Options)!;}
    internal static void RewriteManifest(string path,Func<CadManifest,CadManifest> edit)
    {using var zip=ZipFile.Open(path,ZipArchiveMode.Update);var m=edit(ReadManifest(zip));zip.GetEntry("manifest.json")!.Delete();using var stream=zip.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(stream,m,CadJson.Options);}
    internal static void RewriteSection(string path,string kind,Func<byte[],byte[]> edit)
    {
        using var zip=ZipFile.Open(path,ZipArchiveMode.Update);var m=ReadManifest(zip);var s=m.Sections.Single(s=>s.Kind==kind);byte[] bytes;
        using(var input=zip.GetEntry(s.Path)!.Open()){using var buffer=new MemoryStream();input.CopyTo(buffer);bytes=edit(buffer.ToArray());}
        zip.GetEntry(s.Path)!.Delete();using(var output=zip.CreateEntry(s.Path).Open())output.Write(bytes);
        m=m with{Sections=m.Sections.Replace(s,s with{Length=bytes.Length,Sha256=Hash(bytes)})};
        zip.GetEntry("manifest.json")!.Delete();using var stream=zip.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(stream,m,CadJson.Options);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
