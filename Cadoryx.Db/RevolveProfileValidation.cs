using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Axis clearance from analytical arc extrema and polynomial curve derivatives, never tessellation.</summary>
internal static class RevolveProfileValidation
{
    private const double Clearance=1e-7;
    public static void Validate(RevolveRecipe recipe)
    {
        var (origin,direction)=recipe.Axis?.InMeridian(recipe.Placement)??(Vector3d.Zero,Vector3d.UnitZ);
        double Radius(Point2d p)=>(p.X-origin.X)*direction.Z-(p.Y-origin.Z)*direction.X;
        var outer=Extrema(recipe.Profile,Radius).ToArray();
        // An explicitly authored line may use either radial half-plane. Legacy Z keeps positive radii.
        double sign=recipe.Axis is not null&&outer.Min()< -Clearance&&outer.Max()<=Clearance?-1:1;
        double Signed(Point2d p)=>sign*Radius(p);
        void Region(SketchProfile p,bool child)
        {
            var values=Extrema(p,Signed).ToArray();
            if(values.Min() < -Clearance||child&&values.Min()<=Clearance)
                throw new CadValidationException("Revolution regions must remain in one radial half-plane; holes and islands must stay strictly away from the axis.");
            foreach(var edge in Edges(p))
                if(InteriorExtrema(edge,Signed).Any(v=>v<=Clearance))
                    throw new CadValidationException("A curved boundary cannot touch or cross the revolution axis internally.");
            if(p.Circle is not null&&values.Min()<=Clearance)
                throw new CadValidationException("A revolved circle must stay strictly away from the axis.");
            foreach(var h in p.Holes)Region(SketchProfile.FromCircle(h.Center,h.Radius),true);
            foreach(var h in p.PolygonHoles)Region(new(h),true);
            foreach(var h in p.MixedHoles)Region(new([]){BoundaryCurves=h},true);
            foreach(var h in p.SplineHoles)Region(new([]){Spline=h},true);
            foreach(var island in p.Islands)Region(island.Profile,true);
        }
        Region(recipe.Profile,false);
    }
    private static IEnumerable<SketchBoundaryCurve> Edges(SketchProfile p)
    {
        if(p.Circle is not null)return [];
        if(!p.BoundaryCurves.IsEmpty)return p.BoundaryCurves;
        if(p.Arc is {} a)return [new(a.Start,a.End,a.Middle),new(a.End,a.Start)];
        if(p.Bezier is {} b)return [new(b.Start,b.End){BezierControl=b.Control},new(b.End,b.Start)];
        if(p.Spline is {} s)return [new(s.Controls[0],s.Controls[^1]){SplineControls=s.Controls,SplineKnots=s.Knots},new(s.Controls[^1],s.Controls[0])];
        return SketchMixedProfile.Polygon(p.Points);
    }
    private static IEnumerable<double> Extrema(SketchProfile p,Func<Point2d,double> radius)
    {
        if(p.Circle is {} c)
        {
            double center=radius(c.Center);
            yield return center-c.Radius;yield return center+c.Radius;yield break;
        }
        foreach(var edge in Edges(p))
        {
            yield return radius(edge.Start);yield return radius(edge.End);
            foreach(double v in InteriorExtrema(edge,radius))yield return v;
        }
    }
    private static IEnumerable<double> InteriorExtrema(SketchBoundaryCurve edge,Func<Point2d,double> radius)
    {
        if(edge.Ellipse is {} ellipse)
        {
            double zero=radius(new(0,0));double rx=radius(new(1,0))-zero,ry=radius(new(0,1))-zero;
            double extremum=Math.Atan2(rx*ellipse.Sine.X+ry*ellipse.Sine.Y,rx*ellipse.Cosine.X+ry*ellipse.Cosine.Y);
            foreach(double angle in new[]{extremum,extremum+Math.PI})
            {
                double travel=ellipse.SweepAngle>0?Positive(angle-ellipse.StartAngle):Positive(ellipse.StartAngle-angle);
                if(travel>1e-10&&travel<Math.Abs(ellipse.SweepAngle)-1e-10)
                    yield return radius(ellipse.At(travel/Math.Abs(ellipse.SweepAngle)));
            }
        }
        else if(edge.Middle is {} middle)
        {
            var arc=SketchArcGeometry.Through(edge.Start,middle,edge.End);
            double rx=radius(new(1,0))-radius(new(0,0)),ry=radius(new(0,1))-radius(new(0,0));
            double extremum=Math.Atan2(ry,rx);
            foreach(double angle in new[]{extremum,extremum+Math.PI})
            {
                double travel=arc.SweepAngle>0?Positive(angle-arc.StartAngle):Positive(arc.StartAngle-angle);
                if(travel>1e-10&&travel<Math.Abs(arc.SweepAngle)-1e-10)
                    yield return radius(new(arc.Center.X+arc.Radius*Math.Cos(angle),arc.Center.Y+arc.Radius*Math.Sin(angle)));
            }
        }
        else if(edge.BezierControl is {} control)
        {
            double a=radius(edge.Start),b=radius(control),c=radius(edge.End);
            foreach(double t in Stationary([a,2*(b-a),a-2*b+c,0]))
                yield return (1-t)*(1-t)*a+2*t*(1-t)*b+t*t*c;
        }
        else if(!edge.SplineControls.IsDefaultOrEmpty)
        {
            var spans=SplinePolynomials(edge.SplineControls.Select(radius).ToArray(),edge.SplineDefinition).ToArray();
            for(int i=0;i<spans.Length;i++)
            {
                var polynomial=spans[i];
                // A knot is internal to the curve, although it is the end of a polynomial span.
                if(i>0)yield return polynomial[0];
                foreach(double t in Stationary(polynomial))yield return At(polynomial,t);
            }
        }
    }
    private static double Positive(double a)=>(a%Math.Tau+Math.Tau)%Math.Tau;
    private static double At(double[] p,double t)=>((p[3]*t+p[2])*t+p[1])*t+p[0];
    private static IEnumerable<double> Stationary(double[] p)
    {
        double a=3*p[3],b=2*p[2],c=p[1],scale=Math.Max(Math.Abs(a),Math.Max(Math.Abs(b),Math.Abs(c)));
        if(scale==0)yield break;
        a/=scale;b/=scale;c/=scale;
        if(Math.Abs(a)<=1e-14)
        {
            if(Math.Abs(b)>1e-14){double root=-c/b;if(root>0&&root<1)yield return root;}
            yield break;
        }
        double discriminant=b*b-4*a*c;
        if(discriminant<0)yield break;
        double q=-.5*(b+Math.CopySign(Math.Sqrt(discriminant),b));
        if(q==0){double root=-b/(2*a);if(root>0&&root<1)yield return root;yield break;}
        foreach(double root in new[]{q/a,c/q})if(root>0&&root<1)yield return root;
    }
    private static IEnumerable<double[]> SplinePolynomials(double[] controls,CubicSplineDefinition definition)
    {
        var full=definition.ExpandedKnots();
        for(int span=3;span<controls.Length;span++)
        {
            double start=full[span],width=full[span+1]-start;
            if(width==0)continue;
            var values=Enumerable.Range(0,4).Select(j=>new[]{controls[span-3+j],0d,0d,0d}).ToArray();
            // Symbolic de Boor on a normalized span: each blend is an affine polynomial.
            for(int r=1;r<=3;r++)for(int j=3;j>=r;j--)
            {
                int index=span-3+j;double denominator=full[index+4-r]-full[index];
                double alpha=(start-full[index])/denominator,beta=width/denominator;
                var previous=values[j-1];var current=values[j];var result=new double[4];
                for(int k=0;k<4;k++)
                {
                    result[k]=previous[k]+alpha*(current[k]-previous[k]);
                    if(k>0)result[k]+=beta*(current[k-1]-previous[k-1]);
                }
                values[j]=result;
            }
            yield return values[3];
        }
    }
}
