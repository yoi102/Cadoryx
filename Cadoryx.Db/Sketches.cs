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
public sealed record SketchArc(SketchEntityId Id,SketchEntityId Start,SketchEntityId Middle,SketchEntityId End,bool IsConstruction=false);
public sealed record SketchBezier(SketchEntityId Id,SketchEntityId Start,SketchEntityId Control,SketchEntityId End,bool IsConstruction=false);
public sealed record SketchSpline(SketchEntityId Id,ImmutableArray<SketchEntityId> Controls,bool IsConstruction=false);

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
public sealed record TangentConstraint(SketchConstraintId Id,SketchEntityId Line,SketchEntityId Circle,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record AngleConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,double AngleRadians,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);

/// <summary>Local coordinates are millimeters; Plane maps local XY into the owning part. Solver state is transient.</summary>
public sealed record CadSketch(SketchId Id,DefinitionId PartId,string Name,RigidTransform3d Plane,
    ImmutableArray<SketchPoint> Points,ImmutableArray<SketchLine> Lines,ImmutableArray<SketchCircle> Circles,
    ImmutableArray<SketchConstraint> Constraints)
{
    public ImmutableArray<SketchArc> Arcs {get;init;}=[];
    public ImmutableArray<SketchBezier> Beziers {get;init;}=[];
    public ImmutableArray<SketchSpline> Splines {get;init;}=[];
    public Guid Revision {get;init;}=Guid.NewGuid();
    public static CadSketch Create(DefinitionId part,string name,RigidTransform3d plane)=>new(SketchId.New(),part,name,plane,[],[],[],[]);

    public void Validate()
    {
        CadGuard.Id(Id);CadGuard.Id(PartId);CadGuard.Name(Name);Plane.Validate();
        if(Revision==Guid.Empty)throw new CadValidationException("Missing sketch revision.");
        if(Points.IsDefault||Lines.IsDefault||Circles.IsDefault||Arcs.IsDefault||Beziers.IsDefault||Splines.IsDefault||Constraints.IsDefault)throw new CadValidationException("Uninitialized sketch tables.");
        if(Points.Length+Lines.Length+Circles.Length+Arcs.Length+Beziers.Length+Splines.Length>10000||Constraints.Length>10000)throw new CadValidationException("Sketch table limit exceeded.");
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
        foreach(var arc in Arcs)
        {
            Entity(arc.Id);Point(arc.Start);Point(arc.Middle);Point(arc.End);
            _=SketchArcGeometry.Through(points[arc.Start].Position,points[arc.Middle].Position,points[arc.End].Position);
        }
        foreach(var bezier in Beziers)
        {
            Entity(bezier.Id);Point(bezier.Start);Point(bezier.Control);Point(bezier.End);
            if(bezier.Start==bezier.End)throw new CadValidationException("Bezier endpoints must differ.");
            SketchBezierGeometry.Validate(points[bezier.Start].Position,points[bezier.Control].Position,points[bezier.End].Position);
        }
        foreach(var spline in Splines)
        {
            Entity(spline.Id);
            if(spline.Controls.IsDefault||spline.Controls.Distinct().Count()!=spline.Controls.Length)
                throw new CadValidationException("Spline needs distinct control point identities.");
            foreach(var id in spline.Controls)Point(id);
            SketchSplineGeometry.Validate(spline.Controls.Select(id=>points[id].Position).ToImmutableArray());
        }
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
                case TangentConstraint t:Line(t.Line);Circle(t.Circle);break;
                case AngleConstraint a:Pair(a.A,a.B,Line);CadGuard.Finite(a.AngleRadians);
                    if(a.AngleRadians<=1e-6||a.AngleRadians>=Math.PI-1e-6)
                        throw new CadValidationException("Angle dimension must lie strictly between 0 and 180 degrees.");break;
                default:throw new CadValidationException("Unsupported sketch constraint.");
            }
        }
    }
    private static double Distance(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y);
}

