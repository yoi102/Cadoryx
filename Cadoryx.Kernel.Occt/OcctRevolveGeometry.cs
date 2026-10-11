using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private static Shape BuildRevolve(RevolveRecipe recipe,CancellationToken token,bool canonicalExpressions=false)
    {
        var (point,direction)=recipe.Axis?.InMeridian(recipe.Placement)??(Vector3d.Zero,Vector3d.UnitZ);
        double Radius(Point2d p)=>(p.X-point.X)*direction.Z-(p.Y-point.Z)*direction.X;
        var outer=recipe.Profile.BoundaryCurves.SelectMany(c=>new[]{Radius(c.Start),Radius(c.End)}).ToArray();
        double radialSign=recipe.Axis is not null&&outer.Length>0&&outer.Min()< -1e-7&&outer.Max()<=1e-7?-1:1;
        void CheckFreeformAxis(SketchProfile profile)
        {
            foreach(var edge in profile.BoundaryCurves.Where(c=>c.Expression is not null))
            {
                var kernel=new OcctGeometryKernel();var expression=NativeExpressionCurve(kernel.PrepareExpression(edge.Expression!)).Curve;
                using var nativeEdge=expression.ToEdge(SketchPlane.XY);var bounds=nativeEdge.GetBoundingBox();
                double bound=new[]{new Point2d(bounds.Minimum.X,bounds.Minimum.Y),new(bounds.Minimum.X,bounds.Maximum.Y),
                    new(bounds.Maximum.X,bounds.Minimum.Y),new(bounds.Maximum.X,bounds.Maximum.Y)}.Max(p=>double.Hypot(p.X-point.X,p.Y-point.Z))+1;
                var axisCurve=SketchCurveExpression2d.FromCurve(SketchCurve2d.Segment(new(point.X-bound*direction.X,point.Z-bound*direction.Z),
                    new(point.X+bound*direction.X,point.Z+bound*direction.Z)));
                var hits=expression.InspectIntersections(axisCurve);
                if(hits.HasOverlappingIntervals||hits.Endpoints.Any(h=>h.FirstNormalizedParameter>1e-10&&h.FirstNormalizedParameter<1-1e-10))
                    throw new CadValidationException("An exact expression cannot touch or cross the revolution axis internally.");
                var middle=expression.Evaluate(.5).Point;
                if(radialSign*Radius(new(middle.X,middle.Y))<=1e-7)
                    throw new CadValidationException("The exact expression revolution boundary lies outside its radial half-plane.");
            }
            foreach(var edge in profile.BoundaryCurves.Where(c=>c.Freeform is not null))
            {
                var g=edge.Freeform!;double bound=g.Poles.Max(p=>double.Hypot(p.X-point.X,p.Y-point.Z))+1;
                var axisCurve=SketchCurve2d.Segment(new(point.X-bound*direction.X,point.Z-bound*direction.Z),
                    new(point.X+bound*direction.X,point.Z+bound*direction.Z));
                var curve=OcctFreeformSketchGeometry.Create(g);
                var hits=SketchModeling.InspectIntersections(curve,axisCurve);
                if(hits.HasOverlappingIntervals||hits.Endpoints.Any(h=>h.FirstNormalizedParameter>1e-10&&h.FirstNormalizedParameter<1-1e-10))
                    throw new CadValidationException("A copied freeform boundary cannot touch or cross the revolution axis internally.");
                // Exact intersections prove this finite continuous curve stays on one side. Classify that side once.
                if(radialSign*Radius(g.At(.5))<=1e-7)
                    throw new CadValidationException("The copied freeform revolution boundary lies outside its declared radial half-plane.");
            }
        }
        using var axis=GpAx1.Create(point.X,point.Y,point.Z,direction.X,direction.Y,direction.Z);
        Shape Boundary(SketchProfile profile)
        {
            token.ThrowIfCancellationRequested();
            CheckFreeformAxis(profile);
            using var face=OcctSketchProfileGeometry.CreateFace(profile,true,canonicalExpressions);RequireClear(face,token);
            var result=face.Revolve(axis,recipe.AngleRadians);
            try
            {
                RequireOutput(result,GuidedOutput.Solid,token);
                if(result.GetTopologySummary().UniqueCounts.SolidCount!=1)
                    throw new CadValidationException("One revolution boundary must produce exactly one solid.");
                return result;
            }
            catch{result.Dispose();throw;}
        }
        Shape Region(SketchProfile profile)
        {
            var holes=HoleBoundaries(profile).ToArray();
            ValidatePlanarRegion(OuterBoundary(profile),holes,profile.Islands.Select(i=>OuterBoundary(i.Profile)).ToArray(),token);
            Shape current=Boundary(OuterBoundary(profile));
            try
            {
                foreach(var profileHole in holes)
                {
                    using var hole=Boundary(profileHole);RequireContained(current,hole,token);
                    var next=current.Cut(hole);current.Dispose();current=next;RequireOutput(current,GuidedOutput.Solid,token);
                }
                if(current.GetTopologySummary().UniqueCounts.SolidCount!=1)
                    throw new CadValidationException("An unconnected material region must be authored as an explicit island.");
                if(profile.Islands.IsEmpty)return current;
                var children=new List<Shape>();
                try
                {
                    foreach(var island in profile.Islands)children.Add(Region(island.Profile));
                    var compound=ShapeFactory.CreateCompound(new[]{current}.Concat(children).ToArray());
                    try{RequireOutput(compound,GuidedOutput.Solid,token);return compound;}
                    catch{compound.Dispose();throw;}
                }
                finally{foreach(var child in children)child.Dispose();current.Dispose();}
            }
            catch{current.Dispose();throw;}
        }
        using var local=Region(recipe.Profile);
        return AuthoringPlaced(CopyAuthoringShape(local),recipe.Placement);
    }
}
