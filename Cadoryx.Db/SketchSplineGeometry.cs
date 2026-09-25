using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>A clamped, uniform cubic B-spline and its closing chord.</summary>
public static class SketchSplineGeometry
{
    public const int Degree=3;
    public const int MaximumControls=8;

    public static void Validate(ImmutableArray<Point2d> controls)
    {
        if(controls.IsDefault||controls.Length is <4 or >MaximumControls)
            throw new CadValidationException("A cubic spline region needs 4 to 8 control points.");
        foreach(var point in controls)CadGuard.Finite(point.X,point.Y);
        var start=controls[0];var end=controls[^1];
        double dx=end.X-start.X,dy=end.Y-start.Y,lengthSquared=dx*dx+dy*dy;
        if(!double.IsFinite(lengthSquared)||lengthSquared<=1e-14)
            throw new CadValidationException("Spline endpoints must differ.");
        // Monotone chord projection and one-sided controls make the spline-plus-chord
        // a simple region without relying on a display tessellation for topology.
        double previous=0;int side=0;
        for(int i=1;i<controls.Length-1;i++)
        {
            double px=controls[i].X-start.X,py=controls[i].Y-start.Y;
            double along=(px*dx+py*dy)/lengthSquared;
            double cross=dx*py-dy*px;
            if(!double.IsFinite(along)||along<=previous+1e-8||along>=1-1e-8||
               !double.IsFinite(cross)||Math.Abs(cross)<=1e-9*lengthSquared)
                throw new CadValidationException("Spline controls must advance along and stay away from the closing chord.");
            int current=Math.Sign(cross);
            if(side!=0&&side!=current)throw new CadValidationException("Spline controls must stay on one side of the chord.");
            side=current;previous=along;
        }
    }

    public static double[] Knots(int count)
    {
        if(count is <4 or >MaximumControls)throw new CadValidationException("Invalid spline control count.");
        return Enumerable.Range(0,count-2).Select(i=>i/(double)(count-3)).ToArray();
    }
    public static int[] Multiplicities(int count)=>Knots(count).Select((_,i)=>i==0||i==count-3?4:1).ToArray();

    public static Point2d At(ImmutableArray<Point2d> controls,double t)
    {
        Validate(controls);
        if(!double.IsFinite(t))throw new CadValidationException("Spline parameter must be finite.");
        t=Math.Clamp(t,0,1);
        if(t==1)return controls[^1];
        var knots=Knots(controls.Length);
        int span=Degree;
        while(span<controls.Length-1&&t>=knots[span-Degree+1])span++;
        var values=new Point2d[Degree+1];
        for(int j=0;j<=Degree;j++)values[j]=controls[span-Degree+j];
        // Expanded knot vector for de Boor evaluation.
        var full=Enumerable.Repeat(0d,4).Concat(knots.Skip(1).SkipLast(1)).Concat(Enumerable.Repeat(1d,4)).ToArray();
        for(int r=1;r<=Degree;r++)for(int j=Degree;j>=r;j--)
        {
            int index=span-Degree+j;
            double denominator=full[index+Degree-r+1]-full[index];
            double alpha=denominator==0?0:(t-full[index])/denominator;
            values[j]=new((1-alpha)*values[j-1].X+alpha*values[j].X,
                (1-alpha)*values[j-1].Y+alpha*values[j].Y);
        }
        return values[Degree];
    }
}
