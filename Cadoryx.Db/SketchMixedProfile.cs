using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>One directed boundary edge; a middle point makes it an exact circular arc.</summary>
public sealed record SketchBoundaryCurve(Point2d Start,Point2d End,Point2d? Middle=null)
{
    public Point2d? BezierControl {get;init;}
    public ImmutableArray<Point2d> SplineControls {get;init;}=[];
}

/// <summary>Exact, bounded line/arc loops. Curves stay ordered and directed.</summary>
public static class SketchMixedProfile
{
    private const double Tolerance=1e-7;
    private static bool HasFree(SketchBoundaryCurve c)=>c.BezierControl is not null||!c.SplineControls.IsDefaultOrEmpty;
    private static Point2d[] Samples(SketchBoundaryCurve curve)
    {
        if(curve.BezierControl is {} control)
            return Enumerable.Range(0,129).Select(i=>SketchBezierGeometry.At(curve.Start,control,curve.End,i/128d)).ToArray();
        if(!curve.SplineControls.IsDefaultOrEmpty)
            return Enumerable.Range(0,129).Select(i=>SketchSplineGeometry.At(curve.SplineControls,i/128d)).ToArray();
        return [curve.Start,curve.End];
    }
    public static bool Same(SketchBoundaryCurve a,SketchBoundaryCurve b)=>
        a.Start==b.Start&&a.End==b.End&&a.Middle==b.Middle&&a.BezierControl==b.BezierControl&&
        (a.SplineControls.IsDefaultOrEmpty?ImmutableArray<Point2d>.Empty:a.SplineControls)
            .SequenceEqual(b.SplineControls.IsDefaultOrEmpty?ImmutableArray<Point2d>.Empty:b.SplineControls);
    public static bool SameLoop(ImmutableArray<SketchBoundaryCurve> a,ImmutableArray<SketchBoundaryCurve> b)=>
        a.Length==b.Length&&a.Zip(b).All(pair=>Same(pair.First,pair.Second));

    public static ImmutableArray<SketchBoundaryCurve> Polygon(ImmutableArray<Point2d> vertices)=>
        Enumerable.Range(0,vertices.Length).Select(i=>new SketchBoundaryCurve(vertices[i],vertices[(i+1)%vertices.Length])).ToImmutableArray();

    public static ImmutableArray<SketchBoundaryCurve> Circle(CircularSketchRegion circle)
    {
        var left=new Point2d(circle.Center.X-circle.Radius,circle.Center.Y);
        var right=new Point2d(circle.Center.X+circle.Radius,circle.Center.Y);
        return [new(left,right,new(circle.Center.X,circle.Center.Y-circle.Radius)),
            new(right,left,new(circle.Center.X,circle.Center.Y+circle.Radius))];
    }

    /// <summary>Every hole must be strictly inside the outer loop; all holes must be disjoint.</summary>
    public static void ValidateHoles(ImmutableArray<SketchBoundaryCurve> outer,
        ImmutableArray<ImmutableArray<SketchBoundaryCurve>> holes)
    {
        foreach(var hole in holes)
        {
            if(Touch(outer,hole)||!Contains(outer,InteriorProbe(hole)))
                throw new CadValidationException("A hole must be strictly inside the outer boundary.");
        }
        for(int i=0;i<holes.Length;i++)for(int j=i+1;j<holes.Length;j++)
            if(Touch(holes[i],holes[j])||Contains(holes[i],InteriorProbe(holes[j]))||
                Contains(holes[j],InteriorProbe(holes[i])))
                throw new CadValidationException("Holes touch, overlap or nest.");
    }

    /// <summary>One bounded nesting level: every island is strictly inside exactly one hole.</summary>
    public static void ValidateIslands(ImmutableArray<ImmutableArray<SketchBoundaryCurve>> holes,
        ImmutableArray<ImmutableArray<SketchBoundaryCurve>> islands)
    {
        foreach(var island in islands)
        {
            int parents=holes.Count(h=>!Touch(h,island)&&Contains(h,InteriorProbe(island)));
            if(parents!=1||holes.Any(h=>Touch(h,island)))
                throw new CadValidationException("An island must be strictly inside one hole.");
        }
        for(int i=0;i<islands.Length;i++)for(int j=i+1;j<islands.Length;j++)
            if(Touch(islands[i],islands[j])||Contains(islands[i],InteriorProbe(islands[j]))||
                Contains(islands[j],InteriorProbe(islands[i])))
                throw new CadValidationException("Islands touch, overlap or nest.");
    }

