using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackSketches([property:Key(0)] PackSketch[] Sketches);
[MessagePackObject]
public sealed record PackSketch([property:Key(0)] Guid Id,[property:Key(1)] Guid Part,[property:Key(2)] string Name,
    [property:Key(3)] PackTransform Plane,[property:Key(4)] PackSketchPoint[] Points,[property:Key(5)] PackSketchLine[] Lines,
    [property:Key(6)] PackSketchCircle[] Circles,[property:Key(7)] PackSketchConstraint[] Constraints,[property:Key(8)] Guid Revision=default);
[MessagePackObject]
public sealed record PackSketchPoint([property:Key(0)] Guid Id,[property:Key(1)] double X,[property:Key(2)] double Y,[property:Key(3)] bool Construction);
[MessagePackObject]
public sealed record PackSketchLine([property:Key(0)] Guid Id,[property:Key(1)] Guid Start,[property:Key(2)] Guid End,[property:Key(3)] bool Construction);
[MessagePackObject]
public sealed record PackSketchCircle([property:Key(0)] Guid Id,[property:Key(1)] Guid Center,[property:Key(2)] double Radius,[property:Key(3)] bool Construction);
[MessagePackObject]
public sealed record PackSketchConstraint([property:Key(0)] Guid Id,[property:Key(1)] string Kind,[property:Key(2)] Guid[] Targets,
    [property:Key(3)] double[] Values,[property:Key(4)] bool Enabled);

internal static partial class MessagePackSections
{
    internal static byte[] EncodeSketches(IEnumerable<CadSketch> sketches)=>Serialize(new PackSketches(sketches.OrderBy(s=>s.Id.Value).Select(s=>new PackSketch(
        s.Id.Value,s.PartId.Value,s.Name,T(s.Plane),s.Points.Select(p=>new PackSketchPoint(p.Id.Value,p.Position.X,p.Position.Y,p.IsConstruction)).ToArray(),
        s.Lines.Select(l=>new PackSketchLine(l.Id.Value,l.Start.Value,l.End.Value,l.IsConstruction)).ToArray(),
        s.Circles.Select(c=>new PackSketchCircle(c.Id.Value,c.Center.Value,c.Radius,c.IsConstruction)).ToArray(),s.Constraints.Select(SketchConstraint).ToArray(),s.Revision)).ToArray()));

    internal static byte[] UpgradeSketchRevisions(ReadOnlyMemory<byte> bytes)
    {
        var legacy=Read<PackSketches>(bytes);
        if(legacy.Sketches.Any(s=>s.Revision!=Guid.Empty))throw new InvalidDataException("Unexpected revision in legacy sketch data.");
        // Stable baseline on every load of the same legacy document; subsequent edits receive fresh revisions.
        return Serialize(new PackSketches(legacy.Sketches.Select(s=>s with{Revision=s.Id}).ToArray()));
    }

    internal static ImmutableDictionary<SketchId,CadSketch> DecodeSketches(ReadOnlyMemory<byte> bytes)
    {
        var result=ImmutableDictionary.CreateBuilder<SketchId,CadSketch>();
        foreach(var s in Read<PackSketches>(bytes).Sketches)
        {
            var sketch=new CadSketch(new(s.Id),new(s.Part),s.Name,T(s.Plane),
                s.Points.Select(p=>new SketchPoint(new(p.Id),new(p.X,p.Y),p.Construction)).ToImmutableArray(),
                s.Lines.Select(l=>new SketchLine(new(l.Id),new(l.Start),new(l.End),l.Construction)).ToImmutableArray(),
                s.Circles.Select(c=>new SketchCircle(new(c.Id),new(c.Center),c.Radius,c.Construction)).ToImmutableArray(),
                s.Constraints.Select(SketchConstraint).ToImmutableArray()){Revision=s.Revision};
            sketch.Validate();result.Add(sketch.Id,sketch);
        }
        return result.ToImmutable();
    }
    private static PackSketchConstraint SketchConstraint(SketchConstraint c)
    {
        (string Kind,SketchEntityId[] Targets,double[] Values) data=c switch
        {
            FixPointConstraint f=>("fix-point",[f.Point],[f.Position.X,f.Position.Y]),
            CoincidentConstraint p=>("coincident",[p.A,p.B],[]),HorizontalConstraint h=>("horizontal",[h.Line],[]),VerticalConstraint v=>("vertical",[v.Line],[]),
            DistanceConstraint d=>("distance",[d.A,d.B],[d.Distance]),OffsetXConstraint x=>("offset-x",[x.A,x.B],[x.Offset]),OffsetYConstraint y=>("offset-y",[y.A,y.B],[y.Offset]),
            LengthConstraint l=>("length",[l.Line],[l.Length]),ParallelConstraint p=>("parallel",[p.A,p.B],[]),PerpendicularConstraint p=>("perpendicular",[p.A,p.B],[]),
            EqualLengthConstraint e=>("equal-length",[e.A,e.B],[]),RadiusConstraint r=>("radius",[r.Circle],[r.Radius]),EqualRadiusConstraint e=>("equal-radius",[e.A,e.B],[]),
            _=>throw new NotSupportedException("Unknown sketch constraint cannot be persisted.")
        };
        return new(c.Id.Value,data.Kind,data.Targets.Select(t=>t.Value).ToArray(),data.Values,c.IsEnabled);
    }
    private static SketchConstraint SketchConstraint(PackSketchConstraint c)
    {
        var (targets,values)=c.Kind switch
        {
            "fix-point"=>(1,2),"horizontal" or "vertical"=>(1,0),"length" or "radius"=>(1,1),
            "distance" or "offset-x" or "offset-y"=>(2,1),"coincident" or "parallel" or "perpendicular" or "equal-length" or "equal-radius"=>(2,0),
            _=>throw new NotSupportedException("Unknown sketch constraint: "+c.Kind)
        };
        if(c.Targets.Length!=targets||c.Values.Length!=values)throw new InvalidDataException("Malformed sketch constraint arguments.");
        var id=new SketchConstraintId(c.Id);var a=new SketchEntityId(c.Targets[0]);var b=targets==2?new SketchEntityId(c.Targets[1]):default;
        double value=values>0?c.Values[0]:0;bool enabled=c.Enabled;
        return c.Kind switch
        {
            "fix-point"=>new FixPointConstraint(id,a,new(value,c.Values[1]),enabled),"coincident"=>new CoincidentConstraint(id,a,b,enabled),
            "horizontal"=>new HorizontalConstraint(id,a,enabled),"vertical"=>new VerticalConstraint(id,a,enabled),
            "distance"=>new DistanceConstraint(id,a,b,value,enabled),"offset-x"=>new OffsetXConstraint(id,a,b,value,enabled),"offset-y"=>new OffsetYConstraint(id,a,b,value,enabled),
            "length"=>new LengthConstraint(id,a,value,enabled),"parallel"=>new ParallelConstraint(id,a,b,enabled),"perpendicular"=>new PerpendicularConstraint(id,a,b,enabled),
            "equal-length"=>new EqualLengthConstraint(id,a,b,enabled),"radius"=>new RadiusConstraint(id,a,value,enabled),"equal-radius"=>new EqualRadiusConstraint(id,a,b,enabled),
            _=>throw new NotSupportedException(c.Kind)
        };
    }
}
