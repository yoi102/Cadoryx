using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record LegacyPackSketchRegionSection([property:Key(0),JsonRequired]int Version,
    [property:Key(1),JsonRequired]LegacyPackSketchRegionTable[] Sketches);
[MessagePackObject] public sealed record LegacyPackSketchRegionTable([property:Key(0),JsonRequired]Guid Sketch,
    [property:Key(1),JsonRequired]LegacyPackSketchRegionAuthor[] Groups);
[MessagePackObject] public sealed record LegacyPackSketchRegionAuthor([property:Key(0),JsonRequired]Guid Id,
    [property:Key(1),JsonRequired]PackSketchRegionSource[][] Sources,[property:Key(2),JsonRequired]double Distance,
    [property:Key(3),JsonRequired]int Join,[property:Key(4),JsonRequired]LegacyPackSketchRegionLoop[] Loops,
    [property:Key(5),JsonRequired]bool Frozen,[property:Key(6),JsonRequired]string? Reason,[property:Key(7),JsonRequired]string Algorithm);
[MessagePackObject] public sealed record LegacyPackSketchRegionLoop([property:Key(0),JsonRequired]int Depth,
    [property:Key(1),JsonRequired]int? Parent,[property:Key(2),JsonRequired]bool Reversed,[property:Key(3),JsonRequired]PackSketchRegionOutput[] Outputs);

public static partial class SketchRegionSectionStorage
{
    internal static byte[] EncodeLegacyEmptyMessagePack()=>MessagePackSerializer.Serialize(new LegacyPackSketchRegionSection(2,[]),Options);
    internal static byte[] UpgradeMessagePack(ReadOnlyMemory<byte> bytes)=>Decode(()=>
    {
        CheckShape(Check(bytes),2);
        return UpgradeLegacy(MessagePackSerializer.Deserialize<LegacyPackSketchRegionSection>(bytes,Options),2);
    });
    private static byte[] UpgradeLegacyJson(ReadOnlyMemory<byte> bytes)
        =>UpgradeLegacy(JsonSerializer.Deserialize<LegacyPackSketchRegionSection>(Check(bytes).Span,CadJson.Options)??
            throw new InvalidDataException("Null legacy region section."),1);

    private static byte[] UpgradeLegacy(LegacyPackSketchRegionSection value,int version)
    {
        if(value.Version!=version||value.Sketches is null||value.Sketches.Length>10000)
            throw new InvalidDataException("Unsupported legacy region section.");
        var owners=new HashSet<Guid>();
        foreach(var table in value.Sketches)
        {
            if(table is null||table.Sketch==Guid.Empty||!owners.Add(table.Sketch)||table.Groups is not {Length:>0 and <=10000})
                throw new InvalidDataException("Invalid legacy region owner table.");
            foreach(var group in table.Groups)
                if(group is null||group.Algorithm!=SketchRegionJointAuthors.LegacyAlgorithm||group.Sources is not {Length:>0 and <=64}||
                    group.Sources.Any(l=>l is not {Length:>0 and <=256})||group.Sources.Sum(l=>l.Length)>4096||
                    group.Loops is null||group.Loops.Length!=group.Sources.Length||group.Loops.Any(l=>l is null||l.Outputs is not {Length:>0 and <=256})||
                    group.Loops.Sum(l=>l.Outputs.Length)>4096)
                    throw new InvalidDataException("Legacy region table cannot carry new branch semantics or exceed existing source/output budgets.");
        }
        // The old creating operation recorded no connection branch. Keep its
        // algorithm and all author values; an empty table means unrecorded,
        // never a branch inferred from coordinates or equal parameters.
        var upgraded=new PackSketchRegionSection(SchemaVersion,value.Sketches.Select(t=>new PackSketchRegionTable(t.Sketch,t.Groups.Select(g=>
            new PackSketchRegionAuthor(g.Id,g.Sources,g.Distance,g.Join,g.Loops.Select(l=>new PackSketchRegionLoop(l.Depth,l.Parent,l.Reversed,l.Outputs,[])).ToArray(),
                g.Frozen,g.Reason,g.Algorithm)).ToArray())).ToArray());
        ValidateTables(upgraded);return Check(MessagePackSerializer.Serialize(upgraded,Options));
    }

    internal static bool HasRecordedConnections(ReadOnlyMemory<byte> bytes,string encoding,int version)=>Decode(()=>
    {
        if(encoding=="messagepack"&&version==2){CheckShape(Check(bytes),2);return false;}
        if(encoding=="json"&&version==1){_=UpgradeLegacyJson(bytes);return false;}
        PackSketchRegionSection section;
        if(encoding=="messagepack"&&version==SchemaVersion)
        {CheckShape(Check(bytes));section=MessagePackSerializer.Deserialize<PackSketchRegionSection>(bytes,Options);ValidateTables(section);}
        else if(encoding=="json"&&version==JsonSchemaVersion)
        {section=JsonSerializer.Deserialize<PackSketchRegionSection>(Check(bytes).Span,CadJson.Options)??throw new InvalidDataException("Null region section.");ValidateTables(section,JsonSchemaVersion);}
        else throw new InvalidDataException("Unknown region connection section encoding or version.");
        return section.Sketches.Any(t=>t.Groups.Any(g=>g.Algorithm!=SketchRegionJointAuthors.LegacyAlgorithm||g.Loops.Any(l=>l.Joints.Length!=0)));
    });
}
