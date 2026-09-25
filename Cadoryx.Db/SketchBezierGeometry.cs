namespace Cadoryx.Db;

/// <summary>A quadratic Bezier arch and its implicit closing chord form one bounded region.</summary>
public static class SketchBezierGeometry
{
    public static void Validate(Point2d start,Point2d control,Point2d end)
    {
        CadGuard.Finite(start.X,start.Y,control.X,control.Y,end.X,end.Y);
        double dx=end.X-start.X,dy=end.Y-start.Y;
        double chord=double.Hypot(dx,dy);
        double cross=dx*(control.Y-start.Y)-dy*(control.X-start.X);
        if(chord<=1e-7||!double.IsFinite(cross)||Math.Abs(cross)<=1e-9*chord*chord)
            throw new CadValidationException("A Bezier region needs distinct endpoints and a non-collinear control point.");
    }
    public static Point2d At(Point2d start,Point2d control,Point2d end,double t)
    {
        double u=1-t;
        return new(u*u*start.X+2*u*t*control.X+t*t*end.X,
            u*u*start.Y+2*u*t*control.Y+t*t*end.Y);
    }
}
