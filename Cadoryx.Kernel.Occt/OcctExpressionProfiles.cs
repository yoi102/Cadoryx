using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private static SketchCurveExpressionGeometry OwnedExpression(ExactSketchCurve curve)
    {
        curve.Validate();if(curve.Expression is {} expression)return expression;
        if(curve.Arc is {} arc&&Math.Abs(Math.Abs(arc.SweepAngle)-Math.Tau)<=1e-12)
        {
            var circle=new SketchEllipseGeometry(arc.Center,new(arc.Radius,0),new(0,arc.Radius),arc.StartAngle,arc.SweepAngle);
            return SketchCurveExpressionGeometry.FromBoundary(new(curve.Start,curve.End){Ellipse=circle});
        }
        return SketchCurveExpressionGeometry.FromBoundary(new(curve.Start,curve.End,curve.Arc?.At(.5))
        {BezierControl=curve.Bezier?.Control,SplineControls=curve.Spline?.Controls??[],
            SplineKnots=curve.Spline is {} spline?CubicSplineKnotVector.From(spline):null,Ellipse=curve.Ellipse,Freeform=curve.Freeform});
    }
    internal static SketchExpressionChain2d ExpressionChain(IReadOnlyList<SketchBoundaryCurve> boundary,bool closed,bool canonicalExpressions=false)
    {
        if(boundary.Count is <1 or >256)throw new CadValidationException("An expression boundary needs 1 to 256 explicitly ordered curves.");
        var kernel=new OcctGeometryKernel();var curves=boundary.Select(c=>canonicalExpressions&&c.Expression is {} expression?
            GuidedExpression(expression):NativeExpressionCurve(kernel.PrepareExpression(c.Expression??OwnedExpression(ExactSketchCurve.FromBoundary(c)))).Curve).ToArray();
        var chain=SketchExpressionChain2d.CreateOrdered(curves,closed);
        if(chain.Curves.Count!=curves.Length||!chain.Curves.Select((c,i)=>ReferenceEquals(c,curves[i])).All(same=>same))
            throw new CadValidationException("Expression preparation changed the author order or direction.");
        return chain;
    }
    internal static Shape ExpressionFace(IReadOnlyList<SketchBoundaryCurve> boundary,bool canonicalExpressions=false)=>
        SketchExpressionProfile2d.Create(ExpressionChain(boundary,true,canonicalExpressions)).CreateFace(SketchPlane.XY);
}
