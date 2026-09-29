using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Rendering;

public readonly record struct InstanceBodyKey(OccurrencePath Path,BodyId BodyId);
/// <summary>Transient visibility, separate from document/export visibility.</summary>
public sealed class ReviewVisibility
{
    private readonly HashSet<InstanceBodyKey> hidden=[];
    private HashSet<InstanceBodyKey>? isolated;
    public bool IsFiltered=>hidden.Count>0||isolated is not null;
    public ImmutableArray<ReviewInstance> CaptureHidden()=>[..hidden.Select(k=>new ReviewInstance(k.Path,k.BodyId))];
    public ImmutableArray<ReviewInstance>? CaptureIsolated()=>isolated is null?null:[..isolated.Select(k=>new ReviewInstance(k.Path,k.BodyId))];
    public void Restore(ReviewBookmark bookmark)
    {
        hidden.Clear();hidden.UnionWith(bookmark.Hidden.Select(k=>new InstanceBodyKey(k.Path,k.BodyId)));
        isolated=bookmark.Isolated is {} keys?keys.Select(k=>new InstanceBodyKey(k.Path,k.BodyId)).ToHashSet():null;
    }
    public void Hide(IEnumerable<InstanceBodyKey> keys)=>hidden.UnionWith(keys);
    public void Isolate(IEnumerable<InstanceBodyKey> keys)
    {
        var set=keys.ToHashSet();if(set.Count==0)return;isolated=set;hidden.ExceptWith(set);
    }
    public void Reset(){hidden.Clear();isolated=null;}
    public CadScene Apply(CadScene scene)=>scene with{Items=scene.Items.Where(i=>
        !hidden.Contains(new(i.Path,i.BodyId))&&(isolated is null||isolated.Contains(new(i.Path,i.BodyId)))).ToImmutableArray()};
}

public enum SectionAxis { X,Y,Z }
/// <summary>A visual half-space or slab in world coordinates; does not alter BRep/export.</summary>
public sealed record SectionView(bool Enabled=false,SectionAxis Axis=SectionAxis.Z,double OffsetMm=0,bool Reverse=false,double? SlabThicknessMm=null)
{
    public void Validate()
    {
        if(!Enum.IsDefined(Axis)||!double.IsFinite(OffsetMm)||Math.Abs(OffsetMm)>1e9||
            SlabThicknessMm is {} t&&(!double.IsFinite(t)||t<=0||t>1e9))
            throw new CadValidationException("Invalid section plane or slab thickness.");
    }
    public IReadOnlyList<(Vector3d Normal,double D)> Planes()
    {
        Validate();if(!Enabled)return [];
        var n=Axis switch{SectionAxis.X=>new Vector3d(1,0,0),SectionAxis.Y=>new(0,1,0),_=>Vector3d.UnitZ};
        if(SlabThicknessMm is {} thickness)return [(n,-OffsetMm+thickness/2),(n*-1,OffsetMm+thickness/2)];
        double sign=Reverse?-1:1;return [(n*sign,-OffsetMm*sign)];
    }
}

public static class SceneEnvelope
{
    public static Bounds3d? Measure(IEnumerable<SceneItem> items)
    {
        var points=new List<Vector3d>();
        foreach(var item in items)
        {
            var b=item.Geometry.Bounds;
            foreach(var x in new[]{b.Min.X,b.Max.X})foreach(var y in new[]{b.Min.Y,b.Max.Y})foreach(var z in new[]{b.Min.Z,b.Max.Z})
                points.Add(item.WorldTransform.Apply(new(x,y,z)));
        }
        return points.Count==0?null:new(new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),
            new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)));
    }
    public static CadCamera Fit(CadCamera camera,Bounds3d bounds)
    {
        bounds.Validate();double radius=Math.Max((bounds.Max-bounds.Min).Length/2,0.001);
        double scale=radius*2.3/Math.Min(Math.Max(camera.Aspect,0.01),1);
        double distance=Math.Max((camera.Eye-camera.Target).Length,scale);
        if(camera.Perspective)distance=Math.Max(distance,scale/2/Math.Tan(camera.FieldOfViewY*Math.PI/360));
        var center=(bounds.Min+bounds.Max)/2;var direction=(camera.Eye-camera.Target).Normalized();
        return camera with{Target=center,Eye=center+direction*distance,Scale=scale,AutoFitDepth=true};
    }
    /// <summary>Frames the entire scene before any native presentation exists, including its depth.</summary>
    public static CadCamera FitVisible(CadCamera camera,Bounds3d bounds)
        =>FitVisible(camera,bounds,Corners(bounds));
    /// <summary>Uses each placed body corner, avoiding a second inflation from the scene's world AABB.</summary>
    public static CadCamera FitVisible(CadCamera camera,IReadOnlyCollection<SceneItem> items)
    {
        if(Measure(items) is not {} bounds)throw new ArgumentException("The scene has no visible bodies.",nameof(items));
        return FitVisible(camera,bounds,items.SelectMany(item=>Corners(item.Geometry.Bounds).Select(item.WorldTransform.Apply)));
    }
    private static IEnumerable<Vector3d> Corners(Bounds3d bounds)
    {
        foreach(double x in new[]{bounds.Min.X,bounds.Max.X})
        foreach(double y in new[]{bounds.Min.Y,bounds.Max.Y})
        foreach(double z in new[]{bounds.Min.Z,bounds.Max.Z})yield return new(x,y,z);
    }
    private static CadCamera FitVisible(CadCamera camera,Bounds3d bounds,IEnumerable<Vector3d> points)
    {
        bounds.Validate();
        var center=(bounds.Min+bounds.Max)/2;
        var back=(camera.Eye-camera.Target).Normalized();
        var right=camera.Up.Cross(back).Normalized();
        var up=back.Cross(right).Normalized();
        double aspect=Math.Max(camera.Aspect,0.01),halfHeight=0,maxDepth=0;
        double tanHalfFov=Math.Tan(camera.FieldOfViewY*Math.PI/360);
        foreach(var point in points)
        {
            var offset=point-center;
            double span=Math.Max(Math.Abs(offset.Dot(up)),Math.Abs(offset.Dot(right))/aspect);
            halfHeight=Math.Max(halfHeight,span);
            if(camera.Perspective&&tanHalfFov>0)
                maxDepth=Math.Max(maxDepth,offset.Dot(back)+span*1.15/tanHalfFov);
        }
        double scale=Math.Max(halfHeight*2*1.15,0.01);
        double distance=camera.Perspective&&tanHalfFov>0
            ?Math.Max(maxDepth+(bounds.Max-bounds.Min).Length*0.01,0.01)
            :Math.Max((camera.Eye-camera.Target).Length,scale);
        return camera with{Target=center,Eye=center+back*distance,Up=up,Scale=scale,AutoFitDepth=true};
    }
}
