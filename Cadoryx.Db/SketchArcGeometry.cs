namespace Cadoryx.Db;

/// <summary>Exact circle through three authored points. The middle point selects the directed arc.</summary>
public readonly record struct SketchArcGeometry(Point2d Center,double Radius,double StartAngle,double SweepAngle)
{
    public static SketchArcGeometry Through(Point2d start,Point2d middle,Point2d end)
    {
        CadGuard.Finite(start.X,start.Y,middle.X,middle.Y,end.X,end.Y);
        double ab=double.Hypot(start.X-middle.X,start.Y-middle.Y);
        double bc=double.Hypot(middle.X-end.X,middle.Y-end.Y);
        double ca=double.Hypot(end.X-start.X,end.Y-start.Y);
        double cross=(middle.X-start.X)*(end.Y-start.Y)-(middle.Y-start.Y)*(end.X-start.X);
        if(Math.Min(ab,Math.Min(bc,ca))<=1e-7||Math.Abs(cross)<=1e-9*Math.Max(ab*bc,Math.Max(bc*ca,ca*ab)))
            throw new CadValidationException("A three-point arc needs distinct, non-collinear points.");
        double bx=middle.X-start.X,by=middle.Y-start.Y,cx=end.X-start.X,cy=end.Y-start.Y;
        double d=2*(bx*cy-by*cx);
        double ux=((bx*bx+by*by)*cy-(cx*cx+cy*cy)*by)/d;
        double uy=((cx*cx+cy*cy)*bx-(bx*bx+by*by)*cx)/d;
        var center=new Point2d(start.X+ux,start.Y+uy);
        double radius=double.Hypot(ux,uy);
        if(!double.IsFinite(radius)||radius<=1e-7)throw new CadValidationException("Invalid three-point arc radius.");
        double a=Math.Atan2(start.Y-center.Y,start.X-center.X);
        double m=Math.Atan2(middle.Y-center.Y,middle.X-center.X);
        double e=Math.Atan2(end.Y-center.Y,end.X-center.X);
        static double Forward(double angle)=>((angle%Math.Tau)+Math.Tau)%Math.Tau;
        double endSweep=Forward(e-a),middleSweep=Forward(m-a);
        double sweep=middleSweep<endSweep?endSweep:endSweep-Math.Tau;
        if(Math.Abs(sweep)<=1e-9||Math.Abs(sweep)>=Math.Tau-1e-9)
            throw new CadValidationException("Invalid three-point arc sweep.");
        return new(center,radius,a,sweep);
    }
    public Point2d At(double fraction)=>new(Center.X+Radius*Math.Cos(StartAngle+SweepAngle*fraction),
        Center.Y+Radius*Math.Sin(StartAngle+SweepAngle*fraction));
}