/// <summary>Explicit frozen-profile bridge. Live sketch-driven feature dependencies belong to M4-S2.</summary>
public static class SketchProfileBuilder
{
    public static SketchProfile SplineSegment(CadSketch sketch,SketchEntityId splineId)
    {
        sketch.Validate();
        var spline=sketch.Splines.SingleOrDefault(s=>s.Id==splineId);
        if(spline is null||spline.IsConstruction)throw new CadValidationException("Missing or construction spline boundary.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var profile=new SketchProfile([]){Spline=new(spline.Controls.Select(id=>points[id]).ToImmutableArray())};
        profile.Validate();return profile;
    }
    public static SketchProfile BezierSegment(CadSketch sketch,SketchEntityId bezierId)
    {
        sketch.Validate();
        var bezier=sketch.Beziers.SingleOrDefault(b=>b.Id==bezierId);
        if(bezier is null||bezier.IsConstruction)throw new CadValidationException("Missing or construction Bezier boundary.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var profile=new SketchProfile([]){Bezier=new(points[bezier.Start],points[bezier.Control],points[bezier.End])};
        profile.Validate();return profile;
    }
    public static SketchProfile Mixed(CadSketch sketch,IEnumerable<SketchEntityId> orderedCurves)
    {
        sketch.Validate();var ids=orderedCurves.ToArray();
        if(ids.Length is <3 or >256||ids.Distinct().Count()!=ids.Length)
            throw new CadValidationException("A mixed profile requires distinct ordered curves.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var lines=sketch.Lines.Where(l=>!l.IsConstruction).ToDictionary(l=>l.Id);
        var arcs=sketch.Arcs.Where(a=>!a.IsConstruction).ToDictionary(a=>a.Id);
        if(!ids.Any(arcs.ContainsKey))throw new CadValidationException("A mixed profile requires a non-construction arc.");
        (SketchEntityId Start,SketchEntityId End,SketchEntityId? Middle) Edge(SketchEntityId id)=>
            lines.TryGetValue(id,out var line)?(line.Start,line.End,null):
            arcs.TryGetValue(id,out var arc)?(arc.Start,arc.End,arc.Middle):
            throw new CadValidationException("Mixed profile references missing or construction geometry.");
        SketchProfile? Build(SketchEntityId first)
        {
            var curves=ImmutableArray.CreateBuilder<SketchBoundaryCurve>();var at=first;
            foreach(var id in ids)
            {
                var edge=Edge(id);
                if(edge.Start!=at&&edge.End!=at)return null;
                bool forward=edge.Start==at;
                var end=forward?edge.End:edge.Start;
                curves.Add(new(points[at],points[end],edge.Middle is {} middle?points[middle]:null));
                at=end;
            }
            return at==first?new SketchProfile([]){BoundaryCurves=curves.ToImmutable()}:null;
        }
        var seed=Edge(ids[0]);
        var profile=Build(seed.Start)??Build(seed.End)??throw new CadValidationException("Mixed curves do not form an ordered closed loop.");
        profile.Validate();return profile;
    }
    public static SketchProfile ArcSegment(CadSketch sketch,SketchEntityId arcId)
    {
        sketch.Validate();
        var arc=sketch.Arcs.SingleOrDefault(a=>a.Id==arcId);
        if(arc is null||arc.IsConstruction)throw new CadValidationException("Missing or construction boundary arc.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var region=new ThreePointArcRegion(points[arc.Start],points[arc.Middle],points[arc.End]);
        var profile=new SketchProfile([]){Arc=region};profile.Validate();return profile;
    }
    public static SketchProfile Circle(CadSketch sketch,SketchEntityId circleId)
    {
        sketch.Validate();
        var circle=sketch.Circles.SingleOrDefault(c=>c.Id==circleId);
        if(circle is null||circle.IsConstruction)throw new CadValidationException("Missing or construction boundary circle.");
        var center=sketch.Points.Single(p=>p.Id==circle.Center).Position;
        var profile=SketchProfile.FromCircle(center,circle.Radius);profile.Validate();return profile;
    }
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
