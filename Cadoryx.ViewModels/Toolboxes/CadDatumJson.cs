using Cadoryx.Db;

namespace Cadoryx.ViewModels.Toolboxes;

/// <summary>Writable author input is separate from evaluated caches and native copied evidence.</summary>
internal static class CadDatumJson
{
    internal static double[] Vector(Vector3d p)=>[p.X,p.Y,p.Z];
    internal static object Frame(RigidTransform3d frame)=>new{origin=Vector(frame.Translation),normal=Vector(frame.Rotation.Rotate(Vector3d.UnitZ)),u=Vector(frame.Rotation.Rotate(new(1,0,0)))};
    internal static object Selection(ExactTopologySelection s)=>new{featureId=s.FeatureId.ToString(),revision=s.Revision.ToString(),asset=s.Asset.ToString(),
        fingerprint=s.Fingerprint,index=s.FullTopologyIndex,kind=s.Kind.ToString(),adapterVersion=s.AdapterVersion};
    internal static Dictionary<string,object> Source(DatumPlaneSource s)
    {
        var result=new Dictionary<string,object>{["kind"]=s.Kind.ToString()};
        switch(s.Kind)
        {
            case DatumSourceKind.Basis:result["basis"]=s.Basis.ToString();break;
            case DatumSourceKind.Datum:result["datum"]=s.Datum!.Value.ToString("D");break;
            case DatumSourceKind.BoxFace:result["feature"]=s.Feature!.Value.ToString();result["boundary"]=s.Boundary!.Value.ToString();break;
            case DatumSourceKind.ExactFace:result["selection"]=Selection(s.Exact!.Selection);result["frame"]=Frame(s.AuthoredFrame!.Value);break;
            case DatumSourceKind.Independent:result["frame"]=Frame(s.AuthoredFrame!.Value);break;
            case DatumSourceKind.AxisNormal:result["datum"]=s.Datum!.Value.ToString("D");if(s.Point is {} id)result["point"]=id.ToString("D");break;
        }
        return result;
    }
    internal static Dictionary<string,object> Author(object datum)
    {
        var result=new Dictionary<string,object>();
        if(datum is CadDatumPlane p)
        {
            result["kind"]=p.Kind.ToString();result["primary"]=Source(p.Primary);
            if(p.Secondary is {} s)result["secondary"]=Source(s);else result["value"]=p.Value;
            if(p.Kind==DatumPlaneKind.Angle){result["hinge"]=p.Hinge.ToString();if(p.HingeAxis is {} axis)result["hingeAxis"]=axis.ToString("D");}
        }
        else if(datum is CadDatumAxis a)
        {
            result["kind"]=a.Kind.ToString();result["origin"]=Vector(a.Origin);result["reverse"]=a.Reverse;result["offset"]=a.Offset;
            if(a.Kind==DatumAxisKind.Basis)result["basis"]=a.Basis.ToString();
            if(a.First is {} f)result["first"]=f.ToString("D");if(a.Second is {} s)result["second"]=s.ToString("D");
            if(a.Exact is {} exact)result["selection"]=Selection(exact.Selection);
            if(a.Kind==DatumAxisKind.Independent)result["direction"]=Vector(a.Direction);
        }
        else if(datum is CadDatumPoint t)
        {
            result["kind"]=t.Kind.ToString();if(t.Kind is DatumPointKind.Independent or DatumPointKind.Offset)result["value"]=Vector(t.Value);
            if(t.First is {} f)result["first"]=f.ToString("D");if(t.Second is {} s)result["second"]=s.ToString("D");if(t.Third is {} third)result["third"]=third.ToString("D");
            if(t.Exact is {} exact)result["selection"]=Selection(exact.Selection);
        }
        else throw new ArgumentException("Unknown datum type.");
        return result;
    }
    internal static object Edit(DocumentSnapshot snapshot,object datum)
    {
        var (action,id,revision,part,name)=datum switch
        {
            CadDatumPlane p=>("set_plane",p.Id,p.Revision,p.PartId,p.Name),
            CadDatumAxis a=>("set_axis",a.Id,a.Revision,a.PartId,a.Name),
            CadDatumPoint p=>("set_point",p.Id,p.Revision,p.PartId,p.Name),
            _=>throw new ArgumentException("Unknown datum type.")
        };
        return new{action,documentId=snapshot.Id.ToString(),stateId=snapshot.StateId.ToString(),id=id.ToString("D"),revision=revision.ToString("D"),
            partId=part.ToString(),name,author=Author(datum)};
    }
}
