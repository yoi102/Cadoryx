using System.Collections.Immutable;

namespace Cadoryx.Db;

public readonly record struct SketchEntityId(Guid Value) : ICadId
{
    public static SketchEntityId New()=>new(Guid.NewGuid());
    public override string ToString()=>Value.ToString("D");
}
public readonly record struct SketchConstraintId(Guid Value) : ICadId
{
    public static SketchConstraintId New()=>new(Guid.NewGuid());
    public override string ToString()=>Value.ToString("D");
}

// Points have stable identities. Adjacent lines may share an endpoint without a coincidence equation.
public sealed record SketchPoint(SketchEntityId Id,Point2d Position,bool IsConstruction=false);
public sealed record SketchLine(SketchEntityId Id,SketchEntityId Start,SketchEntityId End,bool IsConstruction=false);
public sealed record SketchCircle(SketchEntityId Id,SketchEntityId Center,double Radius,bool IsConstruction=false);

public abstract record SketchConstraint(SketchConstraintId Id,bool IsEnabled);
public sealed record FixPointConstraint(SketchConstraintId Id,SketchEntityId Point,Point2d Position,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record CoincidentConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record HorizontalConstraint(SketchConstraintId Id,SketchEntityId Line,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record VerticalConstraint(SketchConstraintId Id,SketchEntityId Line,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record DistanceConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,double Distance,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record OffsetXConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,double Offset,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record OffsetYConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,double Offset,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record LengthConstraint(SketchConstraintId Id,SketchEntityId Line,double Length,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record ParallelConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record PerpendicularConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record EqualLengthConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record RadiusConstraint(SketchConstraintId Id,SketchEntityId Circle,double Radius,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record EqualRadiusConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);

/// <summary>Local coordinates are millimeters; Plane maps local XY into the owning part. Solver state is transient.</summary>
public sealed record CadSketch(SketchId Id,DefinitionId PartId,string Name,RigidTransform3d Plane,
    ImmutableArray<SketchPoint> Points,ImmutableArray<SketchLine> Lines,ImmutableArray<SketchCircle> Circles,
    ImmutableArray<SketchConstraint> Constraints)
{
    public Guid Revision {get;init;}=Guid.NewGuid();
    public static CadSketch Create(DefinitionId part,string name,RigidTransform3d plane)=>new(SketchId.New(),part,name,plane,[],[],[],[]);

    public void Validate()
    {
        CadGuard.Id(Id);CadGuard.Id(PartId);CadGuard.Name(Name);Plane.Validate();
        if(Revision==Guid.Empty)throw new CadValidationException("Missing sketch revision.");
        if(Points.IsDefault||Lines.IsDefault||Circles.IsDefault||Constraints.IsDefault)throw new CadValidationException("Uninitialized sketch tables.");
        if(Points.Length+Lines.Length+Circles.Length>10000||Constraints.Length>10000)throw new CadValidationException("Sketch table limit exceeded.");
        var entities=new HashSet<SketchEntityId>();var constraints=new HashSet<SketchConstraintId>();
        var points=Points.ToDictionary(p=>p.Id);var lines=Lines.ToDictionary(l=>l.Id);var circles=Circles.ToDictionary(c=>c.Id);
        void Entity(SketchEntityId id){CadGuard.Id(id);if(!entities.Add(id))throw new CadValidationException("Duplicate sketch entity ID.");}
        void Point(SketchEntityId id){if(!points.ContainsKey(id))throw new CadValidationException("Missing sketch point.");}
        void Line(SketchEntityId id){if(!lines.ContainsKey(id))throw new CadValidationException("Constraint requires a line.");}
        void Circle(SketchEntityId id){if(!circles.ContainsKey(id))throw new CadValidationException("Constraint requires a circle.");}
        void Pair(SketchEntityId a,SketchEntityId b,Action<SketchEntityId> type)
        {type(a);type(b);if(a==b)throw new CadValidationException("A constraint requires distinct targets.");}
        foreach(var p in Points){Entity(p.Id);CadGuard.Finite(p.Position.X,p.Position.Y);}
        foreach(var l in Lines){Entity(l.Id);Pair(l.Start,l.End,Point);if(Distance(points[l.Start].Position,points[l.End].Position)<=1e-10)throw new CadValidationException("Degenerate sketch line.");}
        foreach(var c in Circles){Entity(c.Id);Point(c.Center);CadGuard.Positive(c.Radius);}
        foreach(var c in Constraints)
        {
            CadGuard.Id(c.Id);if(!constraints.Add(c.Id))throw new CadValidationException("Duplicate sketch constraint ID.");
            // Disabled constraints retain valid typed references so they can be enabled safely later.
            switch(c)
            {
                case FixPointConstraint f:Point(f.Point);CadGuard.Finite(f.Position.X,f.Position.Y);break;
                case CoincidentConstraint p:Pair(p.A,p.B,Point);break;
                case HorizontalConstraint h:Line(h.Line);break;
                case VerticalConstraint v:Line(v.Line);break;
                case DistanceConstraint d:Pair(d.A,d.B,Point);CadGuard.Positive(d.Distance);break;
                case OffsetXConstraint x:Pair(x.A,x.B,Point);CadGuard.Finite(x.Offset);break;
                case OffsetYConstraint y:Pair(y.A,y.B,Point);CadGuard.Finite(y.Offset);break;
                case LengthConstraint l:Line(l.Line);CadGuard.Positive(l.Length);break;
                case ParallelConstraint p:Pair(p.A,p.B,Line);break;
                case PerpendicularConstraint p:Pair(p.A,p.B,Line);break;
                case EqualLengthConstraint e:Pair(e.A,e.B,Line);break;
                case RadiusConstraint r:Circle(r.Circle);CadGuard.Positive(r.Radius);break;
                case EqualRadiusConstraint e:Pair(e.A,e.B,Circle);break;
                default:throw new CadValidationException("Unsupported sketch constraint.");
            }
        }
    }
    private static double Distance(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y);
}

/// <summary>Explicit frozen-profile bridge. Live sketch-driven feature dependencies belong to M4-S2.</summary>
public static class SketchProfileBuilder
{
    public static SketchProfile Polygon(CadSketch sketch,IEnumerable<SketchEntityId> orderedLines)
    {
        sketch.Validate();var ids=orderedLines.ToArray();
        if(ids.Length<3||ids.Distinct().Count()!=ids.Length)throw new CadValidationException("A polygon requires distinct boundary lines.");
        var lines=sketch.Lines.ToDictionary(l=>l.Id);var points=sketch.Points.ToDictionary(p=>p.Id);
        var boundary=ids.Select(id=>lines.TryGetValue(id,out var line)&&!line.IsConstruction?line:throw new CadValidationException("Missing or construction boundary line.")).ToArray();
        SketchProfile? Build(SketchEntityId first)
        {
            var vertices=ImmutableArray.CreateBuilder<Point2d>();var next=first;
            foreach(var line in boundary)
            {
                vertices.Add(points[next].Position);
                if(line.Start==next)next=line.End;else if(line.End==next)next=line.Start;else return null;
            }
            return next==first?new(vertices.ToImmutable()):null;
        }
        var profile=Build(boundary[0].Start)??Build(boundary[0].End)??throw new CadValidationException("Boundary lines do not form an ordered closed loop.");
        profile.Validate();return profile;
    }
}
