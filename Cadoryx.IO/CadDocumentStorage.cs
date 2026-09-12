using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

/// <summary>Versioned ZIP container. Metadata reads never initialize OCCT.</summary>
public sealed class CadDocumentStorage(CadSectionMigrationRegistry? migrations=null,StorageLimits? limits=null) : IDocumentStorage
{
    private readonly CadSectionMigrationRegistry registry=migrations??new();
    private readonly StorageLimits limits=limits??new();
    private static readonly HashSet<string> KnownSections=["document","structure","features","presentation"];
    private static readonly string[] Capabilities=["cadoryx.core.1","occt.brep.1"];
    public async Task SaveAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default)
    {
        snapshot.Validate();using var hold=new DocumentAssetLease(snapshot,assets);
        var full=Path.GetFullPath(path);var directory=Path.GetDirectoryName(full)!;Directory.CreateDirectory(directory);
        var temp=Path.Combine(directory,"."+Path.GetFileName(full)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await WriteAsync(snapshot,assets,temp,cancellationToken).ConfigureAwait(false);
            // Verify every recorded entry before replacing the old document.
            using(var stream=File.OpenRead(temp))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
            {
                var entries=ValidateEntries(zip);
                var manifest=await ManifestAsync(entries,cancellationToken).ConfigureAwait(false);
                foreach(var section in manifest.Sections)await ReadVerified(entries,section.Path,section.Length,section.Sha256,limits.MaxJsonBytes,cancellationToken).ConfigureAwait(false);
                foreach(var asset in manifest.Assets)await ReadVerified(entries,asset.Path,asset.Length,asset.Sha256,limits.MaxAssetBytes,cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if(File.Exists(full))File.Replace(temp,full,null);else File.Move(temp,full);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private async Task WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token)
    {
        var sections=ImmutableArray.CreateBuilder<SectionEntry>();var catalog=ImmutableArray.CreateBuilder<AssetEntry>();
        await using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
        {
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
            {
                async Task Section(string kind,object value)
                {
                    var bytes=MessagePackSections.Encode(kind,value);
                    string entry="sections/"+kind+".msgpack";await WriteEntry(zip,entry,bytes,token);
                    sections.Add(new(kind,entry,2,true,bytes.Length,Hash(bytes),"messagepack"));
                }
                await Section("document",new DocumentSection(snapshot.Id,snapshot.StateId,snapshot.Name,snapshot.RootAssemblyId,snapshot.Settings));
                await Section("structure",new StructureSection(snapshot.Definitions.Values.ToImmutableArray(),snapshot.Bodies.Values.ToImmutableArray()));
                await Section("features",new FeaturesSection(snapshot.Features.Values.ToImmutableArray()));
                await Section("presentation",new PresentationSection(snapshot.Layers.Values.ToImmutableArray(),snapshot.Materials.Values.ToImmutableArray()));
                if(!snapshot.Extensions.IsDefault)
                    foreach(var extension in snapshot.Extensions)
                    {
                        if(KnownSections.Contains(extension.Kind))throw new InvalidDataException("Extension collides with a core section.");
                        using var lease=assets.Acquire(extension.PayloadAssetId);var bytes=lease.Content.ToArray();
                        string entry="extensions/"+sections.Count+".bin";await WriteEntry(zip,entry,bytes,token);
                        sections.Add(new(extension.Kind,entry,extension.SchemaVersion,false,bytes.Length,Hash(bytes),extension.Encoding));
                    }
                var extensionAssets=snapshot.Extensions.IsDefault?[]:snapshot.Extensions.Select(x=>x.PayloadAssetId).ToHashSet();
                foreach(var id in snapshot.ReferencedAssets().Where(id=>!extensionAssets.Contains(id)))
                {
                    using var lease=assets.Acquire(id);var bytes=lease.Content.ToArray();string entry="assets/"+id.Sha256+".bin";
                    await WriteEntry(zip,entry,bytes,token);catalog.Add(new(id,entry,bytes.Length,id.Sha256));
                }
                var manifest=new CadManifest("Cadoryx",1,snapshot.Id,snapshot.StateId,"0.1.0",sections.ToImmutable(),catalog.ToImmutable(),[..Capabilities]);
                await WriteEntry(zip,"manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest,CadJson.Options),token);
            }
            await stream.FlushAsync(token);stream.Flush(true);
        }
    }
    public async Task<LoadedDocument> LoadAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        using var stream=File.OpenRead(path);using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
        var entries=ValidateEntries(zip);var manifest=await ManifestAsync(entries,cancellationToken).ConfigureAwait(false);
        var sectionValues=new Dictionary<string,(SectionEntry Entry,byte[] Bytes)>(StringComparer.Ordinal);
        foreach(var section in manifest.Sections)
        {
            if(!KnownSections.Contains(section.Kind)&&section.Required)throw new NotSupportedException($"Required section {section.Kind} is unsupported.");
            sectionValues.Add(section.Kind,(section,await ReadVerified(entries,section.Path,section.Length,section.Sha256,limits.MaxJsonBytes,cancellationToken).ConfigureAwait(false)));
        }
        T Read<T>(string kind)
        {
            if(!sectionValues.TryGetValue(kind,out var value))throw new InvalidDataException($"Missing section {kind}.");
            return Decode<T>(value.Entry,value.Bytes);
        }
        var document=Read<DocumentSection>("document");var structure=Read<StructureSection>("structure");
        var features=Read<FeaturesSection>("features");var presentation=Read<PresentationSection>("presentation");
        if(document.Id!=manifest.DocumentId||document.StateId!=manifest.StateId)throw new InvalidDataException("Manifest/document identity mismatch.");
        var leases=new List<IAssetLease>();
        try
        {
            foreach(var asset in manifest.Assets)
            {
                var bytes=await ReadVerified(entries,asset.Path,asset.Length,asset.Sha256,limits.MaxAssetBytes,cancellationToken).ConfigureAwait(false);
                var lease=assets.Stage(bytes);leases.Add(lease);
                if(lease.Id!=asset.Id)throw new InvalidDataException("Asset identity mismatch.");
            }
            var extensions=ImmutableArray.CreateBuilder<PreservedSection>();
            foreach(var (kind,value) in sectionValues.Where(x=>!KnownSections.Contains(x.Key)))
            {
                var lease=assets.Stage(value.Bytes);leases.Add(lease);extensions.Add(new(kind,value.Entry.SchemaVersion,lease.Id,value.Entry.Encoding));
            }
            var snapshot=new DocumentSnapshot(document.Id,document.StateId,document.Name,document.RootAssemblyId,document.Settings,
                structure.Definitions.ToImmutableDictionary(x=>x.Id),structure.Bodies.ToImmutableDictionary(x=>x.Id),
                features.Features.ToImmutableDictionary(x=>x.Id),presentation.Layers.ToImmutableDictionary(x=>x.Id),
                presentation.Materials.ToImmutableDictionary(x=>x.Id),extensions.ToImmutable(),extensions.Count>0?manifest.Assets.Select(x=>x.Id).ToImmutableArray():[]);
            snapshot.Validate();using var verify=new DocumentAssetLease(snapshot,assets);
            var loaded=new LoadedDocument(snapshot,leases,extensions.Count>0?[new("IO.READ_ONLY","Unknown optional sections are preserved; editing is disabled.")]:[]);
            leases.Clear();return loaded;
        }
        finally{foreach(var lease in leases)lease.Dispose();}
    }
    public async Task<DocumentSettings> ReadSettingsAsync(string path,CancellationToken cancellationToken=default)
    {
        using var stream=File.OpenRead(path);using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
        var entries=ValidateEntries(zip);var manifest=await ManifestAsync(entries,cancellationToken).ConfigureAwait(false);
        var section=manifest.Sections.SingleOrDefault(x=>x.Kind=="document")??throw new InvalidDataException("Missing document section.");
        var bytes=await ReadVerified(entries,section.Path,section.Length,section.Sha256,limits.MaxJsonBytes,cancellationToken).ConfigureAwait(false);
        var dto=Decode<DocumentSection>(section,bytes);dto.Settings.Validate();return dto.Settings;
    }
    private T Decode<T>(SectionEntry section,byte[] bytes)
    {
        try{return (section.SchemaVersion,section.Encoding) switch
        {
            (1,"json")=>registry.Read<T>(section.Kind,1,1,bytes),
            (2,"messagepack")=>MessagePackSections.Decode<T>(section.Kind,bytes),
            _=>throw new NotSupportedException($"Unsupported {section.Kind} schema/encoding {section.SchemaVersion}/{section.Encoding}.")
        };}
        catch(Exception ex) when(ex is MessagePack.MessagePackSerializationException or NullReferenceException or ArgumentException or JsonException)
        {throw new InvalidDataException($"Malformed {section.Kind} section.",ex);}
    }
    private Dictionary<string,ZipArchiveEntry> ValidateEntries(ZipArchive zip)
    {
        if(zip.Entries.Count>limits.MaxEntries)throw new InvalidDataException("Too many ZIP entries.");
        var result=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var entry in zip.Entries)
        {
            var name=entry.FullName;
            if(string.IsNullOrWhiteSpace(name)||name.StartsWith('/')||name.Contains('\\')||name.Contains(':')||
                name.Split('/').Any(x=>x is "" or "." or "..")||!result.TryAdd(name,entry))
                throw new InvalidDataException("Unsafe or duplicate archive path.");
            // Unix symlink file mode.
            if(((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("Symbolic links are not supported.");
            if(entry.Length<0||entry.Length>limits.MaxAssetBytes)throw new InvalidDataException("Entry exceeds limit.");
            total=checked(total+entry.Length);if(total>limits.MaxTotalBytes)throw new InvalidDataException("Archive exceeds size limit.");
        }
        return result;
    }
    private async Task<CadManifest> ManifestAsync(Dictionary<string,ZipArchiveEntry> entries,CancellationToken token)
    {
        if(!entries.TryGetValue("manifest.json",out var entry))throw new InvalidDataException("Missing manifest.");
        var bytes=await ReadBounded(entry,1024*1024,token).ConfigureAwait(false);
        var manifest=JsonSerializer.Deserialize<CadManifest>(bytes,CadJson.Options)??throw new InvalidDataException("Null manifest.");
        if(manifest.Format!="Cadoryx"||manifest.ContainerVersion!=1)throw new NotSupportedException("Unsupported document container.");
        CadGuard.Id(manifest.DocumentId);CadGuard.Id(manifest.StateId);
        if(manifest.RequiredCapabilities.IsDefault||manifest.RequiredCapabilities.Except(Capabilities).Any())throw new NotSupportedException("Required capabilities are unsupported.");
        if(manifest.Sections.IsDefault||manifest.Assets.IsDefault)throw new InvalidDataException("Missing manifest tables.");
        if(manifest.Sections.Select(x=>x.Kind).Distinct().Count()!=manifest.Sections.Length||
            manifest.Assets.Select(x=>x.Id).Distinct().Count()!=manifest.Assets.Length)throw new InvalidDataException("Duplicate manifest identity.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"manifest.json"};
        foreach(var section in manifest.Sections)
        {
            if(section.SchemaVersion<1||string.IsNullOrWhiteSpace(section.Kind)||!paths.Add(section.Path)||!entries.ContainsKey(section.Path))throw new InvalidDataException("Invalid section descriptor.");
        }
        foreach(var asset in manifest.Assets)
        {
            asset.Id.Validate();if(asset.Id.Sha256!=asset.Sha256||!paths.Add(asset.Path)||!entries.ContainsKey(asset.Path))throw new InvalidDataException("Invalid asset descriptor.");
        }
        if(paths.Count!=entries.Count)throw new InvalidDataException("Archive contains unlisted payloads.");
        return manifest;
    }
    private static async Task<byte[]> ReadVerified(Dictionary<string,ZipArchiveEntry> entries,string path,long length,string hash,long limit,CancellationToken token)
    {
        if(!entries.TryGetValue(path,out var entry)||entry.Length!=length)throw new InvalidDataException("Entry length mismatch.");
        var bytes=await ReadBounded(entry,limit,token).ConfigureAwait(false);
        if(Hash(bytes)!=hash)throw new InvalidDataException("Entry SHA-256 mismatch: "+path);
        return bytes;
    }
    private static async Task<byte[]> ReadBounded(ZipArchiveEntry entry,long limit,CancellationToken token)
    {
        if(entry.Length>limit||entry.Length>int.MaxValue)throw new InvalidDataException("Entry exceeds read limit.");
        using var source=entry.Open();using var target=new MemoryStream();var buffer=new byte[65536];long total=0;
        int count;
        while((count=await source.ReadAsync(buffer,token).ConfigureAwait(false))>0)
        {
            total+=count;if(total>limit||total>entry.Length)throw new InvalidDataException("Expanded entry exceeds declared length.");
            await target.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);
        }
        if(total!=entry.Length)throw new InvalidDataException("Truncated entry.");
        return target.ToArray();
    }
    private static async Task WriteEntry(ZipArchive zip,string name,byte[] bytes,CancellationToken token)
    {
        var entry=zip.CreateEntry(name,CompressionLevel.Fastest);await using var output=entry.Open();await output.WriteAsync(bytes,token).ConfigureAwait(false);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
