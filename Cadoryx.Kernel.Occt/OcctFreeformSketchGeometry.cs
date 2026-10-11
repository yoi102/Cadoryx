using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

internal static class OcctFreeformSketchGeometry
{
    internal static SketchCurve2d Create(SketchFreeformGeometry geometry)
    {
        geometry.Validate();var poles=geometry.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray();
        IReadOnlyList<double>? weights=geometry.Weights.IsEmpty?null:geometry.Weights;
        var full=geometry.Kind==SketchFreeformKind.Bezier?SketchCurve2d.Bezier(poles,weights):
            SketchCurve2d.BSpline(poles,geometry.Knots,geometry.Multiplicities,geometry.Degree,geometry.Periodic,weights);
        double first=geometry.FirstParameter,last=geometry.LastParameter;
        if(geometry.Periodic&&(geometry.FirstParameter<full.FirstParameter||geometry.LastParameter>full.LastParameter))
        {
            // Segment the exact periodic basis in native space. The original authored tables and
            // requested interval remain untouched; this owned finite representation is kernel-only.
            using var basis=FreeformAuthoring.CreateCurve(FreeformCurveDefinition.BSpline(
                geometry.Poles.Select(p=>new GpPoint(p.X,p.Y,0)).ToArray(),geometry.Knots,geometry.Multiplicities,
                geometry.Degree,true,weights));
            using var segment=FreeformAuthoring.SegmentCurve(basis,new(geometry.FirstParameter,geometry.LastParameter));
            var copied=FreeformAuthoring.GetCurveDefinition(segment);var interval=copied.ParameterRange!.Value;
            double tolerance=(last-first)*1e-12;
            if(copied.Kind!=FreeformGeometryKind.BSpline||copied.Degree!=geometry.Degree||
               Math.Abs(interval.First-first)>tolerance||Math.Abs(interval.Last-last)>tolerance)
                throw new CadValidationException("Native periodic segmentation changed the requested finite interval or curve family.");
            first=interval.First;last=interval.Last;
            full=SketchCurve2d.BSpline(copied.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),copied.Knots,copied.Multiplicities,
                copied.Degree,copied.Periodic,copied.Weights.Count==0?null:copied.Weights);
        }
        double width=full.LastParameter-full.FirstParameter;
        var result=first==full.FirstParameter&&last==full.LastParameter?full:
            full.Trim((first-full.FirstParameter)/width,(last-full.FirstParameter)/width);
        return geometry.Reversed?result.Reverse():result;
    }
}
