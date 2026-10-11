using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Taylor coefficients, not numerical differences. Higher primitive orders required
/// by nested normals are evaluated on the original homogeneous author controls.</summary>
internal static class SketchExpressionTaylor
{
    internal static (double First,double Last,bool Reversed) PrimitiveDomain(SketchBoundaryCurve c)
    {
        if(c is null||c.SplineControls.IsDefault||c.Expression is not null)throw new CadValidationException("An expression leaf needs a complete primitive, never an embedded expression boundary.");
        CadGuard.Finite(c.Start.X,c.Start.Y,c.End.X,c.End.Y);
        if((c.Middle is null?0:1)+(c.BezierControl is null?0:1)+(c.SplineControls.IsEmpty?0:1)+(c.Ellipse is null?0:1)+(c.Freeform is null?0:1)>1||
            c.SplineKnots is not null&&c.SplineControls.IsEmpty)
            throw new CadValidationException("An expression primitive needs exactly one curve representation.");
        bool Near(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y)<=1e-7;
        if(c.Freeform is {} freeform)
        {
            freeform.Validate();if(!Near(c.Start,freeform.At(0))||!Near(c.End,freeform.At(1)))
                throw new CadValidationException("Expression freeform endpoints differ from the copied author interval.");
            return(freeform.FirstParameter,freeform.LastParameter,freeform.Reversed);
        }
        if(c.Ellipse is {} ellipse)
        {
            ellipse.Validate();if(!Near(c.Start,ellipse.At(0))||!Near(c.End,ellipse.At(1)))
                throw new CadValidationException("Expression ellipse endpoints differ from the copied author interval.");
            return(Math.Min(ellipse.StartAngle,ellipse.StartAngle+ellipse.SweepAngle),Math.Max(ellipse.StartAngle,ellipse.StartAngle+ellipse.SweepAngle),ellipse.SweepAngle<0);
        }
        if(c.Middle is {} middle)
        {
            var arc=SketchArcGeometry.Through(c.Start,middle,c.End);
            return(Math.Min(arc.StartAngle,arc.StartAngle+arc.SweepAngle),Math.Max(arc.StartAngle,arc.StartAngle+arc.SweepAngle),arc.SweepAngle<0);
        }
        if(c.BezierControl is {} control){SketchBezierGeometry.Validate(c.Start,control,c.End);return(0,1,false);}
        if(!c.SplineControls.IsEmpty){var s=c.SplineDefinition;s.Validate();if(!Near(c.Start,s.Controls[0])||!Near(c.End,s.Controls[^1]))throw new CadValidationException("Expression spline endpoints differ from its author controls.");return(s.Knots[0],s.Knots[^1],false);}
        double length=double.Hypot(c.End.X-c.Start.X,c.End.Y-c.Start.Y);
        if(!double.IsFinite(length)||length<=1e-7)throw new CadValidationException("An expression segment needs nonzero length.");
        return(0,length,false);
    }
    internal static SketchFreeformGeometry? Freeform(SketchBoundaryCurve c)
    {
        if(c.Freeform is {} freeform)return freeform;
        if(c.BezierControl is {} control)return new(SketchFreeformKind.Bezier,[c.Start,control,c.End],[],[],[],2,false,0,1);
        if(c.SplineControls.IsEmpty)return null;
        var s=c.SplineDefinition;return new(SketchFreeformKind.BSpline,s.Controls,[],s.Knots,s.Multiplicities,3,false,s.Knots[0],s.Knots[^1]);
    }
    private static Point2d Add(Point2d a,Point2d b)=>new(a.X+b.X,a.Y+b.Y);
    private static Point2d Mul(Point2d a,double b)=>new(a.X*b,a.Y*b);
    internal static Point2d[] Evaluate(SketchCurveExpressionGeometry node,double u,int order,bool upperBoundary)
    {
        if(order>67)throw new CadValidationException("Expression derivative order exceeds its 64-node resource bound.");
        if(node.Primitive is {} primitive)return Primitive(primitive,u,order,upperBoundary);
        var basis=node.Basis!;
        if(node.Kind==SketchCurveExpressionKind.Continuation)return Evaluate(basis,u,order,upperBoundary);
        var values=Evaluate(basis,u,order+1,upperBoundary);var velocity=new Point2d[order+1];
        var squared=new double[order+1];var speed=new double[order+1];var inverse=new double[order+1];
        for(int k=0;k<=order;k++)velocity[k]=Mul(values[k+1],k+1);
        for(int k=0;k<=order;k++)for(int j=0;j<=k;j++)squared[k]+=velocity[j].X*velocity[k-j].X+velocity[j].Y*velocity[k-j].Y;
        speed[0]=Math.Sqrt(squared[0]);
        if(!double.IsFinite(speed[0])||speed[0]<=0)throw new CadValidationException("An exact offset has a singular basis tangent.");
        inverse[0]=1/speed[0];
        for(int k=1;k<=order;k++)
        {
            double remainder=squared[k];for(int j=1;j<k;j++)remainder-=speed[j]*speed[k-j];speed[k]=remainder/(2*speed[0]);
            for(int j=1;j<=k;j++)inverse[k]-=speed[j]*inverse[k-j]/speed[0];
        }
        double distance=node.SignedOffset*(basis.Reversed?-1:1);var result=new Point2d[order+1];
        for(int k=0;k<=order;k++)
        {
            var normal=new Point2d();for(int j=0;j<=k;j++)normal=Add(normal,Mul(new(velocity[j].Y,-velocity[j].X),inverse[k-j]));
            result[k]=Add(values[k],Mul(normal,distance));CadGuard.Finite(result[k].X,result[k].Y);
        }
        return result;
    }
    private static Point2d[] Primitive(SketchBoundaryCurve c,double u,int order,bool upperBoundary)
    {
        if(Freeform(c) is {} freeform)return Rational(freeform,u,order,upperBoundary);
        var result=new Point2d[order+1];
        Point2d center,cosine,sine;
        if(c.Ellipse is {} ellipse){center=ellipse.Center;cosine=ellipse.Cosine;sine=ellipse.Sine;}
        else if(c.Middle is {} middle){var arc=SketchArcGeometry.Through(c.Start,middle,c.End);center=arc.Center;cosine=new(arc.Radius,0);sine=new(0,arc.Radius);}
        else
        {
            double length=double.Hypot(c.End.X-c.Start.X,c.End.Y-c.Start.Y);var direction=new Point2d((c.End.X-c.Start.X)/length,(c.End.Y-c.Start.Y)/length);
            result[0]=Add(c.Start,Mul(direction,u));if(order>0)result[1]=direction;return result;
        }
        double cos=Math.Cos(u),sin=Math.Sin(u),factorial=1;
        for(int k=0;k<=order;k++)
        {
            if(k>0)factorial*=k;
            (double a,double b)=(k%4) switch{0=>(cos,sin),1=>(-sin,cos),2=>(-cos,-sin),_=>(sin,-cos)};
            result[k]=Mul(Add(Mul(cosine,a),Mul(sine,b)),1/factorial);
        }
        result[0]=Add(result[0],center);return result;
    }
    private readonly record struct H(double X,double Y,double W)
    {
        internal static H Blend(H a,H b,double alpha,H da,H db,double slope)=>
            new(a.X+alpha*(b.X-a.X)+slope*(db.X-da.X),a.Y+alpha*(b.Y-a.Y)+slope*(db.Y-da.Y),a.W+alpha*(b.W-a.W)+slope*(db.W-da.W));
    }
    private static Point2d[] Rational(SketchFreeformGeometry g,double u,int order,bool upperBoundary)
    {
        var origin=g.Poles[0];var poles=g.Poles.Select((p,i)=>{double w=g.Weights.IsEmpty?1:g.Weights[i];return new H((p.X-origin.X)*w,(p.Y-origin.Y)*w,w);}).ToArray();
        H[] homogeneous;
        if(g.Kind==SketchFreeformKind.Bezier)
        {
            var values=poles.Select(p=>{var v=new H[order+1];v[0]=p;return v;}).ToArray();
            for(int remaining=g.Degree;remaining>0;remaining--)for(int j=0;j<remaining;j++)
                for(int k=order;k>=0;k--)values[j][k]=H.Blend(values[j][k],values[j+1][k],u,k>0?values[j][k-1]:default,k>0?values[j+1][k-1]:default,k>0?1:0);
            homogeneous=values[0];
        }
        else
        {
            var full=g.Knots.Zip(g.Multiplicities).SelectMany(k=>Enumerable.Repeat(k.First,k.Second)).ToArray();
            if(g.Periodic)
            {
                double period=g.Knots[^1]-g.Knots[0];double offset=(u-g.Knots[0])%period;if(offset<0)offset+=period;
                u=offset==0&&upperBoundary?g.Knots[^1]:g.Knots[0]+offset;
                int extra=g.Degree+1-g.Multiplicities[0];var cycle=full[..^g.Multiplicities[^1]];int count=cycle.Length;
                double CyclicKnot(int index)=>cycle[(index%count+count)%count]+Math.Floor(index/(double)count)*period;
                full=Enumerable.Range(-extra,count+g.Multiplicities[^1]+2*extra).Select(CyclicKnot).ToArray();
                poles=Enumerable.Range(0,full.Length-g.Degree-1).Select(i=>poles[i%poles.Length]).ToArray();
            }
            int span=g.Degree;while(span<poles.Length-1&&u>=full[span+1])span++;
            var values=Enumerable.Range(0,g.Degree+1).Select(j=>{var v=new H[order+1];v[0]=poles[span-g.Degree+j];return v;}).ToArray();
            for(int level=1;level<=g.Degree;level++)for(int j=g.Degree;j>=level;j--)
            {
                int i=span-g.Degree+j;double width=full[i+g.Degree-level+1]-full[i];
                // Zero knot intervals carry no polar blend, including exact continuation at repeated knots.
                double alpha=width==0?0:(u-full[i])/width,slope=width==0?0:1/width;
                for(int k=order;k>=0;k--)values[j][k]=H.Blend(values[j-1][k],values[j][k],alpha,k>0?values[j-1][k-1]:default,k>0?values[j][k-1]:default,k>0?slope:0);
            }
            homogeneous=values[^1];
        }
        if(!double.IsFinite(homogeneous[0].W)||homogeneous[0].W<=0)throw new CadValidationException("An exact author continuation has a singular rational denominator.");
        var result=new Point2d[order+1];
        for(int k=0;k<=order;k++)
        {
            var value=new Point2d(homogeneous[k].X,homogeneous[k].Y);
            for(int j=1;j<=k;j++)value=Add(value,Mul(result[k-j],-homogeneous[j].W));
            result[k]=Mul(value,1/homogeneous[0].W);CadGuard.Finite(result[k].X,result[k].Y);
        }
        result[0]=Add(result[0],origin);return result;
    }
}