    private static Point2d InteriorProbe(ImmutableArray<SketchBoundaryCurve> curves)
    {
        var c=curves[0];
        return c.Middle is {} middle?SketchArcGeometry.Through(c.Start,middle,c.End).At(.37):
            HasFree(c)?Samples(c)[47]:
            new((c.Start.X+c.End.X)/2,(c.Start.Y+c.End.Y)/2);
    }

    private static bool Touch(ImmutableArray<SketchBoundaryCurve> a,ImmutableArray<SketchBoundaryCurve> b)=>
        a.Any(first=>b.Any(second=>Intersections(first,second).Any()));
    public static bool Touches(ImmutableArray<SketchBoundaryCurve> a,ImmutableArray<SketchBoundaryCurve> b)=>Touch(a,b);
    public static bool StrictlyContains(ImmutableArray<SketchBoundaryCurve> outer,ImmutableArray<SketchBoundaryCurve> inner)=>
        !Touch(outer,inner)&&Contains(outer,InteriorProbe(inner));

    private static bool Contains(ImmutableArray<SketchBoundaryCurve> boundary,Point2d point)
    {
        // Select a nearby ray that avoids vertices. The caller has already rejected boundary contact.
        double y=point.Y;
        for(int attempt=0;attempt<8&&boundary.Any(c=>Math.Abs(c.Start.Y-y)<1e-10||Math.Abs(c.End.Y-y)<1e-10);attempt++)
            y=point.Y+(attempt+1)*1e-9;
        int crossings=0;
        foreach(var curve in boundary)
        {
            if(HasFree(curve))
            {
                var samples=Samples(curve);
                for(int i=1;i<samples.Length;i++)
                    if((samples[i-1].Y>y)!=(samples[i].Y>y)&&
                       samples[i-1].X+(y-samples[i-1].Y)*(samples[i].X-samples[i-1].X)/(samples[i].Y-samples[i-1].Y)>point.X)
                        crossings++;
                continue;
            }
            if(curve.Middle is not {} middle)
            {
                if((curve.Start.Y>y)!=(curve.End.Y>y)&&
                    curve.Start.X+(y-curve.Start.Y)*(curve.End.X-curve.Start.X)/(curve.End.Y-curve.Start.Y)>point.X)
                    crossings++;
                continue;
            }
            var arc=SketchArcGeometry.Through(curve.Start,middle,curve.End);
            double sine=(y-arc.Center.Y)/arc.Radius;
            if(Math.Abs(sine)>=1)continue; // tangent ray has no parity change
            double angle=Math.Asin(sine);
            foreach(var theta in new[]{angle,Math.PI-angle})
            {
                var hit=new Point2d(arc.Center.X+arc.Radius*Math.Cos(theta),y);
                // Ray parity must not count the neighboring arc again near a shared endpoint.
                if(hit.X>point.X&&OnArc(hit,arc,strict:true))crossings++;
            }
        }
        return (crossings&1)!=0;
    }

