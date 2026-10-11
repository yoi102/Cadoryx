using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum ExactDatumKind { Plane=0, Cylinder=1, Cone=2, Circle=3 }
/// <summary>Copied analytic values on a confirmed original asset. They never identify topology by geometry.</summary>
public sealed record ExactDatumGeometry(ExactTopologySelection Selection,GeometryAssetRef Source,
    ExactDatumKind Kind,Vector3d Point,Vector3d Direction,double Radius=0)
{
    public void Validate()
    {
        Selection.Validate();Source.Validate();DatumConstructions.Coordinate(Point);Direction.Validate();CadGuard.Finite(Radius);
        if(!Enum.IsDefined(Kind)||Math.Abs(Direction.Length-1)>1e-10||Radius<0||Radius>1_000_000||
            (Kind==ExactDatumKind.Plane?Radius!=0:Radius<=1e-7)||Selection.Asset!=Source.AssetId||Selection.Revision!=Source.Revision||
            Selection.Kind!=(Kind==ExactDatumKind.Circle?HistoryShapeKind.Edge:HistoryShapeKind.Face))
            throw new CadValidationException("Invalid exact analytic datum evidence.");
    }
    public bool IsCurrent(DocumentSnapshot doc,DefinitionId owner)
    {
        Validate();
        if(Selection.DocumentId!=doc.Id||!doc.Features.TryGetValue(Selection.FeatureId,out var f)||f.PartId!=owner)
            throw new CadValidationException("Exact datum source is foreign, missing or belongs to another part.");
        return !f.IsStale&&!f.IsSuppressed&&f.Result==Source;
    }
}
public enum DatumAxisKind { Basis=0, TwoPoints=1, PlaneIntersection=2, PlaneNormal=3, Analytic=4, Independent=5 }
public enum DatumBasisAxis { X=0, Y=1, Z=2 }
public enum DatumPointKind { Independent=0, Offset=1, Midpoint=2, ThreePlanes=3, AxisPlane=4, CircleCenter=5 }

public sealed record CadDatumAxis(Guid Id,DefinitionId PartId,string Name,DatumAxisKind Kind,
    Guid? First=null,Guid? Second=null,ExactDatumGeometry? Exact=null,DatumBasisAxis Basis=DatumBasisAxis.Z,
    bool Reverse=false,Vector3d Origin=default,Vector3d Direction=default,double Offset=0)
{
    public Guid Revision {get;init;}=Guid.NewGuid();
    public RigidTransform3d Frame {get;init;}=RigidTransform3d.Identity;
    public ImmutableArray<Guid> SourceRevisions {get;init;}=[];
    public bool IsStale {get;init;}
    public void Validate()
    {
        DatumConstructions.Identity(Id,PartId,Name,Revision,SourceRevisions,2);Frame.Validate();
        DatumConstructions.Coordinate(Origin);Direction.Validate();CadGuard.Finite(Offset);
        if(!Enum.IsDefined(Kind)||!Enum.IsDefined(Basis)||Math.Abs(Offset)>1_000_000||First==Guid.Empty||Second==Guid.Empty||
            (Kind is DatumAxisKind.TwoPoints or DatumAxisKind.PlaneIntersection?First is null||Second is null||First==Second:
             Kind==DatumAxisKind.PlaneNormal?First is null||Second is not null:First is not null||Second is not null)||
            (Kind==DatumAxisKind.Analytic)!=(Exact is not null)||Kind!=DatumAxisKind.Basis&&Basis!=DatumBasisAxis.Z||
            Kind!=DatumAxisKind.Independent&&Direction!=Vector3d.Zero||Kind==DatumAxisKind.Independent&&Direction.Length<1e-8)
            throw new CadValidationException("Invalid datum axis construction or hidden companion.");
        Exact?.Validate();if(Exact?.Kind==ExactDatumKind.Plane)throw new CadValidationException("An analytic axis requires a cylinder, cone or circular edge.");
    }
}
public sealed record CadDatumPoint(Guid Id,DefinitionId PartId,string Name,DatumPointKind Kind,
    Vector3d Value=default,Guid? First=null,Guid? Second=null,Guid? Third=null,ExactDatumGeometry? Exact=null)
{
    public Guid Revision {get;init;}=Guid.NewGuid();
    public Vector3d Position {get;init;}
    public ImmutableArray<Guid> SourceRevisions {get;init;}=[];
    public bool IsStale {get;init;}
    public void Validate()
    {
        DatumConstructions.Identity(Id,PartId,Name,Revision,SourceRevisions,3);DatumConstructions.Coordinate(Value);DatumConstructions.Coordinate(Position);
        int count=Kind switch{DatumPointKind.Offset=>1,DatumPointKind.Midpoint or DatumPointKind.AxisPlane=>2,DatumPointKind.ThreePlanes=>3,_=>0};
        var refs=new[]{First,Second,Third};
        if(!Enum.IsDefined(Kind)||refs.Take(count).Any(id=>id is null||id==Guid.Empty)||refs.Skip(count).Any(id=>id is not null)||
            refs.Take(count).Distinct().Count()!=count||(Kind==DatumPointKind.CircleCenter)!=(Exact is not null)||
            Kind is not (DatumPointKind.Independent or DatumPointKind.Offset)&&Value!=Vector3d.Zero)
            throw new CadValidationException("Invalid datum point construction or hidden companion.");
        Exact?.Validate();if(Exact is not null&&Exact.Kind!=ExactDatumKind.Circle)throw new CadValidationException("Center requires an explicitly selected analytic circular edge.");
    }
}
/// <summary>A changed exact source is a recoverable frozen dependency, not permission to use an old slot.</summary>
public sealed class DatumSourceUnavailableException():InvalidOperationException("Datum source changed or is suppressed. Reselect or detach explicitly.");

