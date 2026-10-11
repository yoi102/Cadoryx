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
public enum SketchPointOrigin { Unknown=0,Independent=1,CurveGenerated=2 }
public sealed record SketchPoint(SketchEntityId Id,Point2d Position,bool IsConstruction=false)
{ public SketchPointOrigin Origin {get;init;} }
public sealed record SketchLine(SketchEntityId Id,SketchEntityId Start,SketchEntityId End,bool IsConstruction=false);
public sealed record SketchCircle(SketchEntityId Id,SketchEntityId Center,double Radius,bool IsConstruction=false);
/// <summary>An exact copied conic. Its numeric definition is fixed until explicitly edited or detached.</summary>
public sealed record SketchEllipse(SketchEntityId Id,SketchEllipseGeometry Geometry,bool IsConstruction=false)
{ public SketchCurveEndpoints? Endpoints {get;init;} }
public sealed record SketchFreeform(SketchEntityId Id,SketchFreeformGeometry Geometry,bool IsConstruction=false)
{ public SketchCurveEndpoints? Endpoints {get;init;} }
public sealed record SketchArc(SketchEntityId Id,SketchEntityId Start,SketchEntityId Middle,SketchEntityId End,bool IsConstruction=false);
public sealed record SketchBezier(SketchEntityId Id,SketchEntityId Start,SketchEntityId Control,SketchEntityId End,bool IsConstruction=false);
public sealed record SketchSpline(SketchEntityId Id,ImmutableArray<SketchEntityId> Controls,bool IsConstruction=false)
{
    public CubicSplineKnotVector? Knots {get;init;}
}

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
public sealed record MidpointConstraint(SketchConstraintId Id,SketchEntityId Point,SketchEntityId Line,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record SymmetryConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,SketchEntityId Axis,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public enum EndpointTangentBranch { Opposed=1 }
public sealed record EndpointTangentConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,SketchEntityId Joint,
    EndpointTangentBranch Branch=EndpointTangentBranch.Opposed,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public sealed record EndpointCurvatureConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,SketchEntityId Joint,
    EndpointTangentBranch Branch=EndpointTangentBranch.Opposed,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
/// <summary>Side is relative to the directed line. Containment is relative to authored A and B.</summary>
public enum CircularContactBranch { LineLeft=1,LineRight=2,External=3,AContainsB=4,BContainsA=5 }
public sealed record CircularContactConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,
    CircularContactBranch Branch,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled);
public enum FiniteContactTangentBranch { SameDirection=1,OppositeDirection=2 }
public enum FiniteContactContinuity { Tangent=1,Curvature=2 }
public enum FiniteContactParameterMode { Fixed=1,SolveA=2,SolveB=3,SolveBoth=4 }
public sealed record SlidingContactParameter(double Seed,double Lower,double Upper)
{
    public void Validate(double actual)
    {
        try{CadGuard.Finite(Seed,Lower,Upper,actual);}
        catch(CadValidationException error){error.Data["SketchContact.Message"]="SlidingContactInvalidInterval";throw;}
        if(Lower<0||Upper>1||Lower>=Upper||Seed<=Lower||Seed>=Upper||Seed<=0||Seed>=1||
           actual<=Lower||actual>=Upper||actual<=0||actual>=1)
        {
            var error=new CadValidationException("Contact seed and actual parameter must be strictly inside the explicit allowed interval.");
            error.Data["SketchContact.Message"]="SlidingContactInvalidInterval";throw error;
        }
    }
}
public sealed record FiniteParametricContactConstraint(SketchConstraintId Id,SketchEntityId A,SketchEntityId B,
    double ParameterA,double ParameterB,FiniteContactTangentBranch Branch,bool IsEnabled=true) : SketchConstraint(Id,IsEnabled)
{
    public FiniteContactContinuity Continuity {get;init;}=FiniteContactContinuity.Tangent;
    public FiniteContactParameterMode ParameterMode {get;init;}=FiniteContactParameterMode.Fixed;
    public SlidingContactParameter? SlidingA {get;init;}
    public SlidingContactParameter? SlidingB {get;init;}
    public bool SolvesA=>ParameterMode is FiniteContactParameterMode.SolveA or FiniteContactParameterMode.SolveBoth;
    public bool SolvesB=>ParameterMode is FiniteContactParameterMode.SolveB or FiniteContactParameterMode.SolveBoth;
    public int ParameterVariableCount=>(SolvesA?1:0)+(SolvesB?1:0);
}

