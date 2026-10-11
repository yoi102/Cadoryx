using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackSketchExpressionSection([property:Key(0),JsonRequired] int SchemaVersion,
    [property:Key(1),JsonRequired] PackSketchExpressionTable[] Sketches);
[MessagePackObject]
public sealed record PackSketchExpressionTable([property:Key(0),JsonRequired] Guid Sketch,
    [property:Key(1),JsonRequired] PackSketchExpressionEntity[] Curves,
    [property:Key(2),JsonRequired] PackSketchExpressionContact[]? Contacts=null);
[MessagePackObject]
public sealed record PackSketchExpressionContact([property:Key(0),JsonRequired] int Position,
    [property:Key(1),JsonRequired] PackSketchExpressionContactConstraint Constraint);
[MessagePackObject]
public sealed record PackSketchExpressionContactConstraint([property:Key(0),JsonRequired] Guid Id,
    [property:Key(1),JsonRequired] string Kind,[property:Key(2),JsonRequired] Guid[] Targets,
    [property:Key(3),JsonRequired] double[] Values,[property:Key(4),JsonRequired] bool Enabled)
{
    internal static PackSketchExpressionContactConstraint From(PackSketchConstraint c)=>new(c.Id,c.Kind,c.Targets,c.Values,c.Enabled);
    internal PackSketchConstraint ToConstraint()=>new(Id,Kind,Targets,Values,Enabled);
}
[MessagePackObject]
public sealed record PackLegacySketchExpressionSection([property:Key(0),JsonRequired] int SchemaVersion,
    [property:Key(1),JsonRequired] PackLegacySketchExpressionTable[] Sketches);
[MessagePackObject]
public sealed record PackLegacySketchExpressionTable([property:Key(0),JsonRequired] Guid Sketch,
    [property:Key(1),JsonRequired] PackLegacySketchExpressionEntity[] Curves);
[MessagePackObject]
public sealed record PackLegacySketchExpressionEntity([property:Key(0),JsonRequired] Guid Id,
    [property:Key(1),JsonRequired] PackSketchCurveExpression Geometry,[property:Key(2),JsonRequired] PackSketchExpressionSource? Source,
    [property:Key(3),JsonRequired] bool IsConstruction);
[MessagePackObject]
public sealed record PackLegacySketchExpressionContactSection([property:Key(0),JsonRequired] int SchemaVersion,
    [property:Key(1),JsonRequired] PackLegacySketchExpressionContactTable[] Sketches);
[MessagePackObject]
public sealed record PackLegacySketchExpressionContactTable([property:Key(0),JsonRequired] Guid Sketch,
    [property:Key(1),JsonRequired] PackLegacySketchExpressionEntity[] Curves,
    [property:Key(2),JsonRequired] PackSketchExpressionContact[] Contacts);
[MessagePackObject]
public sealed record PackSketchExpressionEntity([property:Key(0),JsonRequired] Guid Id,
    [property:Key(1),JsonRequired] PackSketchCurveExpression Geometry,[property:Key(2),JsonRequired] PackSketchExpressionSource? Source,
    [property:Key(3),JsonRequired] bool IsConstruction,[property:Key(4),JsonRequired] PackSketchExpressionEndpoints? Endpoints=null);
[MessagePackObject]
public sealed record PackSketchExpressionEndpoints([property:Key(0),JsonRequired] Guid Start,[property:Key(1),JsonRequired] Guid End,
    [property:Key(2),JsonRequired] Guid StartEquation,[property:Key(3),JsonRequired] Guid EndEquation);
[MessagePackObject]
public sealed record PackSketchExpressionSource([property:Key(0),JsonRequired] Guid Entity,[property:Key(1),JsonRequired] int Kind,
    [property:Key(2),JsonRequired] PackSketchCurveExpression Geometry,[property:Key(3),JsonRequired] int BasisDepth,
    [property:Key(4),JsonRequired] bool IsFrozen);

