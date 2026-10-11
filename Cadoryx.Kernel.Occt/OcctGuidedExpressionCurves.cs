using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    // Algebraic representation changes only. This never fits a curve or replaces stored author values.
    private static SketchCurveExpression2d GuidedExpression(SketchCurveExpressionGeometry expression)
    {
        var kernel=new OcctGeometryKernel();kernel.PrepareExpression(expression);
        return CanonicalExpressionCurve(expression) is {} canonical?SketchCurveExpression2d.FromCurve(canonical):NativeExpressionCurve(expression).Curve;
    }
    private static SketchCurve2d? CanonicalExpressionCurve(SketchCurveExpressionGeometry expression)
    {
        expression.Validate();
        var nodes=new List<SketchCurveExpressionGeometry>();for(var node=expression;node is not null;node=node.Basis)nodes.Add(node);nodes.Reverse();
        var primitive=nodes[0].Primitive!;Point2d center;double radius,orientation=1,phase=0;
        if(primitive.Middle is {} middle)
        {var arc=SketchArcGeometry.Through(primitive.Start,middle,primitive.End);center=arc.Center;radius=arc.Radius;}
        else if(primitive.Ellipse is {} ellipse&&
            (ellipse.Cosine.Y==0&&ellipse.Sine.X==0&&Math.Abs(ellipse.Cosine.X)==Math.Abs(ellipse.Sine.Y)||
             ellipse.Cosine.X==0&&ellipse.Sine.Y==0&&Math.Abs(ellipse.Cosine.Y)==Math.Abs(ellipse.Sine.X)))
        {
            center=ellipse.Center;radius=double.Hypot(ellipse.Cosine.X,ellipse.Cosine.Y);
            orientation=Math.Sign(ellipse.Cosine.X*ellipse.Sine.Y-ellipse.Cosine.Y*ellipse.Sine.X);phase=Math.Atan2(ellipse.Cosine.Y,ellipse.Cosine.X);
        }
        else
        {
            if(primitive.Freeform is null&&primitive.Ellipse is null&&primitive.Middle is null&&primitive.BezierControl is null&&primitive.SplineControls.IsEmpty)
            {
                var lineStart=expression.At(0);var lineEnd=expression.At(1);
                return SketchCurve2d.Segment(new(lineStart.X,lineStart.Y),new(lineEnd.X,lineEnd.Y));
            }
            return null;
        }
        foreach(var node in nodes.Skip(1))if(node.Kind==Cadoryx.Db.SketchCurveExpressionKind.Offset)
            radius+=node.SignedOffset*orientation*(node.Basis!.Reversed?-1:1);
        if(radius<=0)throw new CadValidationException("A canonical guided circle has nonpositive radius.");
        double start=orientation*(expression.Reversed?expression.LastParameter:expression.FirstParameter)+phase;
        double sweep=orientation*expression.ParameterSpan*(expression.Reversed?-1:1);
        return SketchCurve2d.CircularArc(new(center.X,center.Y),radius,start,sweep);
    }
    private static SketchExpressionChain2d GuidedExpressionChain(IReadOnlyList<SketchBoundaryCurve> boundary,bool closed)
    {
        if(boundary.Count is <1 or >256)throw new CadValidationException("A guided expression boundary exceeds the author curve budget.");
        var curves=boundary.Select(c=>c.Expression is {} expression?GuidedExpression(expression):SketchCurveExpression2d.FromCurve(DirectedCurve(c))).ToArray();
        var chain=SketchExpressionChain2d.CreateOrdered(curves,closed);
        if(chain.Curves.Count!=curves.Length||!chain.Curves.Select((c,i)=>ReferenceEquals(c,curves[i])).All(same=>same))
            throw new CadValidationException("Guided canonicalization changed the supplied author order.");
        return chain;
    }
}