    public static void Validate(ImmutableArray<SketchBoundaryCurve> curves)
    {
        if(curves.IsDefault||curves.Length is <3 or >256||!curves.Any(c=>c.Middle is not null||HasFree(c)))
            throw new CadValidationException("A mixed profile requires a curved edge and three boundary curves.");
        for(int i=0;i<curves.Length;i++)
        {
            var curve=curves[i];var next=curves[(i+1)%curves.Length];
            CadGuard.Finite(curve.Start.X,curve.Start.Y,curve.End.X,curve.End.Y);
            if(curve.SplineControls.IsDefault||
               (curve.Middle is not null&&(curve.BezierControl is not null||!curve.SplineControls.IsEmpty))||
               (curve.BezierControl is not null&&!curve.SplineControls.IsEmpty))
                throw new CadValidationException("A mixed edge must have exactly one curve representation.");
            if(curve.BezierControl is {} control)SketchBezierGeometry.Validate(curve.Start,control,curve.End);
            if(!curve.SplineControls.IsEmpty)
            {
                SketchSplineGeometry.Validate(curve.SplineControls);
                if(Distance(curve.Start,curve.SplineControls[0])>Tolerance||
                   Distance(curve.End,curve.SplineControls[^1])>Tolerance)
                    throw new CadValidationException("Spline controls do not match the mixed edge endpoints.");
            }
            if(curve.Middle is {} middle)
            {
                _=SketchArcGeometry.Through(curve.Start,middle,curve.End);
            }
            else if(Distance(curve.Start,curve.End)<=Tolerance)
                throw new CadValidationException("Mixed profile contains a zero-length line.");
            if(Distance(curve.End,next.Start)>Tolerance)
                throw new CadValidationException("Mixed boundary curves do not form a closed chain.");
        }
        double area=0;
        foreach(var curve in curves)
        {
            area+=(curve.Start.X*curve.End.Y-curve.End.X*curve.Start.Y)/2;
            if(curve.Middle is {} middle)
            {
                var arc=SketchArcGeometry.Through(curve.Start,middle,curve.End);
                area+=arc.Radius*arc.Radius*(arc.SweepAngle-Math.Sin(arc.SweepAngle))/2;
            }
            if(HasFree(curve))
            {
                area-=(curve.Start.X*curve.End.Y-curve.End.X*curve.Start.Y)/2;
                var samples=Samples(curve);
                for(int i=1;i<samples.Length;i++)
                    area+=(samples[i-1].X*samples[i].Y-samples[i].X*samples[i-1].Y)/2;
            }
        }
        if(!double.IsFinite(area)||Math.Abs(area)<=Tolerance*Tolerance)
            throw new CadValidationException("Mixed profile has no enclosed area.");
        for(int i=0;i<curves.Length;i++)for(int j=i+1;j<curves.Length;j++)
        {
            var first=curves[i];var second=curves[j];
            bool adjacent=j==i+1||i==0&&j==curves.Length-1;
            var shared=j==i+1?first.End:curves[0].Start;
            var hits=Intersections(first,second).ToArray();
            if(hits.Any(hit=>!adjacent||Distance(hit,shared)>Tolerance))
                throw new CadValidationException("Mixed profile intersects or touches itself.");
        }
    }