/// <summary>The independent JSON3/MessagePack4 section attaches exact author entities and their
/// contact relations atomically. Geometry bytes alone are never source/native proof.</summary>
public static class SketchExpressionSectionStorage
{
    public const int JsonSchemaVersion=3;
    public const int SchemaVersion=4;
    public const int MaximumBytes=32*1024*1024;
    public static byte[] EncodeJson(IEnumerable<CadSketch> sketches)=>Check(JsonSerializer.SerializeToUtf8Bytes(Pack(sketches) with{SchemaVersion=JsonSchemaVersion},CadJson.Options));
    public static byte[] EncodeMessagePack(IEnumerable<CadSketch> sketches)=>Check(MessagePackSerializer.Serialize(Pack(sketches)));
    public static ImmutableDictionary<SketchId,CadSketch> AttachJson(ReadOnlyMemory<byte> bytes,ImmutableDictionary<SketchId,CadSketch> sketches)=>
        Decode(()=>Attach(JsonSerializer.Deserialize<PackSketchExpressionSection>(Check(bytes).Span,CadJson.Options)??
            throw new InvalidDataException("Null expression section."),sketches,JsonSchemaVersion));
    public static ImmutableDictionary<SketchId,CadSketch> AttachMessagePack(ReadOnlyMemory<byte> bytes,ImmutableDictionary<SketchId,CadSketch> sketches)=>Decode(()=>
    {
        CheckShape(Check(bytes));return Attach(MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes,
            MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData)),sketches);
    });
    internal static byte[] JsonToMessagePack(ReadOnlyMemory<byte> bytes,int version=JsonSchemaVersion)=>Decode(()=>
    {
        var value=version==1?UpgradeLegacy(JsonSerializer.Deserialize<PackLegacySketchExpressionSection>(Check(bytes).Span,CadJson.Options)!,1):
            version==2?UpgradeLegacyContacts(JsonSerializer.Deserialize<PackLegacySketchExpressionContactSection>(Check(bytes).Span,CadJson.Options)!,2):
            JsonSerializer.Deserialize<PackSketchExpressionSection>(Check(bytes).Span,CadJson.Options)??throw new InvalidDataException("Null expression section.");
        _=Read(value,version<=2?SchemaVersion:JsonSchemaVersion);return Check(MessagePackSerializer.Serialize(value with{SchemaVersion=SchemaVersion}));
    });
    internal static byte[] UpgradeMessagePack(ReadOnlyMemory<byte> bytes,int version=2)=>Decode(()=>
    {
        CheckShape(Check(bytes),version);
        var options=MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
        var value=version==2?UpgradeLegacy(MessagePackSerializer.Deserialize<PackLegacySketchExpressionSection>(bytes,options),2):
            UpgradeLegacyContacts(MessagePackSerializer.Deserialize<PackLegacySketchExpressionContactSection>(bytes,options),3);
        _=Read(value);return Check(MessagePackSerializer.Serialize(value));
    });
    private static PackSketchExpressionEntity UpgradeEntity(PackLegacySketchExpressionEntity c)=>c is null?
        throw new InvalidDataException("Null legacy expression entity."):new(c.Id,c.Geometry,c.Source,c.IsConstruction,null);
    private static PackSketchExpressionSection UpgradeLegacy(PackLegacySketchExpressionSection value,int version)
    {
        if(value is null||value.SchemaVersion!=version||value.Sketches is null)throw new InvalidDataException("Invalid legacy expression section.");
        return new(SchemaVersion,value.Sketches.Select(t=>t is null?throw new InvalidDataException("Null legacy expression table."):
            new PackSketchExpressionTable(t.Sketch,t.Curves.Select(UpgradeEntity).ToArray(),[])).ToArray());
    }
    private static PackSketchExpressionSection UpgradeLegacyContacts(PackLegacySketchExpressionContactSection value,int version)
    {
        if(value is null||value.SchemaVersion!=version||value.Sketches is null)throw new InvalidDataException("Invalid legacy expression contact section.");
        return new(SchemaVersion,value.Sketches.Select(t=>
        {
            if(t is null||t.Curves is null||t.Contacts is null||t.Contacts.Any(c=>c is null||c.Constraint is null||
                c.Constraint.Kind is not ("finite-parametric-contact" or "finite-parametric-curvature" or "sliding-parametric-contact")))
                throw new InvalidDataException("Endpoint relations cannot appear in a legacy expression-contact table.");
            return new PackSketchExpressionTable(t.Sketch,t.Curves.Select(UpgradeEntity).ToArray(),t.Contacts);
        }).ToArray());
    }
    internal static bool HasContacts(ReadOnlyMemory<byte> bytes,string encoding,int version)=>Decode(()=>
    {
        if(encoding=="messagepack"&&version==3)
        {CheckShape(Check(bytes),3);return Read(UpgradeLegacyContacts(MessagePackSerializer.Deserialize<PackLegacySketchExpressionContactSection>(bytes,
            MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData)),3)).Values.Any(t=>!t.Contacts.IsEmpty);}
        if(encoding=="json"&&version==2)
            return Read(UpgradeLegacyContacts(JsonSerializer.Deserialize<PackLegacySketchExpressionContactSection>(Check(bytes).Span,CadJson.Options)!,2)).Values.Any(t=>!t.Contacts.IsEmpty);
        if(encoding=="messagepack"&&version==SchemaVersion)
        {CheckShape(Check(bytes));return Read(MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes,
            MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData))).Values.Any(t=>!t.Contacts.IsEmpty);}
        if(encoding=="json"&&version==JsonSchemaVersion)
            return Read(JsonSerializer.Deserialize<PackSketchExpressionSection>(Check(bytes).Span,CadJson.Options)!,JsonSchemaVersion).Values.Any(t=>!t.Contacts.IsEmpty);
        return false;
    });
    internal static bool HasEndpoints(ReadOnlyMemory<byte> bytes,string encoding,int version)=>Decode(()=>
    {
        PackSketchExpressionSection? value=null;
        if(encoding=="messagepack"&&version==SchemaVersion)
        {CheckShape(Check(bytes));value=MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes,MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData));}
        else if(encoding=="json"&&version==JsonSchemaVersion)value=JsonSerializer.Deserialize<PackSketchExpressionSection>(Check(bytes).Span,CadJson.Options);
        return value is not null&&Read(value,encoding=="json"?JsonSchemaVersion:SchemaVersion).Values.Any(t=>
            t.Curves.Any(c=>c.Endpoints is not null)||t.Contacts.Any(c=>c.Constraint is EndpointTangentConstraint or EndpointCurvatureConstraint));
    });
    private static PackSketchExpressionSection Pack(IEnumerable<CadSketch> sketches)
    {
        var all=sketches.ToArray();foreach(var sketch in all)sketch.Validate();
        if(all.Select(s=>s.Id).Distinct().Count()!=all.Length)throw new InvalidDataException("Duplicate sketch registry identity.");
        return new(SchemaVersion,all.Where(s=>!s.ExpressionCurves.IsEmpty).OrderBy(s=>s.Id.Value).Select(s=>new PackSketchExpressionTable(s.Id.Value,
            s.ExpressionCurves.Select(c=>new PackSketchExpressionEntity(c.Id.Value,MessagePackSections.PackExpression(c.Geometry),
                c.Source is {} source?new(source.Entity.Value,(int)source.Kind,MessagePackSections.PackExpression(source.Geometry),source.BasisDepth,source.IsFrozen):null,
                c.IsConstruction,c.Endpoints is {} e?new(e.Start.Value,e.End.Value,e.StartEquation.Value,e.EndEquation.Value):null)).ToArray(),
                s.Constraints.Select((c,i)=>(c,i)).Where(p=>SketchFiniteParametricContact.UsesExpression(s,p.c))
                    .Select(p=>new PackSketchExpressionContact(p.i,PackSketchExpressionContactConstraint.From(MessagePackSections.SketchConstraint(p.c)))).ToArray())).ToArray());
    }
    private sealed record AuthorTable(ImmutableArray<SketchExpressionCurve> Curves,ImmutableArray<(int Position,SketchConstraint Constraint)> Contacts);
    private static ImmutableDictionary<SketchId,AuthorTable> Read(PackSketchExpressionSection value,int version=SchemaVersion)
    {
        if(value is null||value.SchemaVersion!=version||value.Sketches is null||value.Sketches.Length>10000)
            throw new InvalidDataException("Unsupported or incomplete expression entity section.");
        var result=ImmutableDictionary.CreateBuilder<SketchId,AuthorTable>();
        foreach(var table in value.Sketches)
        {
            if(table is null||table.Sketch==Guid.Empty||table.Curves is not {Length:>0 and <=10000}||table.Contacts is null||table.Contacts.Length>10000)
                throw new InvalidDataException("Expression section requires nonempty bounded sketch tables.");
            var curves=table.Curves.Select(c=>c is null?throw new InvalidDataException("Null expression entity."):
                new SketchExpressionCurve(new(c.Id),MessagePackSections.ReadExpression(c.Geometry),c.Source is {} source?
                    new(new(source.Entity),(SketchExpressionSourceKind)source.Kind,MessagePackSections.ReadExpression(source.Geometry),source.BasisDepth,source.IsFrozen):null,c.IsConstruction)
                    {Endpoints=c.Endpoints is {} e?new(new(e.Start),new(e.End),new(e.StartEquation),new(e.EndEquation)):null}).ToImmutableArray();
            var ids=curves.Select(c=>c.Id).ToHashSet();
            var contacts=table.Contacts.Select(c=>
            {
                if(c is null||c.Position is <0 or >=10000||c.Constraint is null||c.Constraint.Kind is not
                    ("finite-parametric-contact" or "finite-parametric-curvature" or "sliding-parametric-contact" or "endpoint-tangent-curves" or "endpoint-curvature-curves"))
                    throw new InvalidDataException("Invalid expression contact record.");
                var constraint=MessagePackSections.SketchConstraint(c.Constraint.ToConstraint());
                var pair=constraint switch{FiniteParametricContactConstraint p=>(p.A,p.B),EndpointTangentConstraint p=>(p.A,p.B),EndpointCurvatureConstraint p=>(p.A,p.B),_=>throw new InvalidDataException("Invalid expression relationship.")};
                if(!ids.Contains(pair.Item1)&&!ids.Contains(pair.Item2))
                    throw new InvalidDataException("Expression contacts require a target in the same owned expression table.");
                return(c.Position,constraint);
            }).ToImmutableArray();
            if(contacts.Select(c=>c.Position).Distinct().Count()!=contacts.Length)throw new InvalidDataException("Duplicate expression contact position.");
            if(!result.TryAdd(new(table.Sketch),new(curves,contacts)))throw new InvalidDataException("Duplicate expression sketch table.");
        }
        return result.ToImmutable();
    }
    private static ImmutableDictionary<SketchId,CadSketch> Attach(PackSketchExpressionSection value,ImmutableDictionary<SketchId,CadSketch> sketches,int version=SchemaVersion)
    {
        if(sketches.Values.Any(s=>!s.ExpressionCurves.IsEmpty))throw new InvalidDataException("Expression entity table must be attached exactly once.");
        var next=sketches;
        foreach(var(id,table) in Read(value,version))
        {
            if(!next.TryGetValue(id,out var sketch))throw new InvalidDataException("Expression table names a missing sketch owner.");
            int count=sketch.Constraints.Length+table.Contacts.Length;
            if(count>10000||table.Contacts.Any(c=>c.Position>=count))throw new InvalidDataException("Expression contact position exceeds the combined constraint table.");
            var contacts=table.Contacts.ToDictionary(c=>c.Position,c=>c.Constraint);var constraints=ImmutableArray.CreateBuilder<SketchConstraint>(count);int basic=0;
            for(int i=0;i<count;i++)constraints.Add(contacts.TryGetValue(i,out var contact)?contact:sketch.Constraints[basic++]);
            sketch=sketch with{ExpressionCurves=table.Curves,Constraints=constraints.ToImmutable()};sketch.Validate();next=next.SetItem(id,sketch);
        }
        return next;
    }
    private static T Decode<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception ex) when(ex is ArgumentException or JsonException or MessagePackSerializationException or NullReferenceException)
        {throw new InvalidDataException("Malformed exact expression entity section.",ex);}
    }
    private static byte[] Check(byte[] bytes){Check((ReadOnlyMemory<byte>)bytes);return bytes;}
    private static ReadOnlyMemory<byte> Check(ReadOnlyMemory<byte> bytes)=>bytes.Length is >0 and <=MaximumBytes?bytes:
        throw new InvalidDataException("Expression entity section exceeds its byte budget.");
    private static void Record(ref MessagePackReader reader,int count)
    {if(reader.ReadArrayHeader()!=count)throw new InvalidDataException("Unexpected expression entity section fields.");}
    private static void CheckShape(ReadOnlyMemory<byte> bytes,int version=SchemaVersion)
    {
        var reader=new MessagePackReader(bytes);Record(ref reader,2);
        if(reader.ReadInt32()!=version)throw new InvalidDataException("Unsupported expression entity schema.");
        int sketches=reader.ReadArrayHeader();if(sketches>10000)throw new InvalidDataException("Expression sketch table budget exceeded.");
        for(int i=0;i<sketches;i++)
        {
            Record(ref reader,version==2?2:3);reader.Skip();int curves=reader.ReadArrayHeader();
            if(curves is <1 or >10000)throw new InvalidDataException("Expression entity table budget exceeded.");
            for(int j=0;j<curves;j++)
            {
                Record(ref reader,version==SchemaVersion?5:4);reader.Skip();SketchCurveExpressionStorage.CheckMessagePackValue(ref reader);
                if(!reader.TryReadNil())
                {Record(ref reader,5);reader.Skip();reader.ReadInt32();SketchCurveExpressionStorage.CheckMessagePackValue(ref reader);reader.ReadInt32();reader.ReadBoolean();}
                reader.ReadBoolean();
                if(version==SchemaVersion&&!reader.TryReadNil())
                {Record(ref reader,4);reader.Skip();reader.Skip();reader.Skip();reader.Skip();}
            }
            if(version>=3)
            {
                int contacts=reader.ReadArrayHeader();if(contacts>10000)throw new InvalidDataException("Expression contact budget exceeded.");
                for(int j=0;j<contacts;j++)
                {
                    Record(ref reader,2);reader.ReadInt32();Record(ref reader,5);reader.Skip();reader.ReadString();
                    int targets=reader.ReadArrayHeader();if(targets is not (2 or 3)||version<SchemaVersion&&targets!=2)throw new InvalidDataException("Invalid expression contact targets.");
                    for(int k=0;k<targets;k++)reader.Skip();int values=reader.ReadArrayHeader();
                    if(values is not (1 or 3 or 11)||version<SchemaVersion&&values==1)throw new InvalidDataException("Invalid expression contact values.");
                    for(int k=0;k<values;k++)reader.ReadDouble();reader.ReadBoolean();
                }
            }
        }
        if(!reader.End)throw new InvalidDataException("Trailing expression entity section data.");
    }
}
