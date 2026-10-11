using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Contact at two explicit finite author parameters, without merging point identities.</summary>
public static class SketchFiniteParametricContact
{
    public static void ValidateTargets(CadSketch sketch,FiniteParametricContactConstraint constraint)
    {
        if(constraint.A==constraint.B||!Enum.IsDefined(constraint.Branch)||!Enum.IsDefined(constraint.Continuity)||!Enum.IsDefined(constraint.ParameterMode))
        {
            var error=new CadValidationException("Select two distinct author curves and an explicit tangent branch.");
            error.Data["SketchContact.Message"]="SlidingContactInvalidMode";throw error;
        }
        if(constraint.SolvesA!=(constraint.SlidingA is not null)||constraint.SolvesB!=(constraint.SlidingB is not null))
        {
            var error=new CadValidationException("Automatic contact parameters require explicit seed/bounds; fixed parameters cannot contain hidden sliding authors.");
            error.Data["SketchContact.Message"]="SlidingContactInvalidMode";throw error;
        }
        constraint.SlidingA?.Validate(constraint.ParameterA);constraint.SlidingB?.Validate(constraint.ParameterB);
        foreach(var target in new[]{(constraint.A,constraint.ParameterA,constraint.SlidingA),(constraint.B,constraint.ParameterB,constraint.SlidingB)})
            if(target.Item3 is {} sliding)
                foreach(double parameter in new[]{sliding.Seed,target.Item2})
                {
                    if(constraint.Continuity==FiniteContactContinuity.Curvature)EvaluateThirdDerivatives(sketch,target.Item1,parameter);
                    else EvaluateDerivatives(sketch,target.Item1,parameter,2);
                }
        foreach(var target in new[]{(constraint.A,constraint.ParameterA),(constraint.B,constraint.ParameterB)})
        {
            var geometry=EvaluateGeometry(sketch,target.Item1,target.Item2,(int)constraint.Continuity);
            CadGuard.Finite(geometry.Position.X,geometry.Position.Y,geometry.Tangent.X,geometry.Tangent.Y,geometry.CurvaturePerMm);
        }
    }

    public static ImmutableArray<(SketchEntityId Point,double Position,double First)> Weights(CadSketch sketch,SketchEntityId curve,double parameter)
        =>DerivativeWeights(sketch,curve,parameter).Select(w=>(w.Point,w.Position,w.First)).ToImmutableArray();

    public static bool HasControlWeights(CadSketch sketch,SketchEntityId curve)=>
        sketch.Lines.Any(c=>c.Id==curve)||sketch.Beziers.Any(c=>c.Id==curve)||sketch.Splines.Any(c=>c.Id==curve);

    public static bool UsesExpression(CadSketch sketch,SketchConstraint constraint)=>SketchAssociativeEndpoints.HasExpressionTarget(sketch,constraint);

