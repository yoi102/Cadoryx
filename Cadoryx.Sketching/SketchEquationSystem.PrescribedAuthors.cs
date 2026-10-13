using Cadoryx.Db;

namespace Cadoryx.Sketching;

internal sealed partial class SketchEquationSystem
{
    // A FixPoint is an authored equality, not an inferred coordinate match.
    // This only eliminates its column from the iteration step when every
    // endpoint-relation source coordinate is explicitly and uniquely fixed.
    // Generated endpoints are never prescribed by this extension. Unproven,
    // partially fixed or expression sources keep the existing coupled route.
    private static bool FullyPrescribedEndpointAuthors(CadSketch sketch)
    {
        var enabled=sketch.Constraints.Where(c=>c.IsEnabled).ToArray();
        if(!enabled.Any(c=>c is EndpointTangentConstraint or EndpointCurvatureConstraint))return false;
        var fixes=enabled.OfType<FixPointConstraint>().ToArray();
        var generatedEndpoints=SketchAssociativeEndpoints.Equations(sketch).Select(e=>e.Point).ToHashSet();
        if(fixes.Any(c=>generatedEndpoints.Contains(c.Point)))return false;
        var fixedPoints=fixes.GroupBy(c=>c.Point)
            .Where(g=>g.Select(c=>c.Position).Distinct().Count()==1).Select(g=>g.Key).ToHashSet();
        if(fixedPoints.Count==0)return false;
        // Endpoint relations alone do not prove every region source fixed.
        // A primitive may consume a live generated endpoint from another author;
        // retain the full coupled solve whenever that dependency is not prescribed.
        if(sketch.RegionOffsets.Where(g=>!g.IsFrozen).SelectMany(g=>g.Sources)
            .SelectMany(loop=>loop).Any(source=>!Complete(source.Entity)))return false;
        if(sketch.ExpressionCurves.Where(c=>c.Source is {IsFrozen:false})
            .Any(c=>!Complete(c.Source!.Entity)))return false;
        return enabled.All(c=>c switch
        {
            FixPointConstraint fix=>fixedPoints.Contains(fix.Point),
            RadiusConstraint radius=>sketch.Circles.Any(circle=>circle.Id==radius.Circle),
            EndpointTangentConstraint relation=>Complete(relation.A)&&Complete(relation.B),
            EndpointCurvatureConstraint relation=>Complete(relation.A)&&Complete(relation.B),
            _=>false
        });

        bool Complete(SketchEntityId curve)
        {
            if(sketch.Lines.SingleOrDefault(c=>c.Id==curve) is {} line)
                return fixedPoints.Contains(line.Start)&&fixedPoints.Contains(line.End);
            if(sketch.Arcs.SingleOrDefault(c=>c.Id==curve) is {} arc)
                return fixedPoints.Contains(arc.Start)&&fixedPoints.Contains(arc.Middle)&&fixedPoints.Contains(arc.End);
            if(sketch.Beziers.SingleOrDefault(c=>c.Id==curve) is {} bezier)
                return fixedPoints.Contains(bezier.Start)&&fixedPoints.Contains(bezier.Control)&&fixedPoints.Contains(bezier.End);
            if(sketch.Splines.SingleOrDefault(c=>c.Id==curve) is {} spline)
                return spline.Controls.All(fixedPoints.Contains);
            return false;
        }
    }
}
