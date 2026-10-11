namespace Cadoryx.Db;

/// <summary>Periodicity comes from the copied author definition, never matching coordinates.</summary>
public static class SketchExpressionPeriod
{
    public static double? FullPeriod(SketchCurveExpressionGeometry curve)
    {
        curve.Validate();var root=curve;while(root.Basis is {} basis)root=basis;
        double? period=root.Primitive!.Ellipse is {} ellipse&&Math.Abs(Math.Abs(ellipse.SweepAngle)-Math.Tau)<=1e-12?Math.Tau:
            root.Primitive.Freeform is {Periodic:true} freeform?freeform.Knots[^1]-freeform.Knots[0]:null;
        if(period is not {} value)return null;
        for(var node=curve;node is not null;node=node.Basis)
            if(Math.Abs(node.ParameterSpan-value)>1e-12*value)return null;
        return value;
    }
}
