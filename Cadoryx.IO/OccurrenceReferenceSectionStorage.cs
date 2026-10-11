using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackOccurrenceReferences([property:Key(0)] PackOccurrenceReference[] Entries);
[MessagePackObject]
public sealed record PackOccurrenceReference([property:Key(0)] Guid Revision,[property:Key(1)] string BoundaryHash,
    [property:Key(2)] string TableHash,[property:Key(3)] int SchemaVersion);

/// <summary>Independent required partition. Old geometry/inline DTO schemas carry no new proof fields.</summary>
public static class OccurrenceReferenceSectionStorage
{
    public const string Kind="occurrence-references",Capability="cadoryx.native-occurrence-tables.1";
    public static SectionFormat Format{get;}=new(Kind,1,"messagepack");
    private static readonly MessagePackSerializerOptions Options=MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(128));
    public static byte[] Encode(DocumentSnapshot snapshot)
    {
        var geometries=snapshot.ReferencedGeometry().GroupBy(g=>g.Revision).Select(group=>
        {
            var first=group.First();if(group.Any(g=>g!=first))throw new InvalidDataException("Conflicting geometry occurrence revision.");return first;
        });
        var entries=geometries.Where(g=>g.SerializedOccurrences is not null).OrderBy(g=>g.Revision.Value)
            .Select(g=>new PackOccurrenceReference(g.Revision.Value,g.AssetId.Sha256,g.SerializedOccurrences!.TableAssetId.Sha256,g.SerializedOccurrences.SchemaVersion)).ToArray();
        if(entries.Length==0)throw new InvalidDataException("An occurrence partition must contain actual geometry declarations.");
        return MessagePackSerializer.Serialize(new PackOccurrenceReferences(entries),Options);
    }
    public static PackOccurrenceReference[] Read(ReadOnlyMemory<byte> bytes,string encoding,int version)
    {
        if(encoding!="messagepack"||version!=1)throw new NotSupportedException("Unsupported occurrence reference partition.");
        var reader=new MessagePackReader(bytes);var data=MessagePackSerializer.Deserialize<PackOccurrenceReferences>(ref reader,Options);
        if(!reader.End||data?.Entries is not {Length:>0}||data.Entries.Any(e=>e is null)||
            data.Entries.Select(e=>e.Revision).Distinct().Count()!=data.Entries.Length)
            throw new InvalidDataException("Empty, duplicate or malformed occurrence reference partition.");
        foreach(var entry in data.Entries)
        {
            if(entry.Revision==Guid.Empty||entry.SchemaVersion!=1)throw new InvalidDataException("Invalid occurrence geometry revision/schema.");
            new AssetId(entry.BoundaryHash).Validate();new AssetId(entry.TableHash).Validate();
        }
        return data.Entries;
    }
    public static void AddAssetRoles(PackOccurrenceReference[] entries,ReadOnlyMemory<byte> geometryBytes,Dictionary<AssetId,string> roles)
    {
        var table=MessagePackSerializer.Deserialize<PackGeometryTable>(geometryBytes,Options);
        foreach(var entry in entries)
        {
            var geometry=table.Items.SingleOrDefault(g=>g.Revision==entry.Revision);
            if(geometry is null||geometry.Hash!=entry.BoundaryHash||geometry.SerializedReferences is not null)
                throw new InvalidDataException("Occurrence declaration is missing, foreign or mixed with an independent legacy capture.");
            var id=new AssetId(entry.TableHash);
            if(roles.TryGetValue(id,out var role)&&role!=AssetFormat.OccurrenceTableMediaType)throw new InvalidDataException("Conflicting native occurrence asset role.");
            roles[id]=AssetFormat.OccurrenceTableMediaType;
        }
    }
    public static DocumentSnapshot Attach(DocumentSnapshot snapshot,PackOccurrenceReference[] entries,IReadOnlyDictionary<AssetId,AssetFormat> formats)
    {
        var declarations=entries.ToDictionary(e=>new GeometryRevisionId(e.Revision));
        var cache=new Dictionary<GeometryRevisionId,(GeometryAssetRef Original,GeometryAssetRef Attached)>();
        var used=new HashSet<GeometryRevisionId>();
        GeometryAssetRef Attach(GeometryAssetRef original)
        {
            if(cache.TryGetValue(original.Revision,out var saved))
            {
                if(saved.Original!=original)throw new InvalidDataException("Conflicting metadata for one occurrence geometry revision.");return saved.Attached;
            }
            var attached=original;
            if(declarations.TryGetValue(original.Revision,out var entry))
            {
                if(original.AssetId.Sha256!=entry.BoundaryHash||original.SerializedReferences is not null||original.SerializedOccurrences is not null)
                    throw new InvalidDataException("Occurrence partition does not belong to this primary geometry.");
                var id=new AssetId(entry.TableHash);
                if(!formats.TryGetValue(id,out var format))throw new InvalidDataException("Occurrence table missing from asset catalog.");
                AssetFormatPolicy.RequireOccurrenceTable(format);
                attached=original with{SerializedOccurrences=new(id,entry.SchemaVersion,format)};attached.Validate();used.Add(original.Revision);
            }
            cache.Add(original.Revision,(original,attached));return attached;
        }
        var result=GeometryReferenceAttachment.Map(snapshot,Attach);
        if(!used.SetEquals(declarations.Keys))throw new InvalidDataException("Unreferenced occurrence declarations.");return result;
    }
}
