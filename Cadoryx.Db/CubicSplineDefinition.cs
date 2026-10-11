using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Copied nonrational, clamped cubic author geometry. Curve validity is independent of region validity.</summary>
public sealed record CubicSplineDefinition(ImmutableArray<Point2d> Controls,
    ImmutableArray<double> Knots,ImmutableArray<int> Multiplicities)
{
    public const int Degree=3;
    public const int MaximumControls=1024;
    public static CubicSplineDefinition Uniform(ImmutableArray<Point2d> controls)
    {
        if(controls.IsDefault||controls.Length is <4 or >MaximumControls)
            throw new CadValidationException("A cubic curve needs 4 to 1024 control points.");
        var knots=Enumerable.Range(0,controls.Length-2).Select(i=>i/(double)(controls.Length-3)).ToImmutableArray();
        return new(controls,knots,knots.Select((_,i)=>i==0||i==knots.Length-1?4:1).ToImmutableArray());
    }
    public void Validate()
    {
        if(Controls.IsDefault||Controls.Length is <4 or >MaximumControls||Knots.IsDefault||Multiplicities.IsDefault||
           Knots.Length<2||Knots.Length!=Multiplicities.Length||Knots.Length>Controls.Length)
            throw new CadValidationException("Invalid cubic curve tables.");
        foreach(var point in Controls)CadGuard.Finite(point.X,point.Y);
        if(Multiplicities[0]!=4||Multiplicities[^1]!=4)
            throw new CadValidationException("Cubic endpoint knots must have multiplicity four.");
        long total=0;
        for(int i=0;i<Knots.Length;i++)
        {
            if(!double.IsFinite(Knots[i])||i>0&&Knots[i]<=Knots[i-1]||Multiplicities[i]<1||
               i>0&&i<Knots.Length-1&&Multiplicities[i]>Degree)
                throw new CadValidationException("Knots must increase with continuous cubic multiplicities.");
            total+=Multiplicities[i];
        }
        if(total!=Controls.Length+Degree+1||!double.IsFinite(Knots[^1]-Knots[0])||Knots[^1]-Knots[0]<=0)
            throw new CadValidationException("Knot multiplicities do not match the cubic control count.");
        if(Controls.All(p=>double.Hypot(p.X-Controls[0].X,p.Y-Controls[0].Y)<=1e-12))
            throw new CadValidationException("A curve cannot collapse to one point.");
    }
    public ImmutableArray<double> ExpandedKnots()
    {Validate();return Knots.Zip(Multiplicities).SelectMany(p=>Enumerable.Repeat(p.First,p.Second)).ToImmutableArray();}
    /// <summary>Evaluates the point and derivative with respect to the normalized [0,1] parameter.</summary>
    public (Point2d Point,Point2d Derivative) Evaluate(double normalizedParameter)
    {
        var result=EvaluateCore(normalizedParameter,false);return(result.Point,result.Derivative);
    }
    /// <summary>Exact one-sided first and second derivatives at knots, in normalized parameter units.</summary>
    public (Point2d Point,Point2d Derivative,Point2d SecondDerivative) EvaluateDerivatives(double normalizedParameter)
        =>EvaluateCore(normalizedParameter,true);
    /// <summary>Exact linear weights for the point and normalized first/second derivatives.
    /// Interior knots use the right-hand span; the final endpoint uses the left-hand span.</summary>
    public ImmutableArray<(double Position,double FirstDerivative,double SecondDerivative)> BasisDerivatives(double normalizedParameter)
    {
        Validate();RequireParameter(normalizedParameter);var full=ExpandedKnots();
        double range=Knots[^1]-Knots[0],u=Knots[0]+range*normalizedParameter;
        var position=Basis(Controls.Length,full,Degree,u);
        var firstKnots=full.RemoveAt(full.Length-1).RemoveAt(0);
        var firstBasis=Basis(Controls.Length-1,firstKnots,Degree-1,u);
        var secondBasis=Basis(Controls.Length-2,firstKnots.RemoveAt(firstKnots.Length-1).RemoveAt(0),Degree-2,u);
        var first=new double[Controls.Length];var second=new double[Controls.Length];
        double Factor(int i)=>full[i+Degree+1]==full[i+1]?0:Degree/(full[i+Degree+1]-full[i+1])*range;
        for(int i=0;i<firstBasis.Length;i++)
        {double value=firstBasis[i]*Factor(i);first[i]-=value;first[i+1]+=value;}
        for(int i=0;i<secondBasis.Length;i++)
        {
            double width=firstKnots[i+Degree]-firstKnots[i+1];
            double value=width==0?0:secondBasis[i]*(Degree-1)/width*range;
            double a=value*Factor(i),b=value*Factor(i+1);
            second[i]+=a;second[i+1]-=a+b;second[i+2]+=b;
        }
        var result=ImmutableArray.CreateBuilder<(double,double,double)>(Controls.Length);
        for(int i=0;i<Controls.Length;i++)
        {CadGuard.Finite(position[i],first[i],second[i]);result.Add((position[i],first[i],second[i]));}
        return result.ToImmutable();
    }
    private static double[] Basis(int count,ImmutableArray<double> full,int degree,double u)
    {
        int span=Span(count,degree,full,u);var values=new double[degree+1][];
        for(int j=0;j<=degree;j++){values[j]=new double[count];values[j][span-degree+j]=1;}
        for(int r=1;r<=degree;r++)for(int j=degree;j>=r;j--)
        {
            int index=span-degree+j;double width=full[index+degree-r+1]-full[index];
            double alpha=width==0?0:(u-full[index])/width;
            for(int i=0;i<count;i++)values[j][i]=(1-alpha)*values[j-1][i]+alpha*values[j][i];
        }
        return values[degree];
    }
    /// <summary>Exact point and first/second/third control weights, with explicit knot-side semantics.</summary>
    public ImmutableArray<(double Position,double FirstDerivative,double SecondDerivative,double ThirdDerivative)> BasisThirdDerivatives(double normalizedParameter)
    {
        var lower=BasisDerivatives(normalizedParameter);var full=ExpandedKnots();
        double range=Knots[^1]-Knots[0],u=Knots[0]+range*normalizedParameter;
        // Each row initially selects one author control. Repeated exact difference
        // operators produce third-derivative controls in normalized units.
        var rows=Enumerable.Range(0,Controls.Length).Select(i=>
        {var row=new double[Controls.Length];row[i]=1;return row;}).ToArray();
        var knots=full;int degree=Degree;
        while(degree>0)
        {
            var next=new double[rows.Length-1][];
            for(int i=0;i<next.Length;i++)
            {
                double width=knots[i+degree+1]-knots[i+1],factor=width==0?0:degree/width*range;
                next[i]=rows[i+1].Zip(rows[i],(a,b)=>(a-b)*factor).ToArray();
            }
            rows=next;knots=knots.RemoveAt(knots.Length-1).RemoveAt(0);degree--;
        }
        var thirdBasis=Basis(rows.Length,knots,0,u);var third=new double[Controls.Length];
        for(int i=0;i<rows.Length;i++)for(int j=0;j<third.Length;j++)third[j]+=thirdBasis[i]*rows[i][j];
        var result=ImmutableArray.CreateBuilder<(double,double,double,double)>(Controls.Length);
        for(int i=0;i<Controls.Length;i++)
        {CadGuard.Finite(third[i]);result.Add((lower[i].Position,lower[i].FirstDerivative,lower[i].SecondDerivative,third[i]));}
        return result.ToImmutable();
    }
    private (Point2d Point,Point2d Derivative,Point2d SecondDerivative) EvaluateCore(double normalizedParameter,bool includeSecond)
    {
        Validate();RequireParameter(normalizedParameter);var full=ExpandedKnots();
        double u=Knots[0]+(Knots[^1]-Knots[0])*normalizedParameter;
        var point=DeBoor(Controls,full,Degree,u);
        var derivative=ImmutableArray.CreateBuilder<Point2d>(Controls.Length-1);
        for(int i=0;i<Controls.Length-1;i++)
        {
            double width=full[i+Degree+1]-full[i+1];
            double scale=width==0?0:Degree/width*(Knots[^1]-Knots[0]);
            derivative.Add(new((Controls[i+1].X-Controls[i].X)*scale,(Controls[i+1].Y-Controls[i].Y)*scale));
        }
        var first=derivative.ToImmutable();var firstKnots=full.RemoveAt(full.Length-1).RemoveAt(0);
        var firstValue=DeBoor(first,firstKnots,Degree-1,u);
        if(!includeSecond)return(point,firstValue,default);
        var second=ImmutableArray.CreateBuilder<Point2d>(first.Length-1);
        for(int i=0;i<first.Length-1;i++)
        {
            double width=firstKnots[i+Degree]-firstKnots[i+1];
            double factor=width==0?0:(Degree-1)/width*(Knots[^1]-Knots[0]);
            second.Add(new((first[i+1].X-first[i].X)*factor,(first[i+1].Y-first[i].Y)*factor));
        }
        return(point,firstValue,
            DeBoor(second.ToImmutable(),firstKnots.RemoveAt(firstKnots.Length-1).RemoveAt(0),Degree-2,u));
    }
    public CubicSplineDefinition Reverse()
    {
        Validate();double first=Knots[0],last=Knots[^1];
        return new(Controls.Reverse().ToImmutableArray(),Knots.Reverse().Select(k=>first+(last-k)).ToImmutableArray(),
            Multiplicities.Reverse().ToImmutableArray());
    }
    /// <summary>Exact knot insertion and extraction. No tessellation or point fitting changes the curve.</summary>
    public CubicSplineDefinition Trim(double firstNormalizedParameter,double lastNormalizedParameter)
    {
        Validate();RequireParameter(firstNormalizedParameter);RequireParameter(lastNormalizedParameter);
        if(firstNormalizedParameter>=lastNormalizedParameter)
            throw new CadValidationException("Trim parameters must increase.");
        double width=Knots[^1]-Knots[0],first=Knots[0]+firstNormalizedParameter*width,last=Knots[0]+lastNormalizedParameter*width;
        var controls=Controls;var full=ExpandedKnots();
        InsertToClamp(first,ref controls,ref full);InsertToClamp(last,ref controls,ref full);
        int begin=full.IndexOf(first),end=full.LastIndexOf(last),count=end-begin-Degree;
        var extracted=full.Skip(begin).Take(end-begin+1).ToImmutableArray();
        var knots=extracted.Distinct().ToImmutableArray();
        var result=new CubicSplineDefinition(controls.Skip(begin).Take(count).ToImmutableArray(),knots,
            knots.Select(k=>extracted.Count(u=>u==k)).ToImmutableArray());
        result.Validate();return result;
    }
    /// <summary>Continues the declared end-span cubic polynomial by an explicit positive parameter amount.</summary>
    public CubicSplineDefinition ExtendEndpoint(bool start,double normalizedAmount)
    {
        Validate();if(!double.IsFinite(normalizedAmount)||normalizedAmount<=0||normalizedAmount>4)
            throw new CadValidationException("Polynomial extension amount must be in (0,4].");
        if(start)return Reverse().ExtendEndpoint(false,normalizedAmount).Reverse();
        double added=normalizedAmount*(Knots[^1]-Knots[0]);var spans=new List<CubicSplineDefinition>();
        for(int i=0;i<Knots.Length-1;i++)spans.Add(Trim((Knots[i]-Knots[0])/(Knots[^1]-Knots[0]),
            (Knots[i+1]-Knots[0])/(Knots[^1]-Knots[0])));
        var final=spans[^1];double factor=1+added/(final.Knots[^1]-final.Knots[0]);
        var p=final.Controls;
        Point2d At(double t)
        {
            double s=1-t;
            return new(s*s*s*p[0].X+3*s*s*t*p[1].X+3*s*t*t*p[2].X+t*t*t*p[3].X,
                s*s*s*p[0].Y+3*s*s*t*p[1].Y+3*s*t*t*p[2].Y+t*t*t*p[3].Y);
        }
        Point2d Derivative(double t)
        {
            double s=1-t;
            return new(3*(s*s*(p[1].X-p[0].X)+2*s*t*(p[2].X-p[1].X)+t*t*(p[3].X-p[2].X)),
                3*(s*s*(p[1].Y-p[0].Y)+2*s*t*(p[2].Y-p[1].Y)+t*t*(p[3].Y-p[2].Y)));
        }
        var a=At(0);var b=At(factor);var da=Derivative(0);var db=Derivative(factor);
        spans[^1]=new([a,new(a.X+factor*da.X/3,a.Y+factor*da.Y/3),
            new(b.X-factor*db.X/3,b.Y-factor*db.Y/3),b],[final.Knots[0],final.Knots[^1]+added],[4,4]);
        var combined=spans[0].Controls.Concat(spans.Skip(1).SelectMany(s=>s.Controls.Skip(1))).ToImmutableArray();
        var knots=Knots.SetItem(Knots.Length-1,Knots[^1]+added);
        var result=new CubicSplineDefinition(combined,knots,knots.Select((_,i)=>i==0||i==knots.Length-1?4:3).ToImmutableArray());
        result.Validate();return result;
    }
    private static void InsertToClamp(double u,ref ImmutableArray<Point2d> controls,ref ImmutableArray<double> full)
    {
        while(full.Count(k=>k==u)<Degree+1)
        {
            int span=Span(controls.Length,Degree,full,u),multiplicity=full.Count(k=>k==u);
            var result=new Point2d[controls.Length+1];
            for(int i=0;i<=span-Degree;i++)result[i]=controls[i];
            for(int i=span-multiplicity;i<controls.Length;i++)result[i+1]=controls[i];
            for(int i=span-Degree+1;i<=span-multiplicity;i++)
            {
                double denominator=full[i+Degree]-full[i];
                double alpha=denominator==0?0:(u-full[i])/denominator;
                result[i]=new((1-alpha)*controls[i-1].X+alpha*controls[i].X,
                    (1-alpha)*controls[i-1].Y+alpha*controls[i].Y);
            }
            controls=result.ToImmutableArray();full=full.Insert(span+1,u);
        }
    }
    private static Point2d DeBoor(ImmutableArray<Point2d> controls,ImmutableArray<double> full,int degree,double u)
    {
        int span=Span(controls.Length,degree,full,u);var values=new Point2d[degree+1];
        for(int j=0;j<=degree;j++)values[j]=controls[span-degree+j];
        for(int r=1;r<=degree;r++)for(int j=degree;j>=r;j--)
        {
            int index=span-degree+j;double denominator=full[index+degree-r+1]-full[index];
            double alpha=denominator==0?0:(u-full[index])/denominator;
            values[j]=new((1-alpha)*values[j-1].X+alpha*values[j].X,
                (1-alpha)*values[j-1].Y+alpha*values[j].Y);
        }
        CadGuard.Finite(values[degree].X,values[degree].Y);return values[degree];
    }
    private static int Span(int count,int degree,ImmutableArray<double> full,double u)
    {
        if(u>=full[count])return count-1;
        int span=degree;while(span<count-1&&u>=full[span+1])span++;return span;
    }
    private static void RequireParameter(double t)
    {if(!double.IsFinite(t)||t<0||t>1)throw new CadValidationException("Curve parameter must lie in [0,1].");}
}
