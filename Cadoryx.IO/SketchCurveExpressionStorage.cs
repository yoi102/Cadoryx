using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

// Independent geometry schema. Node indices describe owned author values, never native identities.
[MessagePackObject]
[MessagePackFormatter(typeof(SketchCurveExpressionPackFormatter))]
public sealed record PackSketchCurveExpression([property:Key(0),JsonRequired] int SchemaVersion,[property:Key(1),JsonRequired] PackSketchExpressionNode[] Nodes);
[MessagePackObject]
public sealed record PackSketchExpressionNode([property:Key(0),JsonRequired] int Kind,[property:Key(1),JsonRequired] int BasisIndex,
    [property:Key(2),JsonRequired] PackSketchExpressionPrimitive? Primitive,[property:Key(3),JsonRequired] double SignedOffset,
    [property:Key(4),JsonRequired] double FirstParameter,[property:Key(5),JsonRequired] double LastParameter,[property:Key(6),JsonRequired] bool Reversed);

// Keep geometry schema1's original eight-field primitive leaf independent from feature boundary evolution.
[MessagePackObject]
public sealed record PackSketchExpressionPrimitive([property:Key(0)] double[] Start,[property:Key(1)] double[] End,
    [property:Key(2)] double[]? Middle,[property:Key(3)] double[]? BezierControl,
    [property:Key(4)] double[][]? SplineControls,[property:Key(5)] PackSplineKnots? SplineKnots,
    [property:Key(6)] PackEllipseGeometry? Ellipse,[property:Key(7)] PackFreeformGeometry? Freeform);

/// <summary>Strict, bounded portable author geometry. This codec is also the section payload
/// contract for document integration; it does not migrate or publish a document on its own.</summary>
public static class SketchCurveExpressionStorage
{
    public const int SchemaVersion=1;
    public const int MaximumBytes=1024*1024;
    public static byte[] EncodeJson(SketchCurveExpressionGeometry expression)=>
        CheckSize(JsonSerializer.SerializeToUtf8Bytes(MessagePackSections.PackExpression(expression),CadJson.Options));
    public static SketchCurveExpressionGeometry DecodeJson(ReadOnlyMemory<byte> bytes)=>Decode(()=>
        MessagePackSections.ReadExpression(JsonSerializer.Deserialize<PackSketchCurveExpression>(CheckSize(bytes).Span,CadJson.Options)??
            throw new InvalidDataException("Null curve expression payload.")));
    public static byte[] EncodeMessagePack(SketchCurveExpressionGeometry expression)=>
        CheckSize(MessagePackSections.EncodeExpression(expression));
    public static SketchCurveExpressionGeometry DecodeMessagePack(ReadOnlyMemory<byte> bytes)=>Decode(()=>
    {
        CheckMessagePackShape(CheckSize(bytes));return MessagePackSections.DecodeExpression(bytes);
    });
    private static T Decode<T>(Func<T> decode)
    {
        try{return decode();}
        catch(Exception error) when(error is CadValidationException or ArgumentException or JsonException or MessagePackSerializationException or NullReferenceException)
        {throw new InvalidDataException("Malformed exact curve expression payload.",error);}
    }
    private static byte[] CheckSize(byte[] bytes){CheckSize((ReadOnlyMemory<byte>)bytes);return bytes;}
    private static ReadOnlyMemory<byte> CheckSize(ReadOnlyMemory<byte> bytes)=>bytes.Length is >0 and <=MaximumBytes?bytes:
        throw new InvalidDataException("Exact expression payload exceeds its bounded byte contract.");
    private static void Record(ref MessagePackReader reader,int fields)
    {if(reader.ReadArrayHeader()!=fields)throw new InvalidDataException("Unexpected fields in exact expression schema1.");}
    private static void Point(ref MessagePackReader reader)
    {Record(ref reader,2);reader.ReadDouble();reader.ReadDouble();}
    private static void PointTable(ref MessagePackReader reader,int maximum)
    {
        int count=reader.ReadArrayHeader();if(count>maximum)throw new InvalidDataException("Expression control table exceeds its author budget.");
        for(int i=0;i<count;i++)Point(ref reader);
    }
    private static void Scalars(ref MessagePackReader reader,int maximum,bool integers=false)
    {
        int count=reader.ReadArrayHeader();if(count>maximum)throw new InvalidDataException("Expression scalar table exceeds its author budget.");
        for(int i=0;i<count;i++){if(integers)reader.ReadInt32();else reader.ReadDouble();}
    }
    private static void Primitive(ref MessagePackReader reader)
    {
        if(reader.TryReadNil())return;Record(ref reader,8);Point(ref reader);Point(ref reader);
        if(!reader.TryReadNil())Point(ref reader);if(!reader.TryReadNil())Point(ref reader);
        if(!reader.TryReadNil())PointTable(ref reader,CubicSplineDefinition.MaximumControls);
        if(!reader.TryReadNil()){Record(ref reader,2);Scalars(ref reader,SketchFreeformGeometry.MaximumPoles+26);Scalars(ref reader,SketchFreeformGeometry.MaximumPoles+26,true);}
        if(!reader.TryReadNil()){Record(ref reader,5);Point(ref reader);Point(ref reader);Point(ref reader);reader.ReadDouble();reader.ReadDouble();}
        if(!reader.TryReadNil())
        {
            Record(ref reader,10);reader.ReadInt32();PointTable(ref reader,SketchFreeformGeometry.MaximumPoles);
            Scalars(ref reader,SketchFreeformGeometry.MaximumPoles);Scalars(ref reader,SketchFreeformGeometry.MaximumPoles+26);
            Scalars(ref reader,SketchFreeformGeometry.MaximumPoles+26,true);reader.ReadInt32();reader.ReadBoolean();reader.ReadDouble();reader.ReadDouble();reader.ReadBoolean();
        }
    }
    private static void CheckMessagePackShape(ReadOnlyMemory<byte> bytes)
    {
        var reader=new MessagePackReader(bytes);CheckMessagePackValue(ref reader);
        if(!reader.End)throw new InvalidDataException("Trailing expression geometry data.");
    }
    internal static void CheckMessagePackValue(ref MessagePackReader reader)
    {
        Record(ref reader,2);
        if(reader.ReadInt32()!=SchemaVersion)throw new InvalidDataException("Unsupported exact expression geometry schema.");
        int nodes=reader.ReadArrayHeader();if(nodes is <1 or >SketchCurveExpressionGeometry.MaximumDepth)throw new InvalidDataException("Invalid expression node count.");
        for(int i=0;i<nodes;i++)
        {Record(ref reader,7);reader.ReadInt32();reader.ReadInt32();Primitive(ref reader);reader.ReadDouble();reader.ReadDouble();reader.ReadDouble();reader.ReadBoolean();}
    }
}