    public static ImmutableArray<(SketchEntityId Point,double Position,double First,double Second)> DerivativeWeights(CadSketch sketch,SketchEntityId curve,double parameter)
    {
        if(!double.IsFinite(parameter)||parameter<=0||parameter>=1)
            throw InvalidParameterOrder("Finite contact parameters must be strictly inside (0,1); use endpoint relations at endpoints.");
        if(sketch.Lines.SingleOrDefault(l=>l.Id==curve) is {} line)
            return [(line.Start,1-parameter,-1,0),(line.End,parameter,1,0)];
        if(sketch.Beziers.SingleOrDefault(b=>b.Id==curve) is {} bezier)
        {
            double s=1-parameter;
            return [(bezier.Start,s*s,-2*s,2),(bezier.Control,2*s*parameter,2-4*parameter,-4),(bezier.End,parameter*parameter,2*parameter,2)];
        }
        if(sketch.Splines.SingleOrDefault(s=>s.Id==curve) is not {} spline)
            throw new CadValidationException("Finite parametric contact supports lines, quadratic Beziers and clamped nonrational cubic author splines.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var definition=CubicSplineKnotVector.Definition(spline.Controls.Select(id=>points[id]).ToImmutableArray(),spline.Knots);
        for(int i=1;i<definition.Knots.Length-1;i++)
            if(definition.Multiplicities[i]>1&&Math.Abs((definition.Knots[i]-definition.Knots[0])/(definition.Knots[^1]-definition.Knots[0])-parameter)<=1e-12)
                throw new CadValidationException("Contact at a multiple internal knot requires an explicit one-sided derivative; select an unambiguous interior parameter.");
        var basis=definition.BasisDerivatives(parameter);
        return spline.Controls.Select((id,i)=>(id,basis[i].Position,basis[i].FirstDerivative,basis[i].SecondDerivative)).ToImmutableArray();
    }

    public static (Point2d Position,Point2d Tangent) Evaluate(CadSketch sketch,SketchEntityId curve,double parameter)
    {var g=EvaluateGeometry(sketch,curve,parameter);return(g.Position,g.Tangent);}

    /// <summary>Analytic jet for a moving G2 contact parameter. Rejects unproved third derivatives at knots.</summary>
    public static (Point2d Position,Point2d First,Point2d Second,Point2d Third) EvaluateThirdDerivatives(CadSketch sketch,SketchEntityId curve,double parameter)
    {
        if(!double.IsFinite(parameter)||parameter<=0||parameter>=1)
            throw InvalidParameterOrder("Finite contact parameters must be strictly inside (0,1).");
        if(sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==curve) is {} expression)
        {
            expression.Geometry.RequireDerivativeContinuity(parameter,3);
            var g=expression.Geometry.Evaluate(parameter);return(g.Point,g.First,g.Second,g.Third);
        }
        if(sketch.Ellipses.SingleOrDefault(c=>c.Id==curve) is {} ellipse)
            return(ellipse.Geometry.At(parameter),ellipse.Geometry.Derivative(parameter),ellipse.Geometry.SecondDerivative(parameter),ellipse.Geometry.ThirdDerivative(parameter));
        if(sketch.Freeforms.SingleOrDefault(c=>c.Id==curve) is {} freeform)
        {
            freeform.Geometry.RequireDerivativeContinuity(parameter,3);
            var g=freeform.Geometry.EvaluateThirdDerivatives(parameter);return(g.Point,g.First,g.Second,g.Third);
        }
        if(sketch.Circles.Any(c=>c.Id==curve)||sketch.Arcs.Any(c=>c.Id==curve))
        {
            var g=EvaluateDerivatives(sketch,curve,parameter);
            double sweep=sketch.Circles.Any(c=>c.Id==curve)?Math.Tau:ArcSweep();
            return(g.Position,g.First,g.Second,new(-g.First.X*sweep*sweep,-g.First.Y*sweep*sweep));
            double ArcSweep()
            {
                var arc=sketch.Arcs.Single(c=>c.Id==curve);var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
                return SketchArcGeometry.Through(points[arc.Start],points[arc.Middle],points[arc.End]).SweepAngle;
            }
        }
        var controls=ThirdDerivativeWeights(sketch,curve,parameter);var authored=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var origin=authored[controls[0].Point];Point2d position=origin,first=default,second=default,third=default;
        foreach(var w in controls)
        {
            var p=authored[w.Point];double dx=p.X-origin.X,dy=p.Y-origin.Y;
            position=new(position.X+w.Position*dx,position.Y+w.Position*dy);
            first=new(first.X+w.First*dx,first.Y+w.First*dy);
            second=new(second.X+w.Second*dx,second.Y+w.Second*dy);
            third=new(third.X+w.Third*dx,third.Y+w.Third*dy);
        }
        CadGuard.Finite(position.X,position.Y,first.X,first.Y,second.X,second.Y,third.X,third.Y);
        return(position,first,second,third);
    }

