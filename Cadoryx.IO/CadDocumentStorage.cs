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
    private static readonly HashSet<string> KnownSections=CadSectionMigrationRegistry.CurrentFormats.Keys.ToHashSet();
    private static readonly string[] Capabilities=["cadoryx.core.1","occt.brep.1","cadoryx.geometry-table.1","cadoryx.asset-catalog.1","cadoryx.sketches.1","cadoryx.sketch-association.1","cadoryx.topology-references.1","cadoryx.local-box-edge.1"];
    public async Task SaveAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                foreach(var asset in manifest.Assets)await VerifyStream(entries,asset.Path,asset.Length,asset.Sha256,limits.MaxAssetBytes,cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if(File.Exists(full))File.Replace(temp,full,null);else File.Move(temp,full);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private async Task WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token)
    {
        var sections=ImmutableArray.CreateBuilder<SectionEntry>();var catalog=AssetCatalog.Build(snapshot,assets,token);
        await using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
        {
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
            {
                async Task Section(SectionPayload payload)
                {
                    var bytes=payload.Bytes;var kind=payload.Format.Kind;
                    string entry="sections/"+kind+".msgpack";await WriteEntry(zip,entry,bytes,token);
                    sections.Add(new(kind,entry,payload.Format.Version,true,bytes.Length,Hash(bytes.Span),payload.Format.Encoding));
                }
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["document"],MessagePackSections.Encode("document",new DocumentSection(snapshot.Id,snapshot.StateId,snapshot.Name,snapshot.RootAssemblyId,snapshot.Settings))));
                foreach(var payload in MessagePackSections.SplitGeometry(new(snapshot.Definitions.Values.ToImmutableArray(),snapshot.Bodies.Values.ToImmutableArray()),new(snapshot.Features.Values.ToImmutableArray())))await Section(payload);
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["presentation"],MessagePackSections.Encode("presentation",new PresentationSection(snapshot.Layers.Values.ToImmutableArray(),snapshot.Materials.Values.ToImmutableArray()))));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["sketches"],MessagePackSections.EncodeSketches(snapshot.Sketches.Values)));
                await Section(new(CadSectionMigrationRegistry.CurrentFormats["topology"],MessagePackSections.EncodeTopology(snapshot.TopologyReferences.Values)));
                if(!snapshot.Extensions.IsDefault)
                    foreach(var extension in snapshot.Extensions)
                    {
                        if(KnownSections.Contains(extension.Kind))throw new InvalidDataException("Extension collides with a core section.");
                        using var lease=assets.Acquire(extension.PayloadAssetId);var bytes=lease.Content;
                        string entry="extensions/"+sections.Count+".bin";await WriteEntry(zip,entry,bytes,token);
                        sections.Add(new(extension.Kind,entry,extension.SchemaVersion,false,bytes.Length,Hash(bytes.Span),extension.Encoding));
                    }
                foreach(var asset in catalog)
                {
                    using var lease=assets.Acquire(asset.Id);await WriteEntry(zip,asset.Path,lease.Content,token);
                }
                var manifest=new CadManifest("Cadoryx",1,snapshot.Id,snapshot.StateId,"0.4.2",sections.ToImmutable(),catalog.ToImmutableArray(),[..Capabilities],1);
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
        var core=Migrate(sectionValues.Values.Where(v=>KnownSections.Contains(v.Entry.Kind)).Select(v=>Payload(v.Entry,v.Bytes)),CadSectionMigrationRegistry.CurrentFormats,cancellationToken);
        var document=Decode<DocumentSection>(core["document"]);var presentation=Decode<PresentationSection>(core["presentation"]);
        if(document.Id!=manifest.DocumentId||document.StateId!=manifest.StateId)throw new InvalidDataException("Manifest/document identity mismatch.");
        var roles=Malformed(()=>MessagePackSections.RequiredAssetRoles(core["geometry"]));
        bool hasExtensions=sectionValues.Keys.Any(k=>!KnownSections.Contains(k));
        if(!hasExtensions&&manifest.Assets.Any(a=>!roles.ContainsKey(a.Id)))throw new InvalidDataException("Unreferenced asset without an extension section.");
        var leases=new List<IAssetLease>();
        var formats=new Dictionary<AssetId,AssetFormat>();
        try
        {
            foreach(var asset in manifest.Assets)
            {
                var bytes=await ReadVerified(entries,asset.Path,asset.Length,asset.Sha256,limits.MaxAssetBytes,cancellationToken).ConfigureAwait(false);
                bool required=roles.TryGetValue(asset.Id,out var role);
                var format=asset.Format??(required?AssetFormatPolicy.Inspect(bytes):AssetFormat.Opaque);
                if(required)
                {
                    if(role==AssetFormat.BRepMediaType)AssetFormatPolicy.RequireBRep(format);else AssetFormatPolicy.RequireXde(format);
                    AssetFormatPolicy.VerifyPayload(format,bytes);
                }
                formats.Add(asset.Id,format);
                var lease=assets.Stage(bytes);leases.Add(lease);
                if(lease.Id!=asset.Id)throw new InvalidDataException("Asset identity mismatch.");
            }
            var (structure,features)=Join(core,formats);
            var extensions=ImmutableArray.CreateBuilder<PreservedSection>();
            foreach(var (kind,value) in sectionValues.Where(x=>!KnownSections.Contains(x.Key)))
            {
                var lease=assets.Stage(value.Bytes);leases.Add(lease);extensions.Add(new(kind,value.Entry.SchemaVersion,lease.Id,value.Entry.Encoding));
            }
            var snapshot=new DocumentSnapshot(document.Id,document.StateId,document.Name,document.RootAssemblyId,document.Settings,
                structure.Definitions.ToImmutableDictionary(x=>x.Id),structure.Bodies.ToImmutableDictionary(x=>x.Id),
                features.Features.ToImmutableDictionary(x=>x.Id),presentation.Layers.ToImmutableDictionary(x=>x.Id),
                presentation.Materials.ToImmutableDictionary(x=>x.Id),extensions.ToImmutable(),extensions.Count>0?manifest.Assets.Select(x=>x.Id).ToImmutableArray():[],extensions.Count>0?formats.ToImmutableDictionary():null)
                {Sketches=Malformed(()=>MessagePackSections.DecodeSketches(core["sketches"].Bytes)),
                 TopologyReferences=Malformed(()=>MessagePackSections.DecodeTopology(core["topology"].Bytes))};
            Malformed(()=>{snapshot.Validate();return true;});using var verify=new DocumentAssetLease(snapshot,assets);
            var diagnostics=ImmutableArray.CreateBuilder<CadDiagnostic>();
            if(extensions.Count>0)diagnostics.Add(new("IO.READ_ONLY","Unknown optional sections and their asset formats are preserved; editing is disabled."));
            if(manifest.AssetCatalogVersion==0)diagnostics.Add(new("IO.LEGACY_ASSET_FORMAT","Legacy asset formats were recognized from their headers. Original kernel and writer versions are unknown."));
            if(manifest.Sections.Any(s=>KnownSections.Contains(s.Kind)&&new SectionFormat(s.Kind,s.SchemaVersion,s.Encoding)!=CadSectionMigrationRegistry.CurrentFormats[s.Kind]))
                diagnostics.Add(new("IO.MIGRATED","Document sections were migrated in memory; IDs, state and geometry bytes are retained. Save writes the current format."));
            var loaded=new LoadedDocument(snapshot,leases,diagnostics.ToImmutable());
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
        var targets=new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}};
        var migrated=Migrate([Payload(section,bytes)],targets,cancellationToken);
        var dto=Decode<DocumentSection>(migrated["document"]);dto.Settings.Validate();return dto.Settings;
    }
    private static SectionPayload Payload(SectionEntry entry,byte[] bytes)=>new(new(entry.Kind,entry.SchemaVersion,entry.Encoding),bytes);
    private IReadOnlyDictionary<string,SectionPayload> Migrate(IEnumerable<SectionPayload> sections,IReadOnlyDictionary<string,SectionFormat> targets,CancellationToken token)
        =>Malformed(()=>registry.Migrate(sections,targets,limits.MaxJsonBytes,limits.MaxTotalBytes,token));
    private static T Decode<T>(SectionPayload payload)=>Malformed(()=>MessagePackSections.Decode<T>(payload.Format.Kind,payload.Bytes));
    private static (StructureSection Structure,FeaturesSection Features) Join(IReadOnlyDictionary<string,SectionPayload> sections,IReadOnlyDictionary<AssetId,AssetFormat> formats)
        =>Malformed(()=>MessagePackSections.JoinGeometry(sections,formats));
    private static T Malformed<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception ex) when(ex is MessagePack.MessagePackSerializationException or NullReferenceException or ArgumentException or JsonException)
        {throw new InvalidDataException("Malformed document sections.",ex);}
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
        var bytes=await ReadBounded(entry,limits.MaxManifestBytes,token).ConfigureAwait(false);
        var manifest=JsonSerializer.Deserialize<CadManifest>(bytes,CadJson.Options)??throw new InvalidDataException("Null manifest.");
        if(manifest.Format!="Cadoryx"||manifest.ContainerVersion!=1)throw new NotSupportedException("Unsupported document container.");
        CadGuard.Id(manifest.DocumentId);CadGuard.Id(manifest.StateId);
        if(manifest.AssetCatalogVersion is <0 or >1)throw new NotSupportedException("Unsupported asset catalog version.");
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
            if(manifest.AssetCatalogVersion==1&&asset.Format is null)throw new InvalidDataException("Missing asset format descriptor.");
            asset.Format?.Validate();
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
        using var source=entry.Open();var bytes=new byte[(int)entry.Length];
        try{await source.ReadExactlyAsync(bytes,token).ConfigureAwait(false);}
        catch(EndOfStreamException ex){throw new InvalidDataException("Truncated entry.",ex);}
        if(await source.ReadAsync(new byte[1],token).ConfigureAwait(false)!=0)throw new InvalidDataException("Expanded entry exceeds declared length.");
        return bytes;
    }
    private static async Task VerifyStream(Dictionary<string,ZipArchiveEntry> entries,string path,long length,string expected,long limit,CancellationToken token)
    {
        if(length<0||length>limit||!entries.TryGetValue(path,out var entry)||entry.Length!=length)throw new InvalidDataException("Invalid asset length.");
        using var source=entry.Open();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=System.Buffers.ArrayPool<byte>.Shared.Rent(65536);long total=0;
        try
        {
            int count;while((count=await source.ReadAsync(buffer,token).ConfigureAwait(false))!=0)
            {
                total+=count;if(total>length)throw new InvalidDataException("Expanded asset exceeds declared length.");hash.AppendData(buffer,0,count);
            }
            if(total!=length||Convert.ToHexStringLower(hash.GetHashAndReset())!=expected)throw new InvalidDataException("Asset integrity check failed: "+path);
        }
        finally{System.Buffers.ArrayPool<byte>.Shared.Return(buffer);}
    }
    private static async Task WriteEntry(ZipArchive zip,string name,ReadOnlyMemory<byte> bytes,CancellationToken token)
    {
        var entry=zip.CreateEntry(name,CompressionLevel.Fastest);await using var output=entry.Open();await output.WriteAsync(bytes,token).ConfigureAwait(false);
    }
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
