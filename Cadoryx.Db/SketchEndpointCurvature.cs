using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Exact one-sided endpoint geometry. Both tangents point into their own curve.</summary>
public static class SketchEndpointCurvature
{
    public static SketchEntityId CommonEndpoint(CadSketch sketch,SketchEntityId a,SketchEntityId b)
    {
        if(a==b)throw new CadValidationException("Select two distinct curves.");
        var first=SketchEndpointTangency.Endpoints(sketch,a);var second=SketchEndpointTangency.Endpoints(sketch,b);
        var shared=new[]{first.Start,first.End}.Intersect(new[]{second.Start,second.End}).ToArray();
        if(shared.Length!=1)throw new CadValidationException("Curvature continuity requires exactly one shared endpoint ID.");
        return shared[0];
    }
    public static void RequireSolvedRelations(CadSketch sketch,double angularTolerance=1e-9,double curvatureTolerancePerMm=1e-7)
    {
        CadGuard.Positive(angularTolerance,curvatureTolerancePerMm);
        foreach(var c in sketch.Constraints.OfType<EndpointCurvatureConstraint>().Where(c=>c.IsEnabled))
        {
            var a=Evaluate(sketch,c.A,c.Joint);var b=Evaluate(sketch,c.B,c.Joint);
            if(Math.Abs(a.Tangent.X*b.Tangent.Y-a.Tangent.Y*b.Tangent.X)>angularTolerance||
                a.Tangent.X*b.Tangent.X+a.Tangent.Y*b.Tangent.Y>=0||Math.Abs(a.CurvaturePerMm+b.CurvaturePerMm)>curvatureTolerancePerMm)
            {
                var error=new CadValidationException("SKETCH.CURVATURE_OUTPUT");error.Data["SketchCurve.Message"]="CurvatureOutputFailure";throw error;
            }
        }
    }
    public static void ValidateTargets(CadSketch sketch,EndpointCurvatureConstraint constraint)
    {
        if(constraint.Branch!=EndpointTangentBranch.Opposed||CommonEndpoint(sketch,constraint.A,constraint.B)!=constraint.Joint)
            throw new CadValidationException("Curvature continuity requires a unique shared endpoint and the smooth branch.");
        foreach(var curve in new[]{constraint.A,constraint.B})
        {
            SketchEndpointTangency.ValidateParametricDerivative(sketch,curve,constraint.Joint);
            var geometry=Evaluate(sketch,curve,constraint.Joint);
            CadGuard.Finite(geometry.Tangent.X,geometry.Tangent.Y,geometry.CurvaturePerMm);
        }
    }

    /// <summary>Linear coefficients for inward first and second normalized parameter derivatives.</summary>
    public static ImmutableArray<(SketchEntityId Point,double First,double Second)> DerivativeWeights(CadSketch sketch,SketchEntityId curve,SketchEntityId joint)
    {
        var ends=SketchEndpointTangency.Endpoints(sketch,curve);
        if(joint!=ends.Start&&joint!=ends.End)throw new CadValidationException("Curvature joint must be an endpoint.");
        bool start=joint==ends.Start;
        if(sketch.Lines.Any(l=>l.Id==curve))return [(joint,-1,0),(start?ends.End:ends.Start,1,0)];
        if(sketch.Beziers.SingleOrDefault(b=>b.Id==curve) is {} bezier)
            return [(joint,-2,2),(bezier.Control,2,-4),(start?bezier.End:bezier.Start,0,2)];
        if(sketch.Splines.SingleOrDefault(s=>s.Id==curve) is not {} spline)
            throw new CadValidationException("Arc curvature uses its exact three-point circle.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var definition=CubicSplineKnotVector.Definition(spline.Controls.Select(id=>points[id]).ToImmutableArray(),spline.Knots);
        var full=definition.ExpandedKnots();double range=definition.Knots[^1]-definition.Knots[0];
        var knots=start?full.Select(k=>(k-full[0])/range).ToArray():full.Reverse().Select(k=>(full[^1]-k)/range).ToArray();
        double first=3/(knots[4]-knots[1]),next=3/(knots[5]-knots[2]),second=2/(knots[4]-knots[2]);
        CadGuard.Finite(first,next,second,second*first,second*(first+next),second*next);
        var controls=start?spline.Controls:spline.Controls.Reverse().ToImmutableArray();
        return [(controls[0],-first,second*first),(controls[1],first,-second*(first+next)),(controls[2],0,second*next)];
    }

    public static (Point2d Tangent,double CurvaturePerMm) Evaluate(CadSketch sketch,SketchEntityId curve,SketchEntityId joint)
    {
        if(sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==curve) is {} expression)
        {
            var e=expression.Endpoints??throw new CadValidationException("Expression endpoint identities are missing.");
            if(joint!=e.Start&&joint!=e.End)throw new CadValidationException("Select an expression endpoint.");
            var value=expression.Geometry.Evaluate(joint==e.Start?0:1);double expressionSpeed=double.Hypot(value.First.X,value.First.Y),sign=joint==e.Start?1:-1;
            if(!double.IsFinite(expressionSpeed)||expressionSpeed<=1e-7)throw new CadValidationException("Degenerate expression endpoint tangent.");
            double expressionK=sign*(value.First.X*value.Second.Y-value.First.Y*value.Second.X)/(expressionSpeed*expressionSpeed*expressionSpeed);CadGuard.Finite(expressionK);
            return(new(sign*value.First.X/expressionSpeed,sign*value.First.Y/expressionSpeed),expressionK);
        }
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);var origin=points[joint];
        Point2d Delta(SketchEntityId point)=>new(points[point].X-origin.X,points[point].Y-origin.Y);
        if(sketch.Arcs.SingleOrDefault(a=>a.Id==curve) is {} arc)
        {
            if(joint!=arc.Start&&joint!=arc.End)throw new CadValidationException("Curvature joint must be an arc endpoint.");
            var u=Delta(arc.Middle);var v=Delta(joint==arc.Start?arc.End:arc.Start);
            var tangent=SketchEndpointTangency.ArcTangentNumerator(u,v);double length=double.Hypot(tangent.X,tangent.Y);
            double curvature=2*(u.X*v.Y-u.Y*v.X)/(double.Hypot(u.X,u.Y)*double.Hypot(v.X,v.Y)*double.Hypot(v.X-u.X,v.Y-u.Y));
            CadGuard.Finite(length,curvature);if(length<=0)throw new CadValidationException("Degenerate endpoint derivative.");
            return (new(tangent.X/length,tangent.Y/length),curvature);
        }
        Point2d first=default,second=default;
        foreach(var weight in DerivativeWeights(sketch,curve,joint))
        {
            var p=Delta(weight.Point);first=new(first.X+weight.First*p.X,first.Y+weight.First*p.Y);
            second=new(second.X+weight.Second*p.X,second.Y+weight.Second*p.Y);
        }
        double speed=double.Hypot(first.X,first.Y);
        if(!double.IsFinite(speed)||speed<=1e-7)throw new CadValidationException("Degenerate endpoint derivative.");
        double k=(first.X*second.Y-first.Y*second.X)/(speed*speed*speed);CadGuard.Finite(k);
        return (new(first.X/speed,first.Y/speed),k);
    }
}
