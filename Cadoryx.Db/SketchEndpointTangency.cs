namespace Cadoryx.Db;

/// <summary>Identity-based smooth joins of supported finite sketch curves.</summary>
public static class SketchEndpointTangency
{
    public static (SketchEntityId Start,SketchEntityId End) Endpoints(CadSketch sketch,SketchEntityId curve)
    {
        if(sketch.Lines.SingleOrDefault(l=>l.Id==curve) is {} line)return(line.Start,line.End);
        if(sketch.Arcs.SingleOrDefault(a=>a.Id==curve) is {} arc)return(arc.Start,arc.End);
        if(sketch.Beziers.SingleOrDefault(b=>b.Id==curve) is {} bezier)return(bezier.Start,bezier.End);
        if(sketch.Splines.SingleOrDefault(s=>s.Id==curve) is {} spline)return(spline.Controls[0],spline.Controls[^1]);
        if(sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==curve) is {Endpoints:{} expression})return(expression.Start,expression.End);
        throw new CadValidationException("Endpoint tangency supports lines, arcs, quadratic Beziers and clamped cubic splines.");
    }
    public static SketchEntityId CommonEndpoint(CadSketch sketch,SketchEntityId a,SketchEntityId b)
    {
        if(a==b||sketch.Lines.Any(l=>l.Id==a)&&sketch.Lines.Any(l=>l.Id==b))
            throw new CadValidationException("Select two distinct curves, including at least one curved entity.");
        var first=Endpoints(sketch,a);var second=Endpoints(sketch,b);
        var shared=new[]{first.Start,first.End}.Intersect(new[]{second.Start,second.End}).ToArray();
        if(shared.Length!=1)throw new CadValidationException("Curves must share exactly one endpoint ID. Coincident coordinates do not merge identities.");
        return shared[0];
    }
    public static SketchEntityId TangentNeighbor(CadSketch sketch,SketchEntityId curve,SketchEntityId joint)
    {
        var ends=Endpoints(sketch,curve);
        if(joint!=ends.Start&&joint!=ends.End)throw new CadValidationException("Tangency joint must be a curve endpoint.");
        if(sketch.Lines.Any(l=>l.Id==curve))return joint==ends.Start?ends.End:ends.Start;
        if(sketch.Beziers.SingleOrDefault(b=>b.Id==curve) is {} bezier)return bezier.Control;
        if(sketch.Splines.SingleOrDefault(s=>s.Id==curve) is {} spline)return joint==ends.Start?spline.Controls[1]:spline.Controls[^2];
        throw new CadValidationException("Circular arc tangents require the three-point branch.");
    }
    public static bool IsParametric(CadSketch sketch,SketchEntityId curve)=>sketch.Beziers.Any(b=>b.Id==curve)||sketch.Splines.Any(s=>s.Id==curve);
    public static void ValidateParametricDerivative(CadSketch sketch,SketchEntityId curve,SketchEntityId joint)
    {
        if(sketch.ExpressionCurves.Any(c=>c.Id==curve)){_=SketchEndpointCurvature.Evaluate(sketch,curve,joint);return;}
        if(!IsParametric(sketch,curve))return;
        var p=sketch.Points.Single(p=>p.Id==joint).Position;var q=sketch.Points.Single(p=>p.Id==TangentNeighbor(sketch,curve,joint)).Position;
        if(double.Hypot(q.X-p.X,q.Y-p.Y)<=1e-7)throw new CadValidationException("Endpoint tangent control must be more than 1e-7 mm from the joint.");
    }
    // Chords start at the selected endpoint; the middle chord fixes the arc's inward direction,
    // including major arcs. This avoids a circumcenter division near a straight arc.
    public static Point2d ArcTangentNumerator(Point2d middleChord,Point2d endChord)
    {
        double u2=middleChord.X*middleChord.X+middleChord.Y*middleChord.Y;
        double v2=endChord.X*endChord.X+endChord.Y*endChord.Y;
        return new(middleChord.X*v2-endChord.X*u2,v2*middleChord.Y-u2*endChord.Y);
    }
}
