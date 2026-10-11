using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum SketchFreeformKind { Bezier=1,BSpline=2 }

/// <summary>Owned rational geometry, including the original finite native interval and direction.</summary>
public sealed record SketchFreeformGeometry(SketchFreeformKind Kind,ImmutableArray<Point2d> Poles,
    ImmutableArray<double> Weights,ImmutableArray<double> Knots,ImmutableArray<int> Multiplicities,
    int Degree,bool Periodic,double FirstParameter,double LastParameter,bool Reversed=false)
{
    public const int MaximumPoles=1024;
    public const int MaximumPeriodShift=1024;
    public void Validate()
    {
        if(Kind is not (SketchFreeformKind.Bezier or SketchFreeformKind.BSpline)||Poles.IsDefault||
           Poles.Length is <2 or >MaximumPoles||Weights.IsDefault||Knots.IsDefault||Multiplicities.IsDefault||
           Degree is <1 or >25||Weights.Length!=0&&Weights.Length!=Poles.Length)
            throw new CadValidationException("Invalid copied freeform curve tables.");
        foreach(var pole in Poles)CadGuard.Finite(pole.X,pole.Y);
        foreach(double weight in Weights)CadGuard.Positive(weight);
        CadGuard.Finite(FirstParameter,LastParameter);
        if(FirstParameter>=LastParameter||!double.IsFinite(LastParameter-FirstParameter))
            throw new CadValidationException("A freeform curve needs a finite increasing native interval.");
        if(Kind==SketchFreeformKind.Bezier)
        {
            if(Periodic||Poles.Length!=Degree+1||!Knots.IsEmpty||!Multiplicities.IsEmpty||FirstParameter<0||LastParameter>1)
                throw new CadValidationException("Invalid Bezier degree or interval.");
        }
        else
        {
            if(Knots.Length<2||Knots.Length>MaximumPoles+26||Knots.Length!=Multiplicities.Length)
                throw new CadValidationException("Invalid freeform knot tables.");
            long total=0;
            for(int i=0;i<Knots.Length;i++)
            {
                int maximum=!Periodic&&(i==0||i==Knots.Length-1)?Degree+1:Degree;
                if(!double.IsFinite(Knots[i])||i>0&&Knots[i]<=Knots[i-1]||Multiplicities[i]<1||Multiplicities[i]>maximum)
                    throw new CadValidationException("Freeform knots must increase with valid multiplicities.");
                total+=Multiplicities[i];
            }
            if((Periodic?total-Multiplicities[0]:total-Degree-1)!=Poles.Length||
               Periodic&&Multiplicities[0]!=Multiplicities[^1]||!double.IsFinite(Knots[^1]-Knots[0]))
                throw new CadValidationException("Freeform knot multiplicities do not match the poles.");
            var full=Knots.Zip(Multiplicities).SelectMany(k=>Enumerable.Repeat(k.First,k.Second)).ToArray();
            double first=Periodic?Knots[0]:full[Degree],last=Periodic?Knots[^1]:full[Poles.Length];
            double period=last-first;
            if(first>=last||!Periodic&&(FirstParameter<first||LastParameter>last)||Periodic&&
               ((LastParameter-FirstParameter)/period>1+1e-12||Math.Abs((FirstParameter-first)/period)>MaximumPeriodShift||
                Math.Abs((LastParameter-first)/period)>MaximumPeriodShift+1))
                throw new CadValidationException("Freeform interval lies outside its supported native domain.");
        }
        if(Poles.All(p=>double.Hypot(p.X-Poles[0].X,p.Y-Poles[0].Y)<=1e-12))
            throw new CadValidationException("A projected freeform curve cannot collapse to a point.");
    }
    public SketchFreeformGeometry Reverse()=>this with{Reversed=!Reversed};
    public SketchFreeformGeometry Project(RigidTransform3d transform)
    {
        transform.Validate();var result=this with{Poles=Poles.Select(p=>
        {var q=transform.Apply(new(p.X,p.Y,0));return new Point2d(q.X,q.Y);}).ToImmutableArray()};
        result.Validate();return result;
    }
    public bool SameValues(SketchFreeformGeometry other)=>Kind==other.Kind&&Degree==other.Degree&&Periodic==other.Periodic&&
        FirstParameter==other.FirstParameter&&LastParameter==other.LastParameter&&Reversed==other.Reversed&&
        Poles.SequenceEqual(other.Poles)&&Weights.SequenceEqual(other.Weights)&&Knots.SequenceEqual(other.Knots)&&
        Multiplicities.SequenceEqual(other.Multiplicities);
    public static bool Same(SketchFreeformGeometry? a,SketchFreeformGeometry? b)=>a is null?b is null:b is not null&&a.SameValues(b);

    /// <summary>Rational evaluation for display and author values. It never proves topology from samples.</summary>
    public (Point2d Point,Point2d Derivative) Evaluate(double normalizedParameter)
    {
        var result=EvaluateDerivatives(normalizedParameter);return(result.Point,result.First);
    }
    /// <summary>Exact rational first and second derivatives of the owned finite, directed interval.</summary>
    public (Point2d Point,Point2d First,Point2d Second) EvaluateDerivatives(double normalizedParameter)
    {
        var result=EvaluateCore(normalizedParameter,false);return(result.Point,result.First,result.Second);
    }
    /// <summary>Exact rational third derivative in directed normalized parameter units.
    /// Knot-side evaluation does not imply continuity; contact callers must require it separately.</summary>
    public (Point2d Point,Point2d First,Point2d Second,Point2d Third) EvaluateThirdDerivatives(double normalizedParameter)
        =>EvaluateCore(normalizedParameter,true);
    private (Point2d Point,Point2d First,Point2d Second,Point2d Third) EvaluateCore(double normalizedParameter,bool includeThird)
    {
        Validate();
        if(!double.IsFinite(normalizedParameter)||normalizedParameter<0||normalizedParameter>1)
            throw new CadValidationException("Curve parameter must lie in [0,1].");
        double u=FirstParameter+(LastParameter-FirstParameter)*(Reversed?1-normalizedParameter:normalizedParameter);
        if(Periodic)
        {
            double origin=Knots[0],period=Knots[^1]-origin;double offset=(u-origin)%period;
            if(offset<0)offset+=period;
            // A finite interval ends on the left side of a C0 seam. Its lower
            // boundary and interior seam use the right side, also when reversed.
            bool upperBoundary=normalizedParameter==(Reversed?0:1);
            u=offset==0&&upperBoundary?Knots[^1]:origin+offset;
        }
        var poleOrigin=Poles[0];
        var poles=Poles.Select((p,i)=>{double w=Weights.IsEmpty?1:Weights[i];return new H((p.X-poleOrigin.X)*w,(p.Y-poleOrigin.Y)*w,w);}).ToArray();
        H value,derivative,second,third=default;
        if(Kind==SketchFreeformKind.Bezier)
        {
            value=Casteljau(poles,u);
            derivative=Casteljau(Enumerable.Range(0,Degree).Select(i=>(poles[i+1]-poles[i])*Degree).ToArray(),u);
            second=Degree<2?default:Casteljau(Enumerable.Range(0,Degree-1).Select(i=>(poles[i+2]-poles[i+1]*2+poles[i])*(Degree*(Degree-1))).ToArray(),u);
            if(includeThird&&Degree>=3)
                third=Casteljau(Enumerable.Range(0,Degree-2).Select(i=>(poles[i+3]-poles[i+2]*3+poles[i+1]*3-poles[i])*(Degree*(Degree-1)*(Degree-2))).ToArray(),u);
        }
        else
        {
            var full=Knots.Zip(Multiplicities).SelectMany(k=>Enumerable.Repeat(k.First,k.Second)).ToList();
            if(Periodic)
            {
                double period=Knots[^1]-Knots[0];int extra=Degree+1-Multiplicities[0];
                var cycle=full.Take(full.Count-Multiplicities[^1]).ToArray();int count=cycle.Length;
                double CyclicKnot(int index)=>cycle[(index%count+count)%count]+Math.Floor(index/(double)count)*period;
                full=Enumerable.Range(-extra,count+Multiplicities[^1]+2*extra).Select(CyclicKnot).ToList();
                poles=Enumerable.Range(0,full.Count-Degree-1).Select(i=>poles[i%poles.Length]).ToArray();
            }
            var knots=full.ToArray();value=DeBoor(poles,knots,Degree,u);
            var differences=Enumerable.Range(0,poles.Length-1).Select(i=>
            {
                double width=knots[i+Degree+1]-knots[i+1];return width==0?new H():(poles[i+1]-poles[i])*(Degree/width);
            }).ToArray();
            derivative=DeBoor(differences,knots[1..^1],Degree-1,u);
            var secondPoles=Enumerable.Range(0,differences.Length-1).Select(i=>
            {
                double width=knots[i+Degree+1]-knots[i+2];return width==0?new H():(differences[i+1]-differences[i])*((Degree-1)/width);
            }).ToArray();
            second=Degree<2?default:DeBoor(secondPoles,knots[2..^2],Degree-2,u);
            if(includeThird&&Degree>=3)
            {
                var thirdPoles=Enumerable.Range(0,secondPoles.Length-1).Select(i=>
                {
                    double width=knots[i+Degree+1]-knots[i+3];
                    return width==0?new H():(secondPoles[i+1]-secondPoles[i])*((Degree-2)/width);
                }).ToArray();
                third=DeBoor(thirdPoles,knots[3..^3],Degree-3,u);
            }
        }
        double scale=(LastParameter-FirstParameter)*(Reversed?-1:1);
        var point=new Point2d(value.X/value.W,value.Y/value.W);
        var first=new Point2d((derivative.X-point.X*derivative.W)/value.W,
            (derivative.Y-point.Y*derivative.W)/value.W);
        var secondValue=new Point2d((second.X-point.X*second.W-2*first.X*derivative.W)/value.W,
            (second.Y-point.Y*second.W-2*first.Y*derivative.W)/value.W);
        // Differentiate W*P=H before applying interval scale or restoring the pole origin.
        // Lower-degree rational curves can still have nonzero third derivatives.
        var jerk=includeThird?new Point2d(
            (third.X-point.X*third.W-3*first.X*second.W-3*secondValue.X*derivative.W)/value.W*scale*scale*scale,
            (third.Y-point.Y*third.W-3*first.Y*second.W-3*secondValue.Y*derivative.W)/value.W*scale*scale*scale):default;
        var acceleration=new Point2d(secondValue.X*scale*scale,secondValue.Y*scale*scale);
        point=new(point.X+poleOrigin.X,point.Y+poleOrigin.Y);var tangent=new Point2d(first.X*scale,first.Y*scale);
        CadGuard.Finite(point.X,point.Y,tangent.X,tangent.Y,acceleration.X,acceleration.Y,jerk.X,jerk.Y);return(point,tangent,acceleration,jerk);
    }
    public void RequireDerivativeContinuity(double normalizedParameter,int order)
    {
        Validate();if(order is <1 or >3||!double.IsFinite(normalizedParameter)||normalizedParameter<=0||normalizedParameter>=1)
            throw new CadValidationException("An explicit interior parameter and derivative order from one to three are required.");
        if(Kind!=SketchFreeformKind.BSpline)return;
        double range=LastParameter-FirstParameter,u=FirstParameter+range*(Reversed?1-normalizedParameter:normalizedParameter);
        if(Periodic){double period=Knots[^1]-Knots[0];u=Knots[0]+((u-Knots[0])%period+period)%period;}
        for(int i=0;i<Knots.Length;i++)
        {
            if(!Periodic&&(i==0||i==Knots.Length-1))continue;
            double distance=Math.Abs(u-Knots[i]);
            if(Periodic&&i==0)distance=Math.Min(distance,Math.Abs(u-Knots[^1]));
            if(distance/range<=1e-12&&Degree-Multiplicities[i]<order)
            {
                var error=new CadValidationException("Contact derivative is not continuous at this knot or periodic seam; choose an unambiguous finite parameter.");
                error.Data["SketchContact.Message"]="SlidingContactDerivativeAmbiguous";throw error;
            }
        }
    }
    public Point2d At(double parameter)=>Evaluate(parameter).Point;
    private readonly record struct H(double X,double Y,double W)
    {
        public static H operator +(H a,H b)=>new(a.X+b.X,a.Y+b.Y,a.W+b.W);
        public static H operator -(H a,H b)=>new(a.X-b.X,a.Y-b.Y,a.W-b.W);
        public static H operator *(H a,double b)=>new(a.X*b,a.Y*b,a.W*b);
    }
    private static H Casteljau(H[] values,double u)
    {
        values=(H[])values.Clone();for(int n=values.Length-1;n>0;n--)for(int i=0;i<n;i++)values[i]=values[i]*(1-u)+values[i+1]*u;
        return values[0];
    }
    private static H DeBoor(H[] poles,double[] knots,int degree,double u)
    {
        int span=degree;while(span<poles.Length-1&&u>=knots[span+1])span++;
        var values=new H[degree+1];for(int j=0;j<=degree;j++)values[j]=poles[span-degree+j];
        for(int r=1;r<=degree;r++)for(int j=degree;j>=r;j--)
        {
            int i=span-degree+j;double width=knots[i+degree-r+1]-knots[i];double alpha=width==0?0:(u-knots[i])/width;
            values[j]=values[j-1]*(1-alpha)+values[j]*alpha;
        }
        return values[degree];
    }
}