internal static partial class MessagePackSections
{
    private static PackSketchExpressionPrimitive PackExpressionPrimitive(SketchBoundaryCurve c)
    {
        if(c.Expression is not null)throw new InvalidDataException("An expression leaf cannot contain another expression boundary.");
        var p=PackCurve(c);return new(p.Start,p.End,p.Middle,p.BezierControl,p.SplineControls,p.SplineKnots,p.Ellipse,p.Freeform);
    }
    private static SketchBoundaryCurve ReadExpressionPrimitive(PackSketchExpressionPrimitive p)=>
        ReadCurve(new(p.Start,p.End,p.Middle,p.BezierControl,p.SplineControls,p.SplineKnots,p.Ellipse,p.Freeform));
    internal static PackSketchCurveExpression PackExpression(SketchCurveExpressionGeometry expression)
    {
        expression.Validate();var chain=new List<SketchCurveExpressionGeometry>();for(var node=expression;node is not null;node=node.Basis)chain.Add(node);chain.Reverse();
        return new(SketchCurveExpressionStorage.SchemaVersion,chain.Select((node,i)=>new PackSketchExpressionNode((int)node.Kind,i-1,
            node.Primitive is {} primitive?PackExpressionPrimitive(primitive):null,node.SignedOffset,node.FirstParameter,node.LastParameter,node.Reversed)).ToArray());
    }
    internal static SketchCurveExpressionGeometry ReadExpression(PackSketchCurveExpression value)
    {
        if(value.SchemaVersion!=SketchCurveExpressionStorage.SchemaVersion||value.Nodes is not {Length:>=1 and <=SketchCurveExpressionGeometry.MaximumDepth})
            throw new InvalidDataException("Unsupported or incomplete expression geometry schema.");
        SketchCurveExpressionGeometry? result=null;
        for(int i=0;i<value.Nodes.Length;i++)
        {
            var node=value.Nodes[i];if(node is null||node.BasisIndex!=i-1||i==0&&node.Kind!=(int)SketchCurveExpressionKind.Primitive||i>0&&node.Kind==(int)SketchCurveExpressionKind.Primitive)
                throw new InvalidDataException("Expression nodes need one leaf and an explicit preceding basis; cycles and unused branches are invalid.");
            result=new((SketchCurveExpressionKind)node.Kind,node.Primitive is null?null:ReadExpressionPrimitive(node.Primitive),result,
                node.SignedOffset,node.FirstParameter,node.LastParameter,node.Reversed);
        }
        result!.Validate();return result;
    }
    internal static byte[] EncodeExpression(SketchCurveExpressionGeometry expression)=>Serialize(PackExpression(expression));
    internal static SketchCurveExpressionGeometry DecodeExpression(ReadOnlyMemory<byte> bytes)=>ReadExpression(Read<PackSketchCurveExpression>(bytes));
}
