using Cadoryx.Db;
using Scalar=Cadoryx.Sketching.SketchContactDifferentials.Scalar;
using Vector=Cadoryx.Sketching.SketchContactDifferentials.Vector;

namespace Cadoryx.Sketching;

internal sealed partial class SketchExpressionDifferentials
{
    private Vector[] Jet(Node node,Scalar u,int order,bool upper)
    {
        if(order>67)throw new CadValidationException("Expression differential Taylor order exceeds the author64-node bound.");
        if(node.Primitive is {} primitive)return primitive(u,order,upper);
        var basis=node.Basis!;if(node.Kind==SketchCurveExpressionKind.Continuation)return Jet(basis,u,order,upper);
        var value=Jet(basis,u,Math.Max(2,order+1),upper);
        var velocity=Enumerable.Range(0,order+1).Select(k=>value[k+1]*(k+1)).ToArray();
        var square=Enumerable.Range(0,order+1).Select(_=>C(0)).ToArray();
        var speed=Enumerable.Range(0,order+1).Select(_=>C(0)).ToArray();
        var inverse=Enumerable.Range(0,order+1).Select(_=>C(0)).ToArray();
        for(int k=0;k<=order;k++)for(int j=0;j<=k;j++)square[k]+=velocity[j].X*velocity[k-j].X+velocity[j].Y*velocity[k-j].Y;
        speed[0]=Scalar.Sqrt(square[0]);Finite(speed[0]);
        if(speed[0].Value<=0)throw new CadValidationException("Expression differential offset has a singular source tangent.");
        inverse[0]=C(1)/speed[0];
        for(int k=1;k<=order;k++)
        {
            var remainder=square[k];for(int j=1;j<k;j++)remainder-=speed[j]*speed[k-j];speed[k]=remainder/(speed[0]*2);
            for(int j=1;j<=k;j++)inverse[k]-=speed[j]*inverse[k-j]/speed[0];
        }
        double distance=node.Distance*(basis.Reversed?-1:1);
        var curvature=(value[1].X*value[2].Y-value[1].Y*value[2].X)*2/(speed[0]*speed[0]*speed[0]);
        var tangentialScale=curvature*distance+1;Finite(tangentialScale);
        if(tangentialScale.Value<=0)throw new CadValidationException("An intermediate expression differential offset folds or reverses its source tangent.");
        var result=new Vector[order+1];
        for(int k=0;k<=order;k++)
        {
            var normal=Zero();for(int j=0;j<=k;j++)normal+=new Vector(velocity[j].Y,-velocity[j].X)*inverse[k-j];
            result[k]=value[k]+normal*distance;Finite(result[k].X);Finite(result[k].Y);
        }
        return result;
    }
    private readonly record struct H(Scalar X,Scalar Y,Scalar W)
    {
        internal static H Blend(H a,H b,Scalar alpha,H da,H db,double slope)=>
            new(a.X+alpha*(b.X-a.X)+(db.X-da.X)*slope,a.Y+alpha*(b.Y-a.Y)+(db.Y-da.Y)*slope,a.W+alpha*(b.W-a.W)+(db.W-da.W)*slope);
    }
    private H EmptyH()=>new(C(0),C(0),C(0));
    private Vector[] Rational(SketchFreeformGeometry geometry,Vector[] controls,Scalar u,int order,bool upper)
    {
        var anchor=controls[0];var poles=controls.Select((p,i)=>
        {double weight=geometry.Weights.IsEmpty?1:geometry.Weights[i];return new H((p.X-anchor.X)*weight,(p.Y-anchor.Y)*weight,C(weight));}).ToArray();
        H[] Values(H point){var values=Enumerable.Range(0,order+1).Select(_=>EmptyH()).ToArray();values[0]=point;return values;}
        H[] homogeneous;
        if(geometry.Kind==SketchFreeformKind.Bezier)
        {
            var values=poles.Select(Values).ToArray();
            for(int remaining=geometry.Degree;remaining>0;remaining--)for(int j=0;j<remaining;j++)for(int k=order;k>=0;k--)
                values[j][k]=H.Blend(values[j][k],values[j+1][k],u,k>0?values[j][k-1]:EmptyH(),k>0?values[j+1][k-1]:EmptyH(),k>0?1:0);
            homogeneous=values[0];
        }
        else
        {
            var full=geometry.Knots.Zip(geometry.Multiplicities).SelectMany(k=>Enumerable.Repeat(k.First,k.Second)).ToArray();
            if(geometry.Periodic)
            {
                double period=geometry.Knots[^1]-geometry.Knots[0];double shift=Math.Floor((u.Value-geometry.Knots[0])/period);
                var wrapped=u-shift*period;
                if(wrapped.Value==geometry.Knots[0]&&upper)wrapped+=period;
                u=wrapped;
                int extra=geometry.Degree+1-geometry.Multiplicities[0];var cycle=full[..^geometry.Multiplicities[^1]];int count=cycle.Length;
                double Knot(int i)=>cycle[(i%count+count)%count]+Math.Floor(i/(double)count)*period;
                full=Enumerable.Range(-extra,count+geometry.Multiplicities[^1]+2*extra).Select(Knot).ToArray();
                poles=Enumerable.Range(0,full.Length-geometry.Degree-1).Select(i=>poles[i%poles.Length]).ToArray();
            }
            int span=geometry.Degree;while(span<poles.Length-1&&u.Value>=full[span+1])span++;
            var values=Enumerable.Range(0,geometry.Degree+1).Select(j=>Values(poles[span-geometry.Degree+j])).ToArray();
            for(int level=1;level<=geometry.Degree;level++)for(int j=geometry.Degree;j>=level;j--)
            {
                int i=span-geometry.Degree+j;double width=full[i+geometry.Degree-level+1]-full[i];
                var alpha=width==0?C(0):(u-full[i])/width;double slope=width==0?0:1/width;
                for(int k=order;k>=0;k--)values[j][k]=H.Blend(values[j-1][k],values[j][k],alpha,
                    k>0?values[j-1][k-1]:EmptyH(),k>0?values[j][k-1]:EmptyH(),k>0?slope:0);
            }
            homogeneous=values[^1];
        }
        Finite(homogeneous[0].W);if(homogeneous[0].W.Value<=0)throw new CadValidationException("Expression differential has a singular rational denominator.");
        var result=new Vector[order+1];
        for(int k=0;k<=order;k++)
        {
            var value=new Vector(homogeneous[k].X,homogeneous[k].Y);
            for(int j=1;j<=k;j++)value-=result[k-j]*homogeneous[j].W;
            result[k]=value/homogeneous[0].W;Finite(result[k].X);Finite(result[k].Y);
        }
        result[0]+=anchor;return result;
    }
}
