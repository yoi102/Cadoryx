using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;

namespace Cadoryx.IO;

public sealed record StorageLimits(long MaxAssetBytes=256L*1024*1024,long MaxTotalBytes=1024L*1024*1024,int MaxEntries=200000,int MaxJsonBytes=32*1024*1024,int MaxManifestBytes=8*1024*1024);
public sealed record SectionEntry(string Kind,string Path,int SchemaVersion,bool Required,long Length,string Sha256,string Encoding="json");
public sealed record AssetEntry(AssetId Id,string Path,long Length,string Sha256,AssetFormat? Format=null);
public sealed record CadManifest(string Format,int ContainerVersion,DocumentId DocumentId,DocumentStateId StateId,string ApplicationVersion,
    ImmutableArray<SectionEntry> Sections,ImmutableArray<AssetEntry> Assets,ImmutableArray<string> RequiredCapabilities,int AssetCatalogVersion=0);
internal sealed record DocumentSection(DocumentId Id,DocumentStateId StateId,string Name,DefinitionId RootAssemblyId,DocumentSettings Settings);
internal sealed record StructureSection(ImmutableArray<CadDefinition> Definitions,ImmutableArray<CadBody> Bodies);
internal sealed record FeaturesSection(ImmutableArray<FeatureDefinition> Features);
internal sealed record PresentationSection(ImmutableArray<CadLayer> Layers,ImmutableArray<CadMaterial> Materials);
public readonly record struct SectionFormat(string Kind,int Version,string Encoding);
public sealed record SectionPayload(SectionFormat Format,ReadOnlyMemory<byte> Bytes);
public static class CadJson
{
    public static JsonSerializerOptions Options { get; }=Create();
    private static JsonSerializerOptions Create()=>new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        WriteIndented=true,MaxDepth=128,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new JsonStringEnumConverter()}
    };
}
/// <summary>Explicit, ordered migrations; a missing step never silently treats old bytes as current.</summary>
public sealed partial class CadSectionMigrationRegistry
{
    private readonly Dictionary<(string,int),Func<JsonElement,JsonElement>> migrations=[];
    public void Register(string kind,int fromVersion,Func<JsonElement,JsonElement> migration)
    {
        if(fromVersion<1)throw new ArgumentOutOfRangeException(nameof(fromVersion));
        migrations.Add((kind,fromVersion),migration);
    }
    public T Read<T>(string kind,int version,int currentVersion,ReadOnlyMemory<byte> bytes)
    {
        if(version<1||version>currentVersion)throw new NotSupportedException($"Unsupported {kind} section version {version}.");
        using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=128});
        var value=json.RootElement.Clone();
        while(version<currentVersion)
        {
            if(!migrations.TryGetValue((kind,version),out var migrate))throw new NotSupportedException($"Missing {kind} migration from {version}.");
            value=migrate(value).Clone();version++;
        }
        return value.Deserialize<T>(CadJson.Options)??throw new InvalidDataException("Null section.");
    }
}