    public static ImmutableArray<(SketchEntityId Point,double Position,double First,double Second,double Third)> ThirdDerivativeWeights(CadSketch sketch,SketchEntityId curve,double parameter)
    {
        if(sketch.Splines.SingleOrDefault(s=>s.Id==curve) is not {} spline)
            return DerivativeWeights(sketch,curve,parameter).Select(w=>(w.Point,w.Position,w.First,w.Second,0d)).ToImmutableArray();
        if(!double.IsFinite(parameter)||parameter<=0||parameter>=1)
            throw InvalidParameterOrder("Finite contact parameters must be strictly inside (0,1).");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var definition=CubicSplineKnotVector.Definition(spline.Controls.Select(id=>points[id]).ToImmutableArray(),spline.Knots);
        for(int i=1;i<definition.Knots.Length-1;i++)
            if(Math.Abs((definition.Knots[i]-definition.Knots[0])/(definition.Knots[^1]-definition.Knots[0])-parameter)<=1e-12)
            {
                var error=new CadValidationException("A cubic author knot does not prove a continuous third derivative; select an unambiguous interior parameter.");
                error.Data["SketchContact.Message"]="SlidingContactDerivativeAmbiguous";throw error;
            }
        var basis=definition.BasisThirdDerivatives(parameter);
        return spline.Controls.Select((id,i)=>(id,basis[i].Position,basis[i].FirstDerivative,basis[i].SecondDerivative,basis[i].ThirdDerivative)).ToImmutableArray();
    }