/// <summary>Local coordinates are millimeters; Plane maps local XY into the owning part. Solver state is transient.</summary>
public sealed record CadSketch(SketchId Id,DefinitionId PartId,string Name,RigidTransform3d Plane,
    ImmutableArray<SketchPoint> Points,ImmutableArray<SketchLine> Lines,ImmutableArray<SketchCircle> Circles,
    ImmutableArray<SketchConstraint> Constraints)
{
    public ImmutableArray<SketchArc> Arcs {get;init;}=[];
    public ImmutableArray<SketchBezier> Beziers {get;init;}=[];
    public ImmutableArray<SketchSpline> Splines {get;init;}=[];
    public ImmutableArray<SketchProjection> Projections {get;init;}=[];
    public ImmutableArray<SketchEllipse> Ellipses {get;init;}=[];
    public ImmutableArray<SketchFreeform> Freeforms {get;init;}=[];
    public ImmutableArray<SketchExpressionCurve> ExpressionCurves {get;init;}=[];
    public ImmutableArray<SketchRegionOffsetAuthor> RegionOffsets {get;init;}=[];
    public ImmutableArray<SketchNativeProjection> NativeProjections {get;init;}=[];
    public Guid Revision {get;init;}=Guid.NewGuid();
    public static CadSketch Create(DefinitionId part,string name,RigidTransform3d plane)=>new(SketchId.New(),part,name,plane,[],[],[],[]);

    public void Validate()
    {
        CadGuard.Id(Id);CadGuard.Id(PartId);CadGuard.Name(Name);Plane.Validate();
        if(Revision==Guid.Empty)throw new CadValidationException("Missing sketch revision.");
        if(Points.IsDefault||Lines.IsDefault||Circles.IsDefault||Arcs.IsDefault||Beziers.IsDefault||Splines.IsDefault||Ellipses.IsDefault||Freeforms.IsDefault||ExpressionCurves.IsDefault||Constraints.IsDefault)throw new CadValidationException("Uninitialized sketch tables.");
        if(Points.Length+Lines.Length+Circles.Length+Arcs.Length+Beziers.Length+Splines.Length+Ellipses.Length+Freeforms.Length+ExpressionCurves.Length>10000||Constraints.Length>10000)throw new CadValidationException("Sketch table limit exceeded.");
        var entities=new HashSet<SketchEntityId>();var constraints=new HashSet<SketchConstraintId>();
        var points=Points.ToDictionary(p=>p.Id);var lines=Lines.ToDictionary(l=>l.Id);var circles=Circles.ToDictionary(c=>c.Id);
        void Entity(SketchEntityId id){CadGuard.Id(id);if(!entities.Add(id))throw new CadValidationException("Duplicate sketch entity ID.");}
        void Point(SketchEntityId id){if(!points.ContainsKey(id))throw new CadValidationException("Missing sketch point.");}
        void Line(SketchEntityId id){if(!lines.ContainsKey(id))throw new CadValidationException("Constraint requires a line.");}
        void Circle(SketchEntityId id){if(!circles.ContainsKey(id))throw new CadValidationException("Constraint requires a circle.");}
        void Circular(SketchEntityId id){if(!circles.ContainsKey(id)&&!Arcs.Any(a=>a.Id==id))throw new CadValidationException("Radius constraint requires a circle or arc.");}
        void Pair(SketchEntityId a,SketchEntityId b,Action<SketchEntityId> type)
        {type(a);type(b);if(a==b)throw new CadValidationException("A constraint requires distinct targets.");}
        foreach(var p in Points){Entity(p.Id);CadGuard.Finite(p.Position.X,p.Position.Y);if(!Enum.IsDefined(p.Origin))throw new CadValidationException("Unknown sketch point origin.");}
        foreach(var l in Lines){Entity(l.Id);Pair(l.Start,l.End,Point);if(Distance(points[l.Start].Position,points[l.End].Position)<=1e-10)throw new CadValidationException("Degenerate sketch line.");}
        foreach(var c in Circles){Entity(c.Id);Point(c.Center);CadGuard.Positive(c.Radius);}
        foreach(var e in Ellipses){Entity(e.Id);e.Geometry.Validate();}
        foreach(var e in Freeforms){Entity(e.Id);e.Geometry.Validate();}
        foreach(var e in ExpressionCurves)Entity(e.Id);
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
            SketchSplineGeometry.Validate(spline.Controls.Select(id=>points[id].Position).ToImmutableArray(),spline.Knots);
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
                case RadiusConstraint r:Circular(r.Circle);CadGuard.Positive(r.Radius);break;
                case EqualRadiusConstraint e:Pair(e.A,e.B,Circular);break;
                case TangentConstraint t:Line(t.Line);Circle(t.Circle);break;
                case CircularContactConstraint t:SketchCircularContact.ValidateTargets(this,t);break;
                case EndpointCurvatureConstraint t:SketchEndpointCurvature.ValidateTargets(this,t);break;
                case FiniteParametricContactConstraint t:SketchFiniteParametricContact.ValidateTargets(this,t);break;
                case EndpointTangentConstraint t:
                    if(t.Branch!=EndpointTangentBranch.Opposed||SketchEndpointTangency.CommonEndpoint(this,t.A,t.B)!=t.Joint)
                        throw new CadValidationException("Endpoint tangency requires the unique shared endpoint and the smooth branch.");
                    SketchEndpointTangency.ValidateParametricDerivative(this,t.A,t.Joint);SketchEndpointTangency.ValidateParametricDerivative(this,t.B,t.Joint);break;
                case MidpointConstraint m:Point(m.Point);Line(m.Line);
                    if(m.Point==lines[m.Line].Start||m.Point==lines[m.Line].End)throw new CadValidationException("Midpoint target must differ from the line endpoints.");break;
                case SymmetryConstraint s:Pair(s.A,s.B,Point);Line(s.Axis);
                    if(new[]{s.A,s.B}.Any(p=>p==lines[s.Axis].Start||p==lines[s.Axis].End))throw new CadValidationException("Symmetric points must differ from the axis endpoints.");break;
                case AngleConstraint a:Pair(a.A,a.B,Line);CadGuard.Finite(a.AngleRadians);
                    if(a.AngleRadians<=1e-6||a.AngleRadians>=Math.PI-1e-6)
                        throw new CadValidationException("Angle dimension must lie strictly between 0 and 180 degrees.");break;
                default:throw new CadValidationException("Unsupported sketch constraint.");
            }
        }
        SketchProjectionGeometry.ValidateLocal(this);
        SketchNativeProjectionGeometry.ValidateLocal(this);
        SketchFixedCurveEndpoints.Validate(this);
        SketchExpressionEntities.ValidateLocal(this);
        SketchRegionAuthorModel.ValidateLocal(this);
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
        var profile=new SketchProfile([]){Spline=new(spline.Controls.Select(id=>points[id]).ToImmutableArray()){Knots=spline.Knots}};
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
        foreach(var id in ids.Where(id=>sketch.ExpressionCurves.Any(c=>c.Id==id)))SketchAssociativeEndpoints.RequireSolved(sketch,curve:id);
        if(ids.Length==1&&sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==ids[0]) is {} expression)
        {
            if(expression.IsConstruction)throw new CadValidationException("Select a non-construction expression boundary.");
            var e=expression.Geometry;var edge=new SketchBoundaryCurve(e.At(0),e.At(1)){Expression=e};
            var expressionProfile=new SketchProfile([]){BoundaryCurves=double.Hypot(edge.Start.X-edge.End.X,edge.Start.Y-edge.End.Y)<=1e-7?
                [edge]:[edge,new(edge.End,edge.Start)]};expressionProfile.Validate();return expressionProfile;
        }
        if(ids.Length==1&&sketch.Freeforms.SingleOrDefault(e=>e.Id==ids[0]) is {} freeform)
        {
            if(freeform.IsConstruction)throw new CadValidationException("Select a non-construction freeform boundary.");
            var g=freeform.Geometry;var edge=new SketchBoundaryCurve(g.At(0),g.At(1)){Freeform=g};
            var freeformProfile=new SketchProfile([]){BoundaryCurves=double.Hypot(edge.Start.X-edge.End.X,edge.Start.Y-edge.End.Y)<=1e-7?
                [edge]:[edge,new(edge.End,edge.Start)]};freeformProfile.Validate();return freeformProfile;
        }
        if(ids.Length==1&&sketch.Ellipses.SingleOrDefault(e=>e.Id==ids[0]) is {} ellipse)
        {
            if(ellipse.IsConstruction)throw new CadValidationException("Select a non-construction ellipse boundary.");
            var e=ellipse.Geometry;var edge=new SketchBoundaryCurve(e.At(0),e.At(1)){Ellipse=e};
            var conicProfile=new SketchProfile([]){BoundaryCurves=Math.Abs(Math.Abs(e.SweepAngle)-Math.Tau)<=1e-10?[edge]:
                [edge,new(edge.End,edge.Start)]};conicProfile.Validate();return conicProfile;
        }
        if(ids.Length is <2 or >256||ids.Distinct().Count()!=ids.Length)
            throw new CadValidationException("A mixed profile requires distinct ordered curves.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var lines=sketch.Lines.Where(l=>!l.IsConstruction).ToDictionary(l=>l.Id);
        var arcs=sketch.Arcs.Where(a=>!a.IsConstruction).ToDictionary(a=>a.Id);
        var beziers=sketch.Beziers.Where(b=>!b.IsConstruction).ToDictionary(b=>b.Id);
        var splines=sketch.Splines.Where(s=>!s.IsConstruction).ToDictionary(s=>s.Id);
        var ellipses=sketch.Ellipses.Where(e=>!e.IsConstruction&&e.Endpoints is not null).ToDictionary(e=>e.Id);
        var freeforms=sketch.Freeforms.Where(e=>!e.IsConstruction&&e.Endpoints is not null).ToDictionary(e=>e.Id);
        var expressions=sketch.ExpressionCurves.Where(e=>!e.IsConstruction&&e.Endpoints is not null).ToDictionary(e=>e.Id);
        if(!ids.Any(id=>arcs.ContainsKey(id)||beziers.ContainsKey(id)||splines.ContainsKey(id)||ellipses.ContainsKey(id)||freeforms.ContainsKey(id)||expressions.ContainsKey(id)))
            throw new CadValidationException("A mixed profile requires a non-construction curved edge.");
        (SketchEntityId Start,SketchEntityId End,SketchEntityId? Middle,SketchEntityId? Control,ImmutableArray<SketchEntityId> Spline) Edge(SketchEntityId id)=>
            lines.TryGetValue(id,out var line)?(line.Start,line.End,null,null,[]):
            arcs.TryGetValue(id,out var arc)?(arc.Start,arc.End,arc.Middle,null,[]):
            beziers.TryGetValue(id,out var bezier)?(bezier.Start,bezier.End,null,bezier.Control,[]):
            splines.TryGetValue(id,out var spline)?(spline.Controls[0],spline.Controls[^1],null,null,spline.Controls):
            ellipses.TryGetValue(id,out var conic)?(conic.Endpoints!.Start,conic.Endpoints.End,null,null,[]):
            freeforms.TryGetValue(id,out var copied)?(copied.Endpoints!.Start,copied.Endpoints.End,null,null,[]):
            expressions.TryGetValue(id,out var derived)?(derived.Endpoints!.Start,derived.Endpoints.End,null,null,[]):
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
                var controls=edge.Spline.IsEmpty?ImmutableArray<Point2d>.Empty:
                    (forward?edge.Spline:edge.Spline.Reverse().ToImmutableArray()).Select(control=>points[control]).ToImmutableArray();
                curves.Add(new(points[at],points[end],edge.Middle is {} middle?points[middle]:null)
                    {BezierControl=edge.Control is {} control?points[control]:null,SplineControls=controls,
                        SplineKnots=splines.TryGetValue(id,out var authored)?(forward?authored.Knots:authored.Knots?.Reverse()):null,
                        Ellipse=ellipses.TryGetValue(id,out var conic)?(forward?conic.Geometry:conic.Geometry.Reversed()):null,
                        Freeform=freeforms.TryGetValue(id,out var copied)?(forward?copied.Geometry:copied.Geometry.Reverse()):null,
                        Expression=expressions.TryGetValue(id,out var derived)?(forward?derived.Geometry:derived.Geometry.Reverse()):null});
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
