using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>A copied finite curve with exactly one explicit representation; it contains no native handles.</summary>
public sealed record ExactSketchCurve(Point2d Start,Point2d End,SketchArcGeometry? Arc=null,
    QuadraticBezierRegion? Bezier=null,CubicSplineDefinition? Spline=null,SketchEllipseGeometry? Ellipse=null,SketchFreeformGeometry? Freeform=null,SketchCurveExpressionGeometry? Expression=null)
{
    public void Validate()
    {
        CadGuard.Finite(Start.X,Start.Y,End.X,End.Y);
        if((Arc is null?0:1)+(Bezier is null?0:1)+(Spline is null?0:1)+(Ellipse is null?0:1)+(Freeform is null?0:1)+(Expression is null?0:1)>1)
            throw new CadValidationException("A curve must have exactly one geometry representation.");
        if(Expression is {} expression)
        {
            expression.Validate();
            if(double.Hypot(expression.At(0).X-Start.X,expression.At(0).Y-Start.Y)>1e-7||double.Hypot(expression.At(1).X-End.X,expression.At(1).Y-End.Y)>1e-7)
                throw new CadValidationException("Expression endpoints differ from its exact finite interval.");
        }
        else if(Freeform is {} freeform)
        {
            freeform.Validate();
            if(double.Hypot(freeform.At(0).X-Start.X,freeform.At(0).Y-Start.Y)>1e-7||
               double.Hypot(freeform.At(1).X-End.X,freeform.At(1).Y-End.Y)>1e-7)
                throw new CadValidationException("Freeform endpoints differ from its finite interval.");
        }
        else if(Ellipse is {} ellipse)
        {
            ellipse.Validate();
            if(double.Hypot(ellipse.At(0).X-Start.X,ellipse.At(0).Y-Start.Y)>1e-7||
               double.Hypot(ellipse.At(1).X-End.X,ellipse.At(1).Y-End.Y)>1e-7)
                throw new CadValidationException("Ellipse endpoints differ from its finite interval.");
        }
        else if(Spline is {} spline)
        {
            spline.Validate();if(spline.Controls[0]!=Start||spline.Controls[^1]!=End)
                throw new CadValidationException("Clamped spline endpoints must match its controls.");
        }
        else if(Bezier is {} bezier)
        {
            CadGuard.Finite(bezier.Control.X,bezier.Control.Y);
            if(bezier.Start!=Start||bezier.End!=End||Start==End&&Start==bezier.Control)
                throw new CadValidationException("Invalid quadratic curve definition.");
        }
        else if(Arc is {} arc)
        {
            CadGuard.Finite(arc.Center.X,arc.Center.Y,arc.StartAngle,arc.SweepAngle);CadGuard.Positive(arc.Radius);
            if(Math.Abs(arc.SweepAngle)<=1e-12||Math.Abs(arc.SweepAngle)>Math.Tau+1e-12||
               double.Hypot(arc.At(0).X-Start.X,arc.At(0).Y-Start.Y)>1e-7||
               double.Hypot(arc.At(1).X-End.X,arc.At(1).Y-End.Y)>1e-7)
                throw new CadValidationException("Invalid circular curve interval.");
        }
        else if(double.Hypot(End.X-Start.X,End.Y-Start.Y)<=1e-7)
            throw new CadValidationException("A segment must have nonzero length.");
    }
    public static ExactSketchCurve Cubic(CubicSplineDefinition curve)=>new(curve.Controls[0],curve.Controls[^1],Spline:curve);
    public static ExactSketchCurve FromBoundary(SketchBoundaryCurve edge)
    {
        if(edge.SplineControls.IsDefault)throw new CadValidationException("Uninitialized exact boundary controls.");
        var result=new ExactSketchCurve(edge.Start,edge.End,
            edge.Middle is {} middle?SketchArcGeometry.Through(edge.Start,middle,edge.End):null,
            edge.BezierControl is {} control?new(edge.Start,control,edge.End):null,
            edge.SplineControls.IsEmpty?null:edge.SplineDefinition,edge.Ellipse,edge.Freeform,edge.Expression);
        if(edge.SplineKnots is not null&&edge.SplineControls.IsEmpty)throw new CadValidationException("Spline knots require controls.");
        result.Validate();return result;
    }
}
public readonly record struct ExactSketchEvaluation(Point2d Point,Point2d NormalizedDerivative);
public readonly record struct ExactSketchProjection(Point2d Point,double Parameter,double Distance);
public readonly record struct ExactSketchIntersection(Point2d Point,double FirstParameter,double SecondParameter,bool IsTangent);
public sealed record ExactRegionInspection(double Area,int EdgeCount);
public enum ExactRegionRelation { Disjoint,Contains,Contained,ContactOrOverlap }
public interface IExactSketchRegionRelations
{
    ExactRegionRelation CompareRegions(ImmutableArray<SketchBoundaryCurve> first,ImmutableArray<SketchBoundaryCurve> second);
}

/// <summary>Exact synchronous bounded curve operations. Callers own cancellation and atomic author edits.</summary>
public interface IExactSketchGeometry
{
    ExactSketchEvaluation Evaluate(ExactSketchCurve curve,double parameter);
    ImmutableArray<ExactSketchProjection> Project(ExactSketchCurve curve,Point2d point);
    ImmutableArray<ExactSketchIntersection> Intersect(ExactSketchCurve first,ExactSketchCurve second);
    ExactRegionInspection InspectRegion(ImmutableArray<ExactSketchCurve> directedBoundary);
}