    private static IEnumerable<Point2d> Intersections(SketchBoundaryCurve a,SketchBoundaryCurve b)
    {
        if(HasFree(a)||HasFree(b))
        {
            var aa=Samples(a);var bb=Samples(b);
            for(int i=1;i<aa.Length;i++)for(int j=1;j<bb.Length;j++)
            {
                var first=new SketchBoundaryCurve(aa[i-1],aa[i]);
                var second=new SketchBoundaryCurve(bb[j-1],bb[j]);
                if(a.Middle is not null)first=a;
                if(b.Middle is not null)second=b;
                foreach(var hit in Intersections(first,second))yield return hit;
            }
            yield break;
        }
        if(a.Middle is not null&&b.Middle is not null)
        {
            var first=SketchArcGeometry.Through(a.Start,a.Middle.Value,a.End);
            var second=SketchArcGeometry.Through(b.Start,b.Middle.Value,b.End);
            double d=Distance(first.Center,second.Center);
            if(d<=Tolerance&&Math.Abs(first.Radius-second.Radius)<=Tolerance)
            {
                foreach(var point in new[]{a.Start,a.End,a.Middle.Value,b.Start,b.End,b.Middle.Value})
                    if(OnArc(point,first)&&OnArc(point,second))yield return point;
                yield break;
            }
            if(d<=Tolerance||d>first.Radius+second.Radius+Tolerance||
                d<Math.Abs(first.Radius-second.Radius)-Tolerance)yield break;
            double along=(first.Radius*first.Radius-second.Radius*second.Radius+d*d)/(2*d);
            double heightSquared=first.Radius*first.Radius-along*along;
            if(heightSquared < -Tolerance)yield break;
            double height=Math.Sqrt(Math.Max(0,heightSquared));
            double dx=(second.Center.X-first.Center.X)/d,dy=(second.Center.Y-first.Center.Y)/d;
            foreach(double sign in new[]{-1d,1d})
            {
                var hit=new Point2d(first.Center.X+along*dx-sign*height*dy,
                    first.Center.Y+along*dy+sign*height*dx);
                if(OnArc(hit,first)&&OnArc(hit,second))yield return hit;
            }
            yield break;
        }
        if(a.Middle is not null){foreach(var hit in ArcLine(a,b))yield return hit;yield break;}
        if(b.Middle is not null){foreach(var hit in ArcLine(b,a))yield return hit;yield break;}
        var p=a.Start;var r=Subtract(a.End,a.Start);var q=b.Start;var s=Subtract(b.End,b.Start);
        double denominator=Cross(r,s),qp=Cross(Subtract(q,p),r);
        if(Math.Abs(denominator)<=1e-12)
        {
            if(Math.Abs(qp)>Tolerance*Length(r))yield break;
            foreach(var candidate in new[]{a.Start,a.End,b.Start,b.End})
                if(OnSegment(candidate,a.Start,a.End)&&OnSegment(candidate,b.Start,b.End))yield return candidate;
            yield break;
        }
        double t=Cross(Subtract(q,p),s)/denominator,u=Cross(Subtract(q,p),r)/denominator;
        if(t>=-Tolerance&&t<=1+Tolerance&&u>=-Tolerance&&u<=1+Tolerance)
            yield return new(p.X+t*r.X,p.Y+t*r.Y);
    }
    private static IEnumerable<Point2d> ArcLine(SketchBoundaryCurve arcCurve,SketchBoundaryCurve line)
    {
        var arc=SketchArcGeometry.Through(arcCurve.Start,arcCurve.Middle!.Value,arcCurve.End);
        var d=Subtract(line.End,line.Start);var f=Subtract(line.Start,arc.Center);
        double a=Cross(d,new(-d.Y,d.X)); // squared length
        double b=2*(f.X*d.X+f.Y*d.Y),c=f.X*f.X+f.Y*f.Y-arc.Radius*arc.Radius;
        double discriminant=b*b-4*a*c;
        if(discriminant < -Tolerance*a)yield break;
        double root=Math.Sqrt(Math.Max(0,discriminant));
        foreach(double t in new[]{(-b-root)/(2*a),(-b+root)/(2*a)})
        {
            if(t < -Tolerance||t > 1+Tolerance)continue;
            var hit=new Point2d(line.Start.X+t*d.X,line.Start.Y+t*d.Y);
            if(OnArc(hit,arc))yield return hit;
        }
    }
    private static bool OnArc(Point2d point,SketchArcGeometry arc,bool strict=false)
    {
        static double Forward(double value)=>((value%Math.Tau)+Math.Tau)%Math.Tau;
        double angle=Math.Atan2(point.Y-arc.Center.Y,point.X-arc.Center.X);
        double progress=arc.SweepAngle>0?Forward(angle-arc.StartAngle):Forward(arc.StartAngle-angle);
        return Math.Abs(Distance(point,arc.Center)-arc.Radius)<=Tolerance&&
            progress<=Math.Abs(arc.SweepAngle)+(strict?0:Tolerance/arc.Radius);
    }
    private static bool OnSegment(Point2d p,Point2d a,Point2d b)=>
        DistanceToLine(p,a,b)<=Tolerance&&p.X>=Math.Min(a.X,b.X)-Tolerance&&p.X<=Math.Max(a.X,b.X)+Tolerance&&
        p.Y>=Math.Min(a.Y,b.Y)-Tolerance&&p.Y<=Math.Max(a.Y,b.Y)+Tolerance;
    private static double DistanceToLine(Point2d p,Point2d a,Point2d b)=>Math.Abs(Cross(Subtract(b,a),Subtract(p,a)))/Distance(a,b);
    private static Point2d Subtract(Point2d a,Point2d b)=>new(a.X-b.X,a.Y-b.Y);
    private static double Cross(Point2d a,Point2d b)=>a.X*b.Y-a.Y*b.X;
    private static double Length(Point2d p)=>double.Hypot(p.X,p.Y);
    private static double Distance(Point2d a,Point2d b)=>Length(Subtract(a,b));
}