public static class DatumConstructions
{
    public static void Coordinate(Vector3d p)
    {p.Validate();if(Math.Max(Math.Abs(p.X),Math.Max(Math.Abs(p.Y),Math.Abs(p.Z)))>1_000_000)throw new CadValidationException("Datum coordinates exceed one million mm.");}
    internal static void Identity(Guid id,DefinitionId part,string name,Guid revision,ImmutableArray<Guid> sources,int maximum)
    {CadGuard.Id(part);CadGuard.Name(name);if(id==Guid.Empty||revision==Guid.Empty||sources.IsDefault||sources.Length>maximum||sources.Any(r=>r==Guid.Empty))throw new CadValidationException("Invalid datum identity or evaluated revisions.");}
    public static RigidTransform3d Frame(Vector3d point,Vector3d normal,Vector3d u)
    {
        Coordinate(point);normal=normal.Normalized();u=u-normal*u.Dot(normal);
        if(u.Length<1e-8)throw new CadValidationException("Authored in-plane direction is parallel to the normal.");
        u=u.Normalized();var v=normal.Cross(u);double m00=u.X,m11=v.Y,m22=normal.Z;
        Quaterniond rotation;double s;
        if(m00+m11+m22>0){s=Math.Sqrt(1+m00+m11+m22)*2;rotation=new((v.Z-normal.Y)/s,(normal.X-u.Z)/s,(u.Y-v.X)/s,s/4);}
        else if(m00>m11&&m00>m22){s=Math.Sqrt(1+m00-m11-m22)*2;rotation=new(s/4,(v.X+u.Y)/s,(normal.X+u.Z)/s,(v.Z-normal.Y)/s);}
        else if(m11>m22){s=Math.Sqrt(1+m11-m00-m22)*2;rotation=new((v.X+u.Y)/s,s/4,(normal.Y+v.Z)/s,(normal.X-u.Z)/s);}
        else{s=Math.Sqrt(1+m22-m00-m11)*2;rotation=new((normal.X+u.Z)/s,(normal.Y+v.Z)/s,s/4,(u.Y-v.X)/s);}
        var frame=new RigidTransform3d(point,rotation);frame.Validate();return frame;
    }
    public static RigidTransform3d AxisFrame(Vector3d point,Vector3d axis)
    {
        // An axis has no authored roll. Choose a stable basis to encode only its directed line.
        axis=axis.Normalized();return Frame(point,axis,Math.Abs(axis.X)<0.9?new(1,0,0):new(0,1,0));
    }
    public static CadDatumPlane Plane(DocumentSnapshot doc,DefinitionId owner,Guid id,bool frozen=false)
    {
        if(!doc.Datums.Planes.TryGetValue(id,out var p)||p.PartId!=owner)throw new CadValidationException("Named plane is missing or foreign.");
        if(p.IsStale&&!frozen)throw new DatumSourceUnavailableException();return p;
    }
    public static CadDatumAxis Axis(DocumentSnapshot doc,DefinitionId owner,Guid id,bool frozen=false)
    {
        if(!doc.Datums.Axes.TryGetValue(id,out var p)||p.PartId!=owner)throw new CadValidationException("Named axis is missing or foreign.");
        if(p.IsStale&&!frozen)throw new DatumSourceUnavailableException();return p;
    }
    public static CadDatumPoint Point(DocumentSnapshot doc,DefinitionId owner,Guid id,bool frozen=false)
    {
        if(!doc.Datums.Points.TryGetValue(id,out var p)||p.PartId!=owner)throw new CadValidationException("Named point is missing or foreign.");
        if(p.IsStale&&!frozen)throw new DatumSourceUnavailableException();return p;
    }
    public static (RigidTransform3d Frame,ImmutableArray<Guid> Revisions) Resolve(DocumentSnapshot doc,CadDatumAxis a,bool frozen=false)
    {
        a.Validate();Vector3d point=a.Origin,axis;ImmutableArray<Guid> revisions=[];
        switch(a.Kind)
        {
            case DatumAxisKind.Independent:axis=a.Direction.Normalized();break;
            case DatumAxisKind.Basis:axis=a.Basis switch{DatumBasisAxis.X=>new(1,0,0),DatumBasisAxis.Y=>new(0,1,0),_=>Vector3d.UnitZ};break;
            case DatumAxisKind.TwoPoints:
                var p=Point(doc,a.PartId,a.First!.Value,frozen);var q=Point(doc,a.PartId,a.Second!.Value,frozen);point=p.Position;
                if((q.Position-p.Position).Length<1e-7)throw new CadValidationException("Two-point axis requires distinct points.");
                axis=(q.Position-p.Position).Normalized();revisions=[p.Revision,q.Revision];break;
            case DatumAxisKind.PlaneNormal:
                var plane=Plane(doc,a.PartId,a.First!.Value,frozen);axis=plane.Frame.Rotation.Rotate(Vector3d.UnitZ);
                point=a.Origin+axis*(plane.Frame.Translation-a.Origin).Dot(axis);revisions=[plane.Revision];break;
            case DatumAxisKind.PlaneIntersection:
                var first=Plane(doc,a.PartId,a.First!.Value,frozen);var second=Plane(doc,a.PartId,a.Second!.Value,frozen);
                var n=first.Frame.Rotation.Rotate(Vector3d.UnitZ);var m=second.Frame.Rotation.Rotate(Vector3d.UnitZ);var cross=n.Cross(m);
                if(cross.Length<1e-8)throw new CadValidationException("Plane intersection is parallel or coincident; there is no unique axis.");
                point=(m.Cross(cross)*n.Dot(first.Frame.Translation)+cross.Cross(n)*m.Dot(second.Frame.Translation))/cross.Dot(cross);
                axis=cross.Normalized();point=point+axis*(a.Origin-point).Dot(axis);revisions=[first.Revision,second.Revision];break;
            case DatumAxisKind.Analytic:
                if(!a.Exact!.IsCurrent(doc,a.PartId)&&!frozen)throw new DatumSourceUnavailableException();axis=a.Exact.Direction;
                point=a.Exact.Point+axis*(a.Origin-a.Exact.Point).Dot(axis);revisions=[a.Exact.Selection.Revision.Value];break;
            default:throw new CadValidationException("Unknown datum axis construction.");
        }
        if(a.Reverse)axis=axis*-1;return(AxisFrame(point+axis*a.Offset,axis),revisions);
    }
    public static (Vector3d Position,ImmutableArray<Guid> Revisions) Resolve(DocumentSnapshot doc,CadDatumPoint p,bool frozen=false)
    {
        p.Validate();Vector3d position;ImmutableArray<Guid> revisions=[];
        switch(p.Kind)
        {
            case DatumPointKind.Independent:position=p.Value;break;
            case DatumPointKind.Offset:var first=Point(doc,p.PartId,p.First!.Value,frozen);position=first.Position+p.Value;revisions=[first.Revision];break;
            case DatumPointKind.Midpoint:
                var a=Point(doc,p.PartId,p.First!.Value,frozen);var b=Point(doc,p.PartId,p.Second!.Value,frozen);position=a.Position+(b.Position-a.Position)/2;revisions=[a.Revision,b.Revision];break;
            case DatumPointKind.ThreePlanes:
                var f=Plane(doc,p.PartId,p.First!.Value,frozen);var s=Plane(doc,p.PartId,p.Second!.Value,frozen);var t=Plane(doc,p.PartId,p.Third!.Value,frozen);
                var n=f.Frame.Rotation.Rotate(Vector3d.UnitZ);var m=s.Frame.Rotation.Rotate(Vector3d.UnitZ);var k=t.Frame.Rotation.Rotate(Vector3d.UnitZ);double det=n.Dot(m.Cross(k));
                if(Math.Abs(det)<1e-8)throw new CadValidationException("Three-plane intersection is singular; no unique point.");
                position=(m.Cross(k)*n.Dot(f.Frame.Translation)+k.Cross(n)*m.Dot(s.Frame.Translation)+n.Cross(m)*k.Dot(t.Frame.Translation))/det;
                revisions=[f.Revision,s.Revision,t.Revision];break;
            case DatumPointKind.AxisPlane:
                var line=Axis(doc,p.PartId,p.First!.Value,frozen);var plane=Plane(doc,p.PartId,p.Second!.Value,frozen);var direction=line.Frame.Rotation.Rotate(Vector3d.UnitZ);var normal=plane.Frame.Rotation.Rotate(Vector3d.UnitZ);
                double dot=direction.Dot(normal);if(Math.Abs(dot)<1e-8)throw new CadValidationException("Axis/plane intersection is parallel or coincident; no unique point.");
                position=line.Frame.Translation+direction*((plane.Frame.Translation-line.Frame.Translation).Dot(normal)/dot);revisions=[line.Revision,plane.Revision];break;
            case DatumPointKind.CircleCenter:
                if(!p.Exact!.IsCurrent(doc,p.PartId)&&!frozen)throw new DatumSourceUnavailableException();position=p.Exact.Point;revisions=[p.Exact.Selection.Revision.Value];break;
            default:throw new CadValidationException("Unknown datum point construction.");
        }
        Coordinate(position);return(position,revisions);
    }
    public static IEnumerable<ExactDatumGeometry> ExactSources(DatumBook b)=>b.Planes.Values.SelectMany(p=>new[]{p.Primary.Exact,p.Secondary?.Exact}).Concat(b.Axes.Values.Select(a=>a.Exact)).Concat(b.Points.Values.Select(p=>p.Exact)).OfType<ExactDatumGeometry>().Distinct();
    public static bool IsStale(DatumBook b,Guid id)=>b.Planes.TryGetValue(id,out var p)?p.IsStale:b.Axes.TryGetValue(id,out var a)?a.IsStale:b.Points.TryGetValue(id,out var point)?point.IsStale:throw new CadValidationException("Missing datum.");
    public static RigidTransform3d InputFrame(DocumentSnapshot doc,DefinitionId owner,Guid id,bool allowAxis=false)
    {if(doc.Datums.Planes.ContainsKey(id))return Plane(doc,owner,id).Frame;if(allowAxis)return Axis(doc,owner,id).Frame;throw new CadValidationException("A plane datum is required.");}
    public static Guid Revision(DatumBook b,Guid id)=>b.Planes.TryGetValue(id,out var p)?p.Revision:b.Axes.TryGetValue(id,out var a)?a.Revision:b.Points.TryGetValue(id,out var point)?point.Revision:throw new CadValidationException("Missing datum.");
}
