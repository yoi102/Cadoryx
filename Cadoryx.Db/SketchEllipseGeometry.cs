namespace Cadoryx.Db;

/// <summary>Exact affine conic: center + cosine*cos(u) + sine*sin(u), on an explicit finite interval.</summary>
public readonly record struct SketchEllipseGeometry(Point2d Center,Point2d Cosine,Point2d Sine,double StartAngle,double SweepAngle)
{
    public void Validate()
    {
        CadGuard.Finite(Center.X,Center.Y,Cosine.X,Cosine.Y,Sine.X,Sine.Y,StartAngle,SweepAngle);
        if(Math.Abs(Center.X)>1e9||Math.Abs(Center.Y)>1e9||Math.Abs(StartAngle)>1e9||
           Math.Abs(SweepAngle)<=1e-12||Math.Abs(SweepAngle)>Math.Tau+1e-12)
            throw new CadValidationException("Invalid finite ellipse interval.");
        var principal=Principal();
        if(principal.MajorRadius>1e9||principal.MinorRadius<=1e-7)
            throw new CadValidationException("Ellipse projection collapses or exceeds its size budget.");
    }
    public Point2d At(double normalized)
    {
        double u=StartAngle+SweepAngle*normalized;
        return new(Center.X+Cosine.X*Math.Cos(u)+Sine.X*Math.Sin(u),Center.Y+Cosine.Y*Math.Cos(u)+Sine.Y*Math.Sin(u));
    }
    public Point2d Derivative(double normalized)
    {
        double u=StartAngle+SweepAngle*normalized;
        return new((-Cosine.X*Math.Sin(u)+Sine.X*Math.Cos(u))*SweepAngle,
            (-Cosine.Y*Math.Sin(u)+Sine.Y*Math.Cos(u))*SweepAngle);
    }
    public Point2d SecondDerivative(double normalized)
    {
        double u=StartAngle+SweepAngle*normalized,squared=SweepAngle*SweepAngle;
        return new((-Cosine.X*Math.Cos(u)-Sine.X*Math.Sin(u))*squared,
            (-Cosine.Y*Math.Cos(u)-Sine.Y*Math.Sin(u))*squared);
    }
    public Point2d ThirdDerivative(double normalized)
    {
        double u=StartAngle+SweepAngle*normalized,cubed=SweepAngle*SweepAngle*SweepAngle;
        return new((Cosine.X*Math.Sin(u)-Sine.X*Math.Cos(u))*cubed,
            (Cosine.Y*Math.Sin(u)-Sine.Y*Math.Cos(u))*cubed);
    }
    public SketchEllipseGeometry Reversed()=>this with{StartAngle=StartAngle+SweepAngle,SweepAngle=-SweepAngle};
    public SketchEllipseGeometry Project(RigidTransform3d sourceToTarget)
    {
        sourceToTarget.Validate();var c=sourceToTarget.Apply(new(Center.X,Center.Y,0));
        var u=sourceToTarget.Rotation.Rotate(new(Cosine.X,Cosine.Y,0));
        var v=sourceToTarget.Rotation.Rotate(new(Sine.X,Sine.Y,0));
        var result=this with{Center=new(c.X,c.Y),Cosine=new(u.X,u.Y),Sine=new(v.X,v.Y)};
        result.Validate();return result;
    }
    public bool SameValues(SketchEllipseGeometry other,double tolerance=1e-7)=>
        double.Hypot(Center.X-other.Center.X,Center.Y-other.Center.Y)<=tolerance&&
        double.Hypot(Cosine.X-other.Cosine.X,Cosine.Y-other.Cosine.Y)<=tolerance&&
        double.Hypot(Sine.X-other.Sine.X,Sine.Y-other.Sine.Y)<=tolerance&&
        Math.Abs(StartAngle-other.StartAngle)<=1e-10&&Math.Abs(SweepAngle-other.SweepAngle)<=1e-10;
    public readonly record struct PrincipalEllipse(double MajorRadius,double MinorRadius,double AxisAngle,double StartAngle,double SweepAngle);
    /// <summary>Stable 2x2 principal-axis decomposition, including reflection and parameter phase.</summary>
    public PrincipalEllipse Principal()
    {
        double xx=Cosine.X*Cosine.X+Sine.X*Sine.X,yy=Cosine.Y*Cosine.Y+Sine.Y*Sine.Y;
        double xy=Cosine.X*Cosine.Y+Sine.X*Sine.Y,det=Cosine.X*Sine.Y-Cosine.Y*Sine.X;
        double major=Math.Sqrt((xx+yy+double.Hypot(xx-yy,2*xy))/2);
        if(!double.IsFinite(major)||major<=0||!double.IsFinite(det)||det==0)
            throw new CadValidationException("Degenerate ellipse axes.");
        double minor=Math.Min(major,Math.Abs(det)/major),axis=.5*Math.Atan2(2*xy,xx-yy);
        double a=(Math.Cos(axis)*Cosine.X+Math.Sin(axis)*Cosine.Y)/major;
        double b=(Math.Cos(axis)*Sine.X+Math.Sin(axis)*Sine.Y)/major;
        int orientation=Math.Sign(det);double phase=Math.Atan2(-orientation*b,a);
        return new(major,minor,axis,orientation*StartAngle+phase,orientation*SweepAngle);
    }
}
