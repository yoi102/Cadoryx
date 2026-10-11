using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IExactSketchRegionRelations
{
    public ExactRegionRelation CompareRegions(ImmutableArray<SketchBoundaryCurve> first,ImmutableArray<SketchBoundaryCurve> second)=>ExactGuard(()=>
    {
        static SketchProfile Profile(ImmutableArray<SketchBoundaryCurve> curves)
        {
            if(curves.IsDefaultOrEmpty||curves.Length>256)throw new CadValidationException("A region requires one to 256 explicitly directed edges.");
            for(int i=0;i<curves.Length;i++)
            {
                var curve=ExactSketchCurve.FromBoundary(curves[i]);var next=curves[(i+1)%curves.Length].Start;
                if(double.Hypot(curve.End.X-next.X,curve.End.Y-next.Y)>1e-7)throw new CadValidationException("Exact region comparison requires a closed directed chain.");
            }
            return new([]){BoundaryCurves=curves};
        }
        // Straight polygons are accepted by the exact builder; no display samples identify containment.
        using var a=OcctSketchProfileGeometry.CreateFace(Profile(first),false);using var b=OcctSketchProfileGeometry.CreateFace(Profile(second),false);
        RequireClear(a,CancellationToken.None);RequireClear(b,CancellationToken.None);
        var aw=a.GetSubShapes(ShapeKind.Wire);var bw=b.GetSubShapes(ShapeKind.Wire);
        try
        {
            if(aw.Length!=1||bw.Length!=1)throw new CadValidationException("A region comparison requires one outer wire per region.");
            if(aw[0].DistanceTo(bw[0]).Distance<=1e-7)return ExactRegionRelation.ContactOrOverlap;
            using var common=a.Common(b);
            if(common.GetTopologySummary().UniqueCounts.FaceCount==0)return ExactRegionRelation.Disjoint;
            using var bOutside=b.Cut(a);
            if(bOutside.GetTopologySummary().UniqueCounts.FaceCount==0)return ExactRegionRelation.Contains;
            using var aOutside=a.Cut(b);
            return aOutside.GetTopologySummary().UniqueCounts.FaceCount==0?ExactRegionRelation.Contained:ExactRegionRelation.ContactOrOverlap;
        }
        finally{foreach(var wire in aw)wire.Dispose();foreach(var wire in bw)wire.Dispose();}
    });
}
