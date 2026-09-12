using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

/// <summary>Semantic classification only, never nearest matching or persistent enumeration IDs.</summary>
public static class BoxTopology
{
    public static bool Matches(Shape shape,BoxRecipe box,TopologyKind kind,BoxBoundary first,BoxBoundary? second=null)
    {
        const double tolerance=1e-6;
        if(Math.Min(box.X,Math.Min(box.Y,box.Z))<=tolerance*100)return false;
        using var inverse=OcctGeometryBridge.ToNative(box.Placement.Inverse());using var local=shape.Transformed(inverse);
        if(kind==TopologyKind.Face?local.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Plane:local.GetEdgeCurveSnapshot().CurveType!=CurveGeometryType.Line)return false;
        var bounds=local.GetBoundingBox();var min=new[]{bounds.Minimum.X,bounds.Minimum.Y,bounds.Minimum.Z};var max=new[]{bounds.Maximum.X,bounds.Maximum.Y,bounds.Maximum.Z};
        var expectedMin=new double[3];var expectedMax=new[]{box.X,box.Y,box.Z};
        foreach(var boundary in second is {} s?new[]{first,s}:new[]{first})
        {int axis=(int)boundary/2;double value=(int)boundary%2==0?0:expectedMax[axis];expectedMin[axis]=value;expectedMax[axis]=value;}
        return Enumerable.Range(0,3).All(axis=>Math.Abs(min[axis]-expectedMin[axis])<=tolerance&&Math.Abs(max[axis]-expectedMax[axis])<=tolerance);
    }
    public static IReadOnlyList<(BoxBoundary First,BoxBoundary? Second)> Classify(Shape shape,BoxRecipe box,TopologyKind kind)
    {
        var result=new List<(BoxBoundary,BoxBoundary?)>();
        foreach(var first in Enum.GetValues<BoxBoundary>())
        {
            if(kind==TopologyKind.Face){if(Matches(shape,box,kind,first))result.Add((first,null));}
            else foreach(var second in Enum.GetValues<BoxBoundary>().Where(s=>(int)s/2>(int)first/2))
                if(Matches(shape,box,kind,first,second))result.Add((first,second));
        }
        return result;
    }
}