    public static (Point2d Position,Point2d First,Point2d Second) EvaluateDerivatives(CadSketch sketch,SketchEntityId curve,double parameter,int order=1)
    {
        if(!double.IsFinite(parameter)||parameter<=0||parameter>=1||order is <1 or >2)
            throw InvalidParameterOrder("Finite contact requires an explicit interior parameter and derivative order.");
        if(sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==curve) is {} expression)
        {
            expression.Geometry.RequireDerivativeContinuity(parameter,order);
            var g=expression.Geometry.Evaluate(parameter);return(g.Point,g.First,g.Second);
        }
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        if(sketch.Ellipses.SingleOrDefault(c=>c.Id==curve) is {} ellipse)
            return(ellipse.Geometry.At(parameter),ellipse.Geometry.Derivative(parameter),ellipse.Geometry.SecondDerivative(parameter));
        if(sketch.Freeforms.SingleOrDefault(c=>c.Id==curve) is {} freeform)
        {freeform.Geometry.RequireDerivativeContinuity(parameter,order);var g=freeform.Geometry.EvaluateDerivatives(parameter);return(g.Point,g.First,g.Second);}
        if(sketch.Circles.SingleOrDefault(c=>c.Id==curve) is {} circle)
            return Circular(points[circle.Center],circle.Radius,0,Math.Tau);
        if(sketch.Arcs.SingleOrDefault(c=>c.Id==curve) is {} arc)
        {var g=SketchArcGeometry.Through(points[arc.Start],points[arc.Middle],points[arc.End]);return Circular(g.Center,g.Radius,g.StartAngle,g.SweepAngle);}
        (Point2d,Point2d,Point2d) Circular(Point2d center,double radius,double start,double sweep)
        {
            double a=start+parameter*sweep,c=Math.Cos(a),s=Math.Sin(a);
            return(new(center.X+radius*c,center.Y+radius*s),new(-radius*s*sweep,radius*c*sweep),new(-radius*c*sweep*sweep,-radius*s*sweep*sweep));
        }
        var weights=DerivativeWeights(sketch,curve,parameter);
        var origin=points[weights[0].Point];Point2d position=origin,first=default,second=default;
        foreach(var w in weights)
        {
            var p=points[w.Point];double dx=p.X-origin.X,dy=p.Y-origin.Y;
            position=new(position.X+w.Position*dx,position.Y+w.Position*dy);
            first=new(first.X+w.First*dx,first.Y+w.First*dy);
            second=new(second.X+w.Second*dx,second.Y+w.Second*dy);
        }
        return(position,first,second);
    }
    private static CadValidationException InvalidParameterOrder(string message)
    {
        var error=new CadValidationException(message);
        error.Data["SketchContact.Message"]="FiniteContactInvalidParameterOrder";
        return error;
    }
    public static (Point2d Position,Point2d Tangent,double CurvaturePerMm) EvaluateGeometry(CadSketch sketch,SketchEntityId curve,double parameter,int order=1)
    {
        var(position,first,second)=EvaluateDerivatives(sketch,curve,parameter,order);
        double speed=double.Hypot(first.X,first.Y);
        if(!double.IsFinite(speed)||speed<=1e-7)throw new CadValidationException("Finite contact derivative must exceed 1e-7 mm per normalized parameter.");
        double curvature=(first.X*second.Y-first.Y*second.X)/(speed*speed*speed);CadGuard.Finite(curvature);
        return(position,new(first.X/speed,first.Y/speed),curvature);
    }

    public static void RequireSolvedRelations(CadSketch sketch,double linearToleranceMm=1e-7,double angularTolerance=1e-9,double curvatureTolerancePerMm=1e-7)
    {
        CadGuard.Positive(linearToleranceMm,angularTolerance,curvatureTolerancePerMm);
        foreach(var c in sketch.Constraints.OfType<FiniteParametricContactConstraint>().Where(c=>c.IsEnabled))
        {
            ValidateTargets(sketch,c);
            var a=EvaluateGeometry(sketch,c.A,c.ParameterA,(int)c.Continuity);var b=EvaluateGeometry(sketch,c.B,c.ParameterB,(int)c.Continuity);
            double dot=a.Tangent.X*b.Tangent.X+a.Tangent.Y*b.Tangent.Y;
            if(Math.Abs(a.Position.X-b.Position.X)>linearToleranceMm||Math.Abs(a.Position.Y-b.Position.Y)>linearToleranceMm||
               Math.Abs(a.Tangent.X*b.Tangent.Y-a.Tangent.Y*b.Tangent.X)>angularTolerance||
               (c.Branch==FiniteContactTangentBranch.SameDirection?dot<=0:dot>=0)||
               c.Continuity==FiniteContactContinuity.Curvature&&Math.Abs(a.CurvaturePerMm-(c.Branch==FiniteContactTangentBranch.SameDirection?1:-1)*b.CurvaturePerMm)>curvatureTolerancePerMm)
            {
                var error=new CadValidationException("SKETCH.FINITE_CONTACT_OUTPUT");
                error.Data["SketchContact.Message"]="ContactBranchFailure";throw error;
            }
        }
    }
    public static void RequireUnchangedAuthors(CadSketch authored,CadSketch solved)
    {
        if(authored.Constraints.Length!=solved.Constraints.Length)throw new CadValidationException("Contact solver changed the constraint author table.");
        var output=solved.Constraints.ToDictionary(c=>c.Id);
        foreach(var source in authored.Constraints)
        {
            if(!output.TryGetValue(source.Id,out var actual))throw new CadValidationException("Contact solver changed a constraint identity.");
            var expected=source;
            if(source is FiniteParametricContactConstraint c&&actual is FiniteParametricContactConstraint value)
            {
                if(c.SolvesA)c.SlidingA!.Validate(value.ParameterA);
                if(c.SolvesB)c.SlidingB!.Validate(value.ParameterB);
                expected=c with{ParameterA=c.SolvesA?value.ParameterA:c.ParameterA,ParameterB=c.SolvesB?value.ParameterB:c.ParameterB};
            }
            if(expected!=actual)throw new CadValidationException("Contact solver changed a fixed parameter, branch, mode, seed, interval or constraint author.");
        }
    }
}
