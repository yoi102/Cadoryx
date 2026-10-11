using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record PackSketchRegionSection([property:Key(0),JsonRequired]int Version,[property:Key(1),JsonRequired]PackSketchRegionTable[] Sketches);
[MessagePackObject] public sealed record PackSketchRegionTable([property:Key(0),JsonRequired]Guid Sketch,[property:Key(1),JsonRequired]PackSketchRegionAuthor[] Groups);
[MessagePackObject] public sealed record PackSketchRegionAuthor([property:Key(0),JsonRequired]Guid Id,[property:Key(1),JsonRequired]PackSketchRegionSource[][] Sources,
    [property:Key(2),JsonRequired]double Distance,[property:Key(3),JsonRequired]int Join,[property:Key(4),JsonRequired]PackSketchRegionLoop[] Loops,
    [property:Key(5),JsonRequired]bool Frozen,[property:Key(6),JsonRequired]string? Reason,[property:Key(7),JsonRequired]string Algorithm);
[MessagePackObject] public sealed record PackSketchRegionSource([property:Key(0),JsonRequired]Guid Entity,[property:Key(1),JsonRequired]int Kind,
    [property:Key(2),JsonRequired]PackSketchCurveExpression Geometry,[property:Key(3),JsonRequired]bool Reversed);
[MessagePackObject] public sealed record PackSketchRegionLoop([property:Key(0),JsonRequired]int Depth,[property:Key(1),JsonRequired]int? Parent,
    [property:Key(2),JsonRequired]bool Reversed,[property:Key(3),JsonRequired]PackSketchRegionOutput[] Outputs,
    [property:Key(4),JsonRequired]PackSketchRegionJoint[] Joints);
[MessagePackObject] public sealed record PackSketchRegionJoint([property:Key(0),JsonRequired]int PreviousSlot,[property:Key(1),JsonRequired]int NextSlot,
    [property:Key(2),JsonRequired]Guid PreviousSource,[property:Key(3),JsonRequired]Guid NextSource,[property:Key(4),JsonRequired]int Kind,
    [property:Key(5),JsonRequired]double PreviousParameter,[property:Key(6),JsonRequired]double NextParameter,
    [property:Key(7),JsonRequired]bool PreviousReversed,[property:Key(8),JsonRequired]bool NextReversed);
[MessagePackObject] public sealed record PackSketchRegionOutput([property:Key(0),JsonRequired]Guid Curve,[property:Key(1),JsonRequired]PackSketchExpressionEndpoints Endpoints,
    [property:Key(2),JsonRequired]int Role,[property:Key(3),JsonRequired]int[] Sources,[property:Key(4),JsonRequired]int BasisDepth,
    [property:Key(5),JsonRequired]bool Reversed,[property:Key(6),JsonRequired]PackSketchCurveExpression Geometry);

