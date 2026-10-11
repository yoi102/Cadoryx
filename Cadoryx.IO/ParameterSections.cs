using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackParameters([property:Key(0)] PackParameter[] Parameters,[property:Key(1)] PackParameterBinding[] Bindings);
[MessagePackObject]
public sealed record PackParameter([property:Key(0)] Guid Id,[property:Key(1)] string Name,[property:Key(2)] int Dimension,
    [property:Key(3)] string Expression,[property:Key(4)] PackParameterTarget? Reference);
[MessagePackObject]
public sealed record PackParameterTarget([property:Key(0)] int Kind,[property:Key(1)] Guid Owner,[property:Key(2)] Guid? Constraint,[property:Key(3)] string Property,
    [property:Key(4)] Guid? Curve=null,[property:Key(5)] int? Index=null);
[MessagePackObject]
public sealed record PackParameterBinding([property:Key(0)] Guid Id,[property:Key(1)] Guid Parameter,[property:Key(2)] PackParameterTarget Target);

internal static partial class MessagePackSections
{
    private static PackParameterTarget Target(ParameterTarget t)=>new((int)t.Kind,t.Owner,t.Constraint,t.Property,t.Curve,t.Index);
    private static ParameterTarget Target(PackParameterTarget t)=>new((ParameterTargetKind)t.Kind,t.Owner,t.Constraint,t.Property,t.Curve,t.Index);
    internal static byte[] UpgradeCurveParameters(ReadOnlyMemory<byte> bytes)
    {
        var book=Read<PackParameters>(bytes);
        var targets=book.Parameters.Select(p=>p.Reference).Concat(book.Bindings.Select(b=>b.Target)).OfType<PackParameterTarget>();
        if(targets.Any(t=>t.Kind>3||t.Curve is not null||t.Index is not null))
            throw new InvalidDataException("Exact curve targets cannot masquerade as the legacy parameter schema.");
        return bytes.ToArray();
    }
    internal static bool HasRegionParameterTargets(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var reader=new MessagePackReader(bytes);bool region=false;
            if(reader.ReadArrayHeader()!=2)throw new InvalidDataException("Invalid parameter table shape.");
            int parameters=reader.ReadArrayHeader();if(parameters>256)throw new InvalidDataException("Parameter table exceeds limits.");
            for(int i=0;i<parameters;i++)
            {
                if(reader.ReadArrayHeader()!=5)throw new InvalidDataException("Invalid parameter record shape.");
                for(int k=0;k<4;k++)reader.Skip();region|=TargetShape(ref reader,true);
            }
            int bindings=reader.ReadArrayHeader();if(bindings>1024)throw new InvalidDataException("Parameter binding table exceeds limits.");
            for(int i=0;i<bindings;i++)
            {
                if(reader.ReadArrayHeader()!=3)throw new InvalidDataException("Invalid parameter binding shape.");
                reader.Skip();reader.Skip();region|=TargetShape(ref reader,false);
            }
            if(!reader.End)throw new InvalidDataException("Trailing parameter bytes.");return region;
        }
        catch(Exception ex) when(ex is MessagePackSerializationException or System.IO.EndOfStreamException or OverflowException)
        {throw new InvalidDataException("Malformed bounded parameter table.",ex);}
        static bool TargetShape(ref MessagePackReader reader,bool nullable)
        {
            if(reader.TryReadNil())return nullable?false:throw new InvalidDataException("Missing driving target.");
            int fields=reader.ReadArrayHeader();if(fields is not (4 or 6))throw new InvalidDataException("Invalid parameter target shape.");
            int kind=reader.ReadInt32();for(int k=1;k<fields;k++)reader.Skip();return kind>=5;
        }
    }
    internal static byte[] UpgradeRegionParameters(ReadOnlyMemory<byte> bytes)
    {
        if(HasRegionParameterTargets(bytes))throw new InvalidDataException("Whole-region parameter targets cannot masquerade as schema2 parameters.");
        return bytes.ToArray();
    }
    public static byte[] EncodeParameters(ParameterBook book)=>Serialize(new PackParameters(
        book.Parameters.OrderBy(p=>p.Id).Select(p=>new PackParameter(p.Id,p.Name,(int)p.Dimension,p.Expression,p.Reference is {} target?Target(target):null)).ToArray(),
        book.Bindings.OrderBy(b=>b.Id).Select(b=>new PackParameterBinding(b.Id,b.Parameter,Target(b.Target))).ToArray()));
    public static ParameterBook DecodeParameters(ReadOnlyMemory<byte> bytes)
    {
        _=HasRegionParameterTargets(bytes);var book=Read<PackParameters>(bytes);
        if(book.Parameters.Length>256||book.Bindings.Length>1024)throw new InvalidDataException("Parameter table exceeds limits.");
        return new(book.Parameters.Select(p=>new CadParameter(p.Id,p.Name,(ParameterDimension)p.Dimension,p.Expression,p.Reference is {} target?Target(target):null)).ToImmutableArray(),
            book.Bindings.Select(b=>new ParameterBinding(b.Id,b.Parameter,Target(b.Target))).ToImmutableArray());
    }
}
