using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cadoryx.Db;

public enum BooleanOperation { Fuse=0, Cut=1, Common=2 }
[JsonPolymorphic(TypeDiscriminatorPropertyName="type")]
[JsonDerivedType(typeof(BoxRecipe),"box")]
[JsonDerivedType(typeof(CylinderRecipe),"cylinder")]
[JsonDerivedType(typeof(BooleanRecipe),"boolean")]
[JsonDerivedType(typeof(ImportedRecipe),"import")]
[JsonDerivedType(typeof(TransformRecipe),"transform")]
[JsonDerivedType(typeof(ExtrudeRecipe),"extrude")]
[JsonDerivedType(typeof(RevolveRecipe),"revolve")]
[JsonDerivedType(typeof(LocalFeatureRecipe),"local-box-edge")]
[JsonDerivedType(typeof(HistoryFilletRecipe),"history-edge-fillet")]
[JsonDerivedType(typeof(HistoryChamferRecipe),"history-edge-chamfer")]
public abstract record GeometryRecipe
{
    public abstract void Validate();
    [JsonIgnore] public virtual IEnumerable<GeometryAssetRef> AssetInputs => [];
}
public sealed record BoxRecipe(double X,double Y,double Z,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate() { CadGuard.Positive(X,Y,Z); Placement.Validate(); }
}
public sealed record CylinderRecipe(double Radius,double Height,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate() { CadGuard.Positive(Radius,Height); Placement.Validate(); }
}
public sealed record ImportedRecipe(GeometryAssetRef Source) : GeometryRecipe
{
    public override void Validate() => Source.Validate();
    public override IEnumerable<GeometryAssetRef> AssetInputs => [Source];
}
public sealed record TransformRecipe(GeometryAssetRef Source,RigidTransform3d Transform) : GeometryRecipe
{
    public override void Validate() { Source.Validate(); Transform.Validate(); }
    public override IEnumerable<GeometryAssetRef> AssetInputs => [Source];
}
public sealed record BooleanRecipe(BooleanOperation Operation,ImmutableArray<GeometryAssetRef> Inputs) : GeometryRecipe
{
    public override void Validate() { if(!Enum.IsDefined(Operation) || Inputs.IsDefault || Inputs.Length<2) throw new CadValidationException("Boolean requires two or more inputs."); foreach(var i in Inputs) i.Validate(); }
    public override IEnumerable<GeometryAssetRef> AssetInputs => Inputs;
}
public readonly record struct Point2d(double X,double Y);
public sealed record CircularSketchRegion(Point2d Center,double Radius);
public sealed record ThreePointArcRegion(Point2d Start,Point2d Middle,Point2d End);
public sealed record QuadraticBezierRegion(Point2d Start,Point2d Control,Point2d End);
public sealed record CubicSplineRegion(ImmutableArray<Point2d> Controls);
public sealed record SketchIslandRegion(ImmutableArray<Point2d> Points,CircularSketchRegion? Circle=null,
    ImmutableArray<SketchBoundaryCurve> BoundaryCurves=default)
{
    public ImmutableArray<SketchBoundaryCurve> BoundaryCurves {get;init;}=BoundaryCurves.IsDefault?[]:BoundaryCurves;
    public CubicSplineRegion? Spline {get;init;}
    public ImmutableArray<CircularSketchRegion> Holes {get;init;}=[];
    public ImmutableArray<ImmutableArray<Point2d>> PolygonHoles {get;init;}=[];
    public ImmutableArray<ImmutableArray<SketchBoundaryCurve>> MixedHoles {get;init;}=[];
    public ImmutableArray<CubicSplineRegion> SplineHoles {get;init;}=[];
    public ImmutableArray<SketchIslandRegion> Islands {get;init;}=[];
    public SketchProfile Profile=>new(Points,Circle,Holes,PolygonHoles)
        {BoundaryCurves=BoundaryCurves,Spline=Spline,MixedHoles=MixedHoles,SplineHoles=SplineHoles,Islands=Islands};
}
public sealed record SketchProfile(ImmutableArray<Point2d> Points,CircularSketchRegion? Circle=null,
    ImmutableArray<CircularSketchRegion> Holes=default,ImmutableArray<ImmutableArray<Point2d>> PolygonHoles=default)
{
    public ThreePointArcRegion? Arc {get;init;}
    public QuadraticBezierRegion? Bezier {get;init;}
    public CubicSplineRegion? Spline {get;init;}
    public ImmutableArray<SketchBoundaryCurve> BoundaryCurves {get;init;}=[];
    public ImmutableArray<ImmutableArray<SketchBoundaryCurve>> MixedHoles {get;init;}=[];
    public ImmutableArray<CubicSplineRegion> SplineHoles {get;init;}=[];
    public ImmutableArray<SketchIslandRegion> Islands {get;init;}=[];
    public ImmutableArray<CircularSketchRegion> Holes {get;init;}=Holes.IsDefault?[]:Holes;
    public ImmutableArray<ImmutableArray<Point2d>> PolygonHoles {get;init;}=PolygonHoles.IsDefault?[]:PolygonHoles;
    public static SketchProfile FromCircle(Point2d center,double radius)=>new([],new(center,radius));
    public void Validate()
    {
        if(BoundaryCurves.IsDefault||MixedHoles.IsDefault||SplineHoles.IsDefault||Islands.IsDefault)throw new CadValidationException("Uninitialized profile boundary, holes or islands.");
        if(!BoundaryCurves.IsEmpty)
        {
            if(Circle is not null||Arc is not null||Bezier is not null||Spline is not null||Points.IsDefault||!Points.IsEmpty)
                throw new CadValidationException("A mixed profile cannot also contain another outer boundary.");
            SketchMixedProfile.Validate(BoundaryCurves);
            ValidateCurveHoles(BoundaryCurves);
            ValidateIslands();
            return;
        }
        if(Arc is {} arc)
        {
            if(Circle is not null||Bezier is not null||Spline is not null||Points.IsDefault||!Points.IsEmpty||!Holes.IsEmpty||!PolygonHoles.IsEmpty||!MixedHoles.IsEmpty||!SplineHoles.IsEmpty||!Islands.IsEmpty)
                throw new CadValidationException("An arc segment profile cannot have polygon vertices or holes.");
            _=SketchArcGeometry.Through(arc.Start,arc.Middle,arc.End);
            return;
        }
        if(Bezier is {} bezier)
        {
            if(Circle is not null||Spline is not null||Points.IsDefault||!Points.IsEmpty||!Holes.IsEmpty||!PolygonHoles.IsEmpty||!MixedHoles.IsEmpty||!SplineHoles.IsEmpty||!Islands.IsEmpty)
                throw new CadValidationException("A Bezier segment profile cannot have another boundary or holes.");
            SketchBezierGeometry.Validate(bezier.Start,bezier.Control,bezier.End);
            return;
        }
        if(Spline is {} spline)
        {
            if(Circle is not null||Points.IsDefault||!Points.IsEmpty)
                throw new CadValidationException("A spline segment profile cannot have another boundary or holes.");
            SketchSplineGeometry.Validate(spline.Controls);
            ValidateCurveHoles(SketchSplineGeometry.ValidationBoundary(spline));ValidateIslands();return;
        }
        if(Circle is {} circle)
        {
            if(Points.IsDefault||!Points.IsEmpty)throw new CadValidationException("A circular profile cannot contain polygon vertices.");
            CadGuard.Finite(circle.Center.X,circle.Center.Y);CadGuard.Positive(circle.Radius);
        }
        else ValidatePolygon();
        if(Holes.Length+PolygonHoles.Length+MixedHoles.Length+SplineHoles.Length>64)throw new CadValidationException("A profile has too many holes.");
        const double clearance=1e-7;
        for(int i=0;i<Holes.Length;i++)
        {
            var hole=Holes[i];CadGuard.Finite(hole.Center.X,hole.Center.Y);CadGuard.Positive(hole.Radius);
            if(Circle is {} outer)
            {
                if(double.Hypot(hole.Center.X-outer.Center.X,hole.Center.Y-outer.Center.Y)+hole.Radius>=outer.Radius-clearance)
                    throw new CadValidationException("Circular hole must be strictly inside the outer circle.");
            }
            else
            {
                if(!InsidePolygon(hole.Center)||Enumerable.Range(0,Points.Length).Any(j=>
                    SegmentDistance(hole.Center,Points[j],Points[(j+1)%Points.Length])<=hole.Radius+clearance))
                    throw new CadValidationException("Circular hole must be strictly inside the polygon.");
            }
            for(int j=0;j<i;j++)if(double.Hypot(hole.Center.X-Holes[j].Center.X,hole.Center.Y-Holes[j].Center.Y)<=
                hole.Radius+Holes[j].Radius+clearance)throw new CadValidationException("Circular holes touch or overlap.");
        }
        for(int i=0;i<PolygonHoles.Length;i++)
        {
            var vertices=PolygonHoles[i];
            (new SketchProfile(vertices)).ValidatePolygon();
            foreach(var p in vertices)
            {
                if(Circle is {} outer)
                {
                    if(double.Hypot(p.X-outer.Center.X,p.Y-outer.Center.Y)>=outer.Radius-clearance)
                        throw new CadValidationException("Polygon hole must be strictly inside the outer circle.");
                }
                else if(!InsidePolygon(p,Points)||BoundaryDistance(p,Points)<=clearance)
                    throw new CadValidationException("Polygon hole must be strictly inside the outer polygon.");
            }
            if(Circle is null&&BoundariesIntersect(vertices,Points))
                throw new CadValidationException("Polygon hole touches the outer boundary.");
            foreach(var circularHole in Holes)
                if(InsidePolygon(circularHole.Center,vertices)||BoundaryDistance(circularHole.Center,vertices)<=circularHole.Radius+clearance)
                    throw new CadValidationException("Polygon and circular holes touch or overlap.");
            for(int j=0;j<i;j++)
            {
                var other=PolygonHoles[j];
                if(InsidePolygon(vertices[0],other)||InsidePolygon(other[0],vertices)||BoundariesIntersect(vertices,other)||
                   BoundaryDistance(vertices[0],other)<=clearance||BoundaryDistance(other[0],vertices)<=clearance)
                    throw new CadValidationException("Polygon holes touch or overlap.");
            }
        }
        if(!MixedHoles.IsEmpty||!SplineHoles.IsEmpty)
            ValidateCurveHoles(Circle is {} outerCircle?SketchMixedProfile.Circle(outerCircle):SketchMixedProfile.Polygon(Points));
        ValidateIslands();
    }
    private void ValidateCurveHoles(ImmutableArray<SketchBoundaryCurve> outer)
    {
        if(Holes.Length+PolygonHoles.Length+MixedHoles.Length+SplineHoles.Length>64)
            throw new CadValidationException("A profile has too many holes.");
        foreach(var hole in MixedHoles)SketchMixedProfile.Validate(hole);
        foreach(var hole in SplineHoles)SketchSplineGeometry.Validate(hole.Controls);
        foreach(var hole in PolygonHoles)(new SketchProfile(hole)).Validate();
        foreach(var hole in Holes){CadGuard.Finite(hole.Center.X,hole.Center.Y);CadGuard.Positive(hole.Radius);}
        var boundaries=Holes.Select(SketchMixedProfile.Circle)
            .Concat(PolygonHoles.Select(SketchMixedProfile.Polygon)).Concat(MixedHoles)
            .Concat(SplineHoles.Select(SketchSplineGeometry.ValidationBoundary)).ToImmutableArray();
        SketchMixedProfile.ValidateHoles(outer,boundaries);
    }
    private void ValidateIslands()
    {
        if(Islands.Length>64)throw new CadValidationException("A profile has too many islands.");
        foreach(var island in Islands)island.Profile.Validate();
        var holes=Holes.Select(SketchMixedProfile.Circle)
            .Concat(PolygonHoles.Select(SketchMixedProfile.Polygon)).Concat(MixedHoles)
            .Concat(SplineHoles.Select(SketchSplineGeometry.ValidationBoundary)).ToImmutableArray();
        var islands=Islands.Select(island=>island.Circle is {} circle?SketchMixedProfile.Circle(circle):
            island.Spline is {} spline?SketchSplineGeometry.ValidationBoundary(spline):
            !island.BoundaryCurves.IsDefaultOrEmpty?island.BoundaryCurves:SketchMixedProfile.Polygon(island.Points)).ToImmutableArray();
        SketchMixedProfile.ValidateIslands(holes,islands);
    }
    private void ValidatePolygon()
    {
        if(Points.IsDefault || Points.Length<3) throw new CadValidationException("A profile requires at least three vertices.");
        foreach(var p in Points) CadGuard.Finite(p.X,p.Y);
        double area=0;
        for(int i=0;i<Points.Length;i++)
        {
            var a=Points[i]; var b=Points[(i+1)%Points.Length];
            if(Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y)<1e-10) throw new CadValidationException("Profile contains a zero-length edge.");
            area+=a.X*b.Y-b.X*a.Y;
            for(int j=i+2;j<Points.Length;j++)
            {
                if(i==0 && j==Points.Length-1) continue;
                if(Intersects(a,b,Points[j],Points[(j+1)%Points.Length])) throw new CadValidationException("Profile self-intersects or touches itself.");
            }
        }
        if(Math.Abs(area)<1e-10) throw new CadValidationException("Profile area is zero.");
    }
    private bool InsidePolygon(Point2d p)=>InsidePolygon(p,Points);
    private static bool InsidePolygon(Point2d p,ImmutableArray<Point2d> polygon)
    {
        bool inside=false;
        for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
        {
            var a=polygon[i];var b=polygon[j];
            if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
        }
        return inside;
    }
    private static double BoundaryDistance(Point2d p,ImmutableArray<Point2d> polygon)=>
        Enumerable.Range(0,polygon.Length).Min(i=>SegmentDistance(p,polygon[i],polygon[(i+1)%polygon.Length]));
    private static bool BoundariesIntersect(ImmutableArray<Point2d> a,ImmutableArray<Point2d> b)=>
        Enumerable.Range(0,a.Length).Any(i=>Enumerable.Range(0,b.Length).Any(j=>
            Intersects(a[i],a[(i+1)%a.Length],b[j],b[(j+1)%b.Length])));
    private static double SegmentDistance(Point2d p,Point2d a,Point2d b)
    {
        double dx=b.X-a.X,dy=b.Y-a.Y;
        double t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/(dx*dx+dy*dy),0,1);
        return double.Hypot(p.X-a.X-t*dx,p.Y-a.Y-t*dy);
    }
    private static bool Intersects(Point2d a,Point2d b,Point2d c,Point2d d)
    {
        static double Cross(Point2d p,Point2d q,Point2d r)=>(q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
        static bool On(Point2d p,Point2d q,Point2d r)=>r.X>=Math.Min(p.X,q.X)-1e-10&&r.X<=Math.Max(p.X,q.X)+1e-10&&r.Y>=Math.Min(p.Y,q.Y)-1e-10&&r.Y<=Math.Max(p.Y,q.Y)+1e-10;
        var x=Cross(a,b,c);var y=Cross(a,b,d);var z=Cross(c,d,a);var w=Cross(c,d,b);
        return (x*y<0&&z*w<0)||(Math.Abs(x)<1e-10&&On(a,b,c))||(Math.Abs(y)<1e-10&&On(a,b,d))||(Math.Abs(z)<1e-10&&On(c,d,a))||(Math.Abs(w)<1e-10&&On(c,d,b));
    }
}
public sealed record ExtrudeRecipe(SketchProfile Profile,double Distance,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate(){Profile.Validate();CadGuard.Positive(Distance);Placement.Validate();}
}
public sealed record RevolveRecipe(SketchProfile Profile,double AngleRadians,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate(){Profile.Validate();if(Profile.Circle is not null||Profile.Arc is not null||Profile.Bezier is not null||Profile.Spline is not null||!Profile.BoundaryCurves.IsEmpty||!Profile.Holes.IsDefaultOrEmpty||!Profile.PolygonHoles.IsDefaultOrEmpty||!Profile.MixedHoles.IsDefaultOrEmpty||!Profile.Islands.IsDefaultOrEmpty)throw new CadValidationException("Circular, curved and holed sketch regions are not supported by revolve.");CadGuard.Positive(AngleRadians);if(AngleRadians>Math.PI*2+1e-10)throw new CadValidationException("Revolution exceeds a full turn.");Placement.Validate();}
}
public sealed record FeatureDefinition(FeatureId Id,DefinitionId PartId,string Name,GeometryRecipe Recipe,
    ImmutableArray<FeatureId> Inputs,BodyId OutputBodyId,GeometryAssetRef Result,int SchemaVersion=1,BodyOutputMetadata? OutputMetadata=null)
{
    public SketchProfileReference? SketchSource {get;init;}
    public TopologyHistory? TopologyHistory {get;init;}
    public FeatureTopologyBinding? TopologyBinding {get;init;}
    public bool IsStale {get;init;}
    /// <summary>Explicit feature-tree suppression; dependent results are blocked until recomputed.</summary>
    public bool IsSuppressed {get;init;}
}
/// <summary>Retains the output's authored attributes when recompute temporarily produces no body.</summary>
public sealed record BodyOutputMetadata(string Name,LayerId Layer,CadAppearance Appearance,bool Visible,MaterialId? Material)
{
    public static BodyOutputMetadata FromBody(CadBody body)=>new(body.Name,body.LayerId,body.Appearance,body.IsVisible,body.MaterialId);
}
