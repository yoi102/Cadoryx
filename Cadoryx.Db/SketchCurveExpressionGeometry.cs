using System.Text.Json.Serialization;

namespace Cadoryx.Db;

public enum SketchCurveExpressionKind { Primitive=1,Offset=2,Continuation=3 }

/// <summary>Owned author values in the primitive's native parameter units. Reversal changes
/// traversal only. Each offset follows the right normal of its directed Basis; no native handle,
/// fitted curve, display polyline or inferred topology identity is stored here.</summary>
public sealed record SketchCurveExpressionGeometry(SketchCurveExpressionKind Kind,SketchBoundaryCurve? Primitive,
    SketchCurveExpressionGeometry? Basis,double SignedOffset,double FirstParameter,double LastParameter,bool Reversed)
{
    public const int MaximumDepth=64;
    [JsonIgnore] public double ParameterSpan=>LastParameter-FirstParameter;
    public override string ToString()=>$"{Kind} [{FirstParameter:R}, {LastParameter:R}], reversed={Reversed}";

    public static SketchCurveExpressionGeometry FromBoundary(SketchBoundaryCurve primitive)
    {
        var(first,last,reversed)=SketchExpressionTaylor.PrimitiveDomain(primitive);
        var value=new SketchCurveExpressionGeometry(SketchCurveExpressionKind.Primitive,primitive,null,0,first,last,reversed);
        value.Validate();return value;
    }
    public SketchCurveExpressionGeometry Offset(double signedDistance)
    {
        var value=new SketchCurveExpressionGeometry(SketchCurveExpressionKind.Offset,null,this,signedDistance,FirstParameter,LastParameter,Reversed);
        value.Validate();return value;
    }
    public SketchCurveExpressionGeometry Reverse()=>this with{Reversed=!Reversed};
    public SketchCurveExpressionGeometry Trim(double firstNormalized,double lastNormalized)
    {
        Validate();
        if(!double.IsFinite(firstNormalized)||!double.IsFinite(lastNormalized)||firstNormalized<0||lastNormalized>1||firstNormalized>=lastNormalized)
            throw new CadValidationException("An expression trim needs an increasing directed interval in [0,1].");
        // Exact endpoint selections retain the authored finite bounds. Subtracting
        // the span from LastParameter can round below FirstParameter.
        double first=Reversed?(lastNormalized==1?FirstParameter:LastParameter-lastNormalized*ParameterSpan):
            firstNormalized==0?FirstParameter:FirstParameter+firstNormalized*ParameterSpan;
        double last=Reversed?(firstNormalized==0?LastParameter:LastParameter-firstNormalized*ParameterSpan):
            lastNormalized==1?LastParameter:FirstParameter+lastNormalized*ParameterSpan;
        var value=this with{FirstParameter=first,LastParameter=last};value.Validate();return value;
    }
    public SketchCurveExpressionGeometry ContinueNativeRange(double first,double last)
    {
        Validate();
        if(first>FirstParameter||last<LastParameter)throw new CadValidationException("Continuation must include the existing finite interval.");
        var value=new SketchCurveExpressionGeometry(SketchCurveExpressionKind.Continuation,null,this,0,first,last,Reversed);
        value.Validate();return value;
    }
    public void Validate()
    {
        var seen=new HashSet<SketchCurveExpressionGeometry>(ReferenceEqualityComparer.Instance);
        for(var node=this;node is not null;node=node.Basis)
        {
            if(!seen.Add(node)||seen.Count>MaximumDepth)throw new CadValidationException("Expression basis cycles or exceeds the 64-node bound.");
            CadGuard.Finite(node.FirstParameter,node.LastParameter,node.SignedOffset);
            if(node.FirstParameter>=node.LastParameter||!double.IsFinite(node.ParameterSpan))
                throw new CadValidationException("An expression needs a finite increasing native interval.");
            if(node.Kind==SketchCurveExpressionKind.Primitive)
            {
                if(node.Primitive is null||node.Basis is not null||node.SignedOffset!=0)
                    throw new CadValidationException("An expression primitive must contain one complete author definition.");
                var(first,last,_)=SketchExpressionTaylor.PrimitiveDomain(node.Primitive);
                if(node.FirstParameter<first||node.LastParameter>last)
                    throw new CadValidationException("A primitive interval lies outside its copied finite definition.");
            }
            else if(node.Kind is SketchCurveExpressionKind.Offset or SketchCurveExpressionKind.Continuation)
            {
                if(node.Primitive is not null||node.Basis is null||node.Kind==SketchCurveExpressionKind.Continuation&&node.SignedOffset!=0)
                    throw new CadValidationException("An expression operation needs exactly one complete basis.");
                if(node.Kind==SketchCurveExpressionKind.Offset&&
                    (node.FirstParameter<node.Basis.FirstParameter||node.LastParameter>node.Basis.LastParameter))
                    throw new CadValidationException("An offset cannot evaluate outside its finite basis; explicitly continue first.");
            }
            else throw new CadValidationException("Unknown exact curve expression kind.");
        }
    }
    public bool SameValues(SketchCurveExpressionGeometry other)
    {
        Validate();other.Validate();var a=this;var b=other;
        while(true)
        {
            if(a.Kind!=b.Kind||a.SignedOffset!=b.SignedOffset||a.FirstParameter!=b.FirstParameter||a.LastParameter!=b.LastParameter||a.Reversed!=b.Reversed)
                return false;
            if(a.Primitive is {} primitive)return b.Primitive is {} otherPrimitive&&SketchMixedProfile.Same(primitive,otherPrimitive);
            if(b.Basis is null)return false;a=a.Basis!;b=b.Basis;
        }
    }
    /// <summary>Analytic derivatives in directed normalized units. This point query is not a
    /// whole-domain regularity or topology proof; contact callers must require continuity.</summary>
    public SketchExpressionEvaluation Evaluate(double normalizedParameter)
    {
        Validate();
        if(!double.IsFinite(normalizedParameter)||normalizedParameter<0||normalizedParameter>1)
            throw new CadValidationException("Curve parameter must lie in [0,1].");
        double native=normalizedParameter==0?(Reversed?LastParameter:FirstParameter):
            normalizedParameter==1?(Reversed?FirstParameter:LastParameter):
            FirstParameter+ParameterSpan*(Reversed?1-normalizedParameter:normalizedParameter);
        var values=SketchExpressionTaylor.Evaluate(this,native,3,native==LastParameter);
        double scale=ParameterSpan*(Reversed?-1:1);
        Point2d Derivative(int n)
        {
            double factor=(n==3?6:n==2?2:1)*Math.Pow(scale,n);
            var p=new Point2d(values[n].X*factor,values[n].Y*factor);CadGuard.Finite(p.X,p.Y);return p;
        }
        CadGuard.Finite(values[0].X,values[0].Y);
        return new(values[0],Derivative(1),Derivative(2),Derivative(3));
    }
    public Point2d At(double normalizedParameter)=>Evaluate(normalizedParameter).Point;
    public void RequireDerivativeContinuity(double normalizedParameter,int order)
    {
        Validate();
        if(order is <1 or >3||!double.IsFinite(normalizedParameter)||normalizedParameter<=0||normalizedParameter>=1)
            throw new CadValidationException("Continuity needs an interior directed parameter and derivative order from one to three.");
        double native=FirstParameter+ParameterSpan*(Reversed?1-normalizedParameter:normalizedParameter);
        int required=order;var node=this;
        while(node.Basis is {} basis){if(node.Kind==SketchCurveExpressionKind.Offset)required++;node=basis;}
        var primitive=node.Primitive!;
        var freeform=SketchExpressionTaylor.Freeform(primitive);
        if(freeform is not {Kind:SketchFreeformKind.BSpline})return;
        double u=native;
        if(freeform.Periodic)
        {
            double period=freeform.Knots[^1]-freeform.Knots[0];double shift=(u-freeform.Knots[0])%period;
            if(shift<0)shift+=period;u=freeform.Knots[0]+shift;
        }
        for(int i=0;i<freeform.Knots.Length;i++)
        {
            if(!freeform.Periodic&&(i==0||i==freeform.Knots.Length-1))continue;
            double distance=Math.Abs(u-freeform.Knots[i]);
            if(freeform.Periodic&&i==0)distance=Math.Min(distance,Math.Abs(u-freeform.Knots[^1]));
            if(distance/ParameterSpan<=1e-12&&freeform.Degree-freeform.Multiplicities[i]<required)
            {
                var error=new CadValidationException("Expression derivative is ambiguous at the source knot or periodic seam.");
                error.Data["SketchContact.Message"]="SlidingContactDerivativeAmbiguous";throw error;
            }
        }
    }
}

public readonly record struct SketchExpressionEvaluation(Point2d Point,Point2d First,Point2d Second,Point2d Third);
