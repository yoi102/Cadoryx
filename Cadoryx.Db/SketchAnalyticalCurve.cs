namespace Cadoryx.Db;

/// <summary>Exact straight/circular geometry used by edits, never display tessellation.</summary>
public sealed record SketchAnalyticalCurve(Point2d Start,Point2d End,SketchArcGeometry? Arc=null,bool Closed=false)
{
    public const double Tolerance=1e-7;
    public double Length=>Arc is {} arc?arc.Radius*Math.Abs(arc.SweepAngle):double.Hypot(End.X-Start.X,End.Y-Start.Y);
    public Point2d At(double t)=>Arc is {} arc?arc.At(t):new(Start.X+t*(End.X-Start.X),Start.Y+t*(End.Y-Start.Y));
    public double Parameter(Point2d point)
    {
        if(Arc is not {} arc)
        {
            double dx=End.X-Start.X,dy=End.Y-Start.Y;
            return((point.X-Start.X)*dx+(point.Y-Start.Y)*dy)/(dx*dx+dy*dy);
        }
        double angle=Math.Atan2(point.Y-arc.Center.Y,point.X-arc.Center.X);
        double travel=arc.SweepAngle>0?angle-arc.StartAngle:arc.StartAngle-angle;
        return((travel%Math.Tau+Math.Tau)%Math.Tau)/Math.Abs(arc.SweepAngle);
    }
    public bool Contains(Point2d p)
    {
        double t=Parameter(p),epsilon=Tolerance/Length;
        return Closed||t>=-epsilon&&t<=1+epsilon||Arc is {} arc&&Math.Tau/Math.Abs(arc.SweepAngle)-t<=epsilon;
    }
    public static SketchAnalyticalCurve From(CadSketch s,SketchEntityId id)
    {
        Point2d P(SketchEntityId p)=>s.Points.Single(v=>v.Id==p).Position;
        if(s.Lines.SingleOrDefault(l=>l.Id==id) is {} l)return new(P(l.Start),P(l.End));
        if(s.Circles.SingleOrDefault(c=>c.Id==id) is {} c)
        {
            var center=P(c.Center);var start=new Point2d(center.X+c.Radius,center.Y);
            return new(start,start,new(center,c.Radius,0,Math.Tau),true);
        }
        if(s.Arcs.SingleOrDefault(a=>a.Id==id) is {} a)return new(P(a.Start),P(a.End),SketchArcGeometry.Through(P(a.Start),P(a.Middle),P(a.End)));
        throw new CadValidationException("Select a straight line, circle or circular arc.");
    }
    public static IEnumerable<SketchEntityId> Ids(CadSketch s)=>s.Lines.Select(l=>l.Id).Concat(s.Circles.Select(c=>c.Id)).Concat(s.Arcs.Select(a=>a.Id));
    public static IEnumerable<Point2d> Intersections(SketchAnalyticalCurve a,SketchAnalyticalCurve b)
    {
        if(a.Arc is null&&b.Arc is null)
        {
            double dx=a.End.X-a.Start.X,dy=a.End.Y-a.Start.Y,ex=b.End.X-b.Start.X,ey=b.End.Y-b.Start.Y;
            double cx=b.Start.X-a.Start.X,cy=b.Start.Y-a.Start.Y,den=dx*ey-dy*ex;
            if(Math.Abs(den)<=1e-12*a.Length*b.Length)
            {
                if(Math.Abs(cx*dy-cy*dx)<=Tolerance*a.Length)throw new CadValidationException("Coincident boundaries are ambiguous.");
                yield break;
            }
            double u=(cx*dy-cy*dx)/den;
            if(u>=-Tolerance/b.Length&&u<=1+Tolerance/b.Length)yield return a.At((cx*ey-cy*ex)/den);
            yield break;
        }
        if(a.Arc is null||b.Arc is null)
        {
            var line=a.Arc is null?a:b;var circle=a.Arc is not null?a:b;var arc=circle.Arc!.Value;
            double dx=(line.End.X-line.Start.X)/line.Length,dy=(line.End.Y-line.Start.Y)/line.Length;
            double along=(arc.Center.X-line.Start.X)*dx+(arc.Center.Y-line.Start.Y)*dy;
            var closest=new Point2d(line.Start.X+along*dx,line.Start.Y+along*dy);
            double distance=double.Hypot(closest.X-arc.Center.X,closest.Y-arc.Center.Y);
            if(distance>arc.Radius+Tolerance)yield break;
            if(Math.Abs(distance-arc.Radius)<=Tolerance)
            {
                if(b.Contains(closest))throw new CadValidationException("A tangent contact cannot define a cut or extension.");
                yield break;
            }
            double half=Math.Sqrt((arc.Radius-distance)*(arc.Radius+distance));
            foreach(double offset in new[]{-half,half})
            {
                var hit=new Point2d(closest.X+offset*dx,closest.Y+offset*dy);
                if(b.Contains(hit))yield return hit;
            }
            yield break;
        }
        var first=a.Arc.Value;var second=b.Arc.Value;
        double separation=double.Hypot(second.Center.X-first.Center.X,second.Center.Y-first.Center.Y);
        if(separation<=Tolerance)
        {
            if(Math.Abs(first.Radius-second.Radius)<=Tolerance)throw new CadValidationException("Coincident circular boundaries are ambiguous.");
            yield break;
        }
        if(separation>first.Radius+second.Radius+Tolerance||separation<Math.Abs(first.Radius-second.Radius)-Tolerance)yield break;
        if(Math.Abs(separation-first.Radius-second.Radius)<=Tolerance||Math.Abs(separation-Math.Abs(first.Radius-second.Radius))<=Tolerance)
            throw new CadValidationException("A tangent contact cannot define a cut or extension.");
        double x=(first.Radius*first.Radius-second.Radius*second.Radius+separation*separation)/(2*separation);
        double height=Math.Sqrt(Math.Max(0,(first.Radius-x)*(first.Radius+x)));
        double ux=(second.Center.X-first.Center.X)/separation,uy=(second.Center.Y-first.Center.Y)/separation;
        foreach(int sign in new[]{-1,1})
        {
            var hit=new Point2d(first.Center.X+x*ux-sign*height*uy,first.Center.Y+x*uy+sign*height*ux);
            if(b.Contains(hit))yield return hit;
        }
    }
}