/// <summary>Independent owned group recipes. Never reconstructs intent from curve coordinates.</summary>
public static partial class SketchRegionSectionStorage
{
    public const int JsonSchemaVersion=2;
    public const int SchemaVersion=3;
    public const int MaximumBytes=32*1024*1024;
    private static readonly MessagePackSerializerOptions Options=MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
    public static byte[] EncodeMessagePack(IEnumerable<CadSketch> sketches)=>Check(MessagePackSerializer.Serialize(Pack(sketches),Options));
    public static byte[] EncodeJson(IEnumerable<CadSketch> sketches)=>Check(JsonSerializer.SerializeToUtf8Bytes(Pack(sketches) with{Version=JsonSchemaVersion},CadJson.Options));
    public static ImmutableDictionary<SketchId,CadSketch> AttachMessagePack(ReadOnlyMemory<byte> bytes,ImmutableDictionary<SketchId,CadSketch> sketches)=>Decode(()=>
    {CheckShape(Check(bytes));return Attach(MessagePackSerializer.Deserialize<PackSketchRegionSection>(bytes,Options),sketches);});
    public static ImmutableDictionary<SketchId,CadSketch> AttachJson(ReadOnlyMemory<byte> bytes,ImmutableDictionary<SketchId,CadSketch> sketches)=>Decode(()=>
        Attach(JsonSerializer.Deserialize<PackSketchRegionSection>(Check(bytes).Span,CadJson.Options)??throw new InvalidDataException("Null region author section."),sketches,JsonSchemaVersion));
    internal static byte[] JsonToMessagePack(ReadOnlyMemory<byte> bytes,int version=JsonSchemaVersion)=>Decode(()=>
    {
        if(version==1)return UpgradeLegacyJson(bytes);
        if(version!=JsonSchemaVersion)throw new InvalidDataException("Unknown region JSON schema.");
        var value=JsonSerializer.Deserialize<PackSketchRegionSection>(Check(bytes).Span,CadJson.Options)??throw new InvalidDataException("Null region author section.");
        ValidateTables(value,JsonSchemaVersion);return Check(MessagePackSerializer.Serialize(value with{Version=SchemaVersion},Options));
    });
    private static PackSketchRegionSection Pack(IEnumerable<CadSketch> sketches)
    {
        var all=sketches.ToArray();foreach(var s in all)s.Validate();
        if(all.Length>10000||all.Select(s=>s.Id).Distinct().Count()!=all.Length)throw new InvalidDataException("Invalid region sketch registry.");
        return new(SchemaVersion,all.Where(s=>!s.RegionOffsets.IsEmpty).OrderBy(s=>s.Id.Value).Select(s=>new PackSketchRegionTable(s.Id.Value,s.RegionOffsets.Select(g=>
            new PackSketchRegionAuthor(g.Id,g.Sources.Select(l=>l.Select(v=>new PackSketchRegionSource(v.Entity.Value,(int)v.Kind,MessagePackSections.PackExpression(v.Geometry),v.Reversed)).ToArray()).ToArray(),
                g.Distance,(int)g.Join,g.Loops.Select(l=>new PackSketchRegionLoop(l.Depth,l.Parent,l.InputTraversalReversed,l.Outputs.Select(o=>new PackSketchRegionOutput(o.Curve.Value,
                    new(o.Endpoints.Start.Value,o.Endpoints.End.Value,o.Endpoints.StartEquation.Value,o.Endpoints.EndEquation.Value),(int)o.Role,o.SourceSlots.ToArray(),o.BasisDepth,o.SourceTraversalReversed,
                    MessagePackSections.PackExpression(o.Geometry))).ToArray(),l.Joints.Select(j=>new PackSketchRegionJoint(j.PreviousSourceSlot,j.NextSourceSlot,
                        j.PreviousSource.Value,j.NextSource.Value,(int)j.Kind,j.PreviousParameter,j.NextParameter,j.PreviousTraversalReversed,j.NextTraversalReversed)).ToArray())).ToArray(),g.IsFrozen,g.FrozenReason,g.Algorithm)).ToArray())).ToArray());
    }
    private static void ValidateTables(PackSketchRegionSection value,int version=SchemaVersion)
    {
        if(value is null||value.Version!=version||value.Sketches is null||value.Sketches.Length>10000)throw new InvalidDataException("Unsupported region section.");
        var owners=new HashSet<Guid>();
        foreach(var table in value.Sketches)
        {
            if(table is null||table.Sketch==Guid.Empty||!owners.Add(table.Sketch)||table.Groups is not {Length:>0 and <=10000})throw new InvalidDataException("Invalid region owner table.");
            foreach(var g in table.Groups)
            {
                if(g is null||g.Sources is not {Length:>0 and <=64}||g.Loops is null||g.Loops.Length!=g.Sources.Length||
                    g.Sources.Any(l=>l is not {Length:>0 and <=256})||g.Sources.Sum(l=>l.Length)>4096||
                    g.Loops.Any(l=>l is null||l.Outputs is not {Length:>0 and <=256}||l.Joints is not {Length:<=256}||l.Joints.Any(j=>j is null))||
                    g.Loops.Sum(l=>l.Outputs.Length)>4096||g.Loops.Sum(l=>l.Joints.Length)>4096)
                    throw new InvalidDataException("Region source or output tables exceed their existing bounds.");
            }
        }
    }
    private static ImmutableDictionary<SketchId,CadSketch> Attach(PackSketchRegionSection value,ImmutableDictionary<SketchId,CadSketch> sketches,int version=SchemaVersion)
    {
        ValidateTables(value,version);if(sketches.Values.Any(s=>!s.RegionOffsets.IsEmpty))throw new InvalidDataException("Attach region authors exactly once.");
        var next=sketches;
        foreach(var table in value.Sketches)
        {
            var id=new SketchId(table.Sketch);if(!next.TryGetValue(id,out var sketch))throw new InvalidDataException("Region table names a missing sketch owner.");
            var groups=table.Groups.Select(g=>new SketchRegionOffsetAuthor(g.Id,g.Sources.Select(l=>l.Select(v=>v is null?throw new InvalidDataException("Null region source."):
                new SketchRegionSourceAuthor(new(v.Entity),(SketchExpressionSourceKind)v.Kind,MessagePackSections.ReadExpression(v.Geometry),v.Reversed)).ToImmutableArray()).ToImmutableArray(),
                g.Distance,(SketchRegionAuthorJoin)g.Join,g.Loops.Select(l=>new SketchRegionLoopAuthor(l.Depth,l.Parent,l.Reversed,l.Outputs.Select(o=>
                {
                    if(o is null||o.Endpoints is null||o.Sources is not {Length:1 or 2})throw new InvalidDataException("Incomplete region output.");
                    var e=o.Endpoints;return new SketchRegionOutputAuthor(new(o.Curve),new(new(e.Start),new(e.End),new(e.StartEquation),new(e.EndEquation)),
                        (SketchRegionAuthorRole)o.Role,o.Sources.ToImmutableArray(),o.BasisDepth,o.Reversed,MessagePackSections.ReadExpression(o.Geometry));
                }).ToImmutableArray()){Joints=l.Joints.Select(j=>new SketchRegionJointAuthor(j.PreviousSlot,j.NextSlot,new(j.PreviousSource),new(j.NextSource),
                    (SketchRegionJointKind)j.Kind,j.PreviousParameter,j.NextParameter,j.PreviousReversed,j.NextReversed)).ToImmutableArray()}).ToImmutableArray(),g.Frozen,g.Reason){Algorithm=g.Algorithm}).ToImmutableArray();
            sketch=sketch with{RegionOffsets=groups};sketch.Validate();next=next.SetItem(id,sketch);
        }
        return next;
    }
    private static byte[] Check(byte[] bytes){Check((ReadOnlyMemory<byte>)bytes);return bytes;}
    private static ReadOnlyMemory<byte> Check(ReadOnlyMemory<byte> bytes)
    {if(bytes.Length is <1 or >MaximumBytes)throw new InvalidDataException("Region section byte budget exceeded.");return bytes;}
    private static T Decode<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception error) when(error is ArgumentException or JsonException or MessagePackSerializationException or NullReferenceException or IndexOutOfRangeException or EndOfStreamException)
        {throw new InvalidDataException("Malformed owned region author section.",error);}
    }
    private static void CheckShape(ReadOnlyMemory<byte> bytes,int version=SchemaVersion)
    {
        var reader=new MessagePackReader(bytes);Header(ref reader,2);if(reader.ReadInt32()!=version||version is not (2 or SchemaVersion))throw new InvalidDataException("Unknown region section version.");
        int tables=Array(ref reader,0,10000);
        for(int t=0;t<tables;t++)
        {
            Header(ref reader,2);reader.Skip();int groups=Array(ref reader,1,10000);
            for(int g=0;g<groups;g++)
            {
                Header(ref reader,8);reader.Skip();int loops=Array(ref reader,1,64),total=0;
                for(int l=0;l<loops;l++)
                {
                    int count=Array(ref reader,1,256);total+=count;if(total>4096)throw new InvalidDataException("Region total source budget exceeded.");
                    for(int s=0;s<count;s++){Header(ref reader,4);reader.Skip();reader.Skip();SketchCurveExpressionStorage.CheckMessagePackValue(ref reader);reader.ReadBoolean();}
                }
                reader.ReadDouble();reader.ReadInt32();int outputs=Array(ref reader,1,64);if(outputs!=loops)throw new InvalidDataException("Region source/output loop counts differ.");total=0;int totalJoints=0;
                for(int l=0;l<outputs;l++)
                {
                    Header(ref reader,version==2?4:5);reader.ReadInt32();reader.Skip();reader.ReadBoolean();int count=Array(ref reader,1,256);total+=count;
                    if(total>4096)throw new InvalidDataException("Region total output budget exceeded.");
                    for(int o=0;o<count;o++)
                    {
                        Header(ref reader,7);reader.Skip();Header(ref reader,4);for(int e=0;e<4;e++)reader.Skip();reader.ReadInt32();int sources=Array(ref reader,1,2);
                        for(int s=0;s<sources;s++)reader.ReadInt32();reader.ReadInt32();reader.ReadBoolean();SketchCurveExpressionStorage.CheckMessagePackValue(ref reader);
                    }
                    if(version==SchemaVersion)
                    {
                        int joints=Array(ref reader,0,256);totalJoints+=joints;if(totalJoints>4096)throw new InvalidDataException("Region total connection budget exceeded.");
                        for(int j=0;j<joints;j++)
                        {Header(ref reader,9);reader.ReadInt32();reader.ReadInt32();reader.Skip();reader.Skip();reader.ReadInt32();reader.ReadDouble();reader.ReadDouble();reader.ReadBoolean();reader.ReadBoolean();}
                    }
                }
                reader.ReadBoolean();reader.Skip();reader.ReadString();
            }
        }
        if(!reader.End)throw new InvalidDataException("Trailing region section data.");
    }
    private static void Header(ref MessagePackReader reader,int count)
    {if(reader.ReadArrayHeader()!=count)throw new InvalidDataException("Unexpected region record field count.");}
    private static int Array(ref MessagePackReader reader,int minimum,int maximum)
    {int count=reader.ReadArrayHeader();if(count<minimum||count>maximum)throw new InvalidDataException("Region array budget exceeded.");return count;}
}
