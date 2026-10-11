using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length!=1)throw new ArgumentException("Usage: SlidingContactGeometryProbe output.json");
var cases=new Dictionary<string,SketchCurve2d>
{
    ["circle"]=SketchCurve2d.Circle(new(3,-2),4),
    ["arc-forward-seam"]=SketchCurve2d.CircularArc(new(3,-2),4,5.7,2.1),
    ["arc-reverse-seam"]=SketchCurve2d.CircularArc(new(3,-2),4,.5,-2.1),
    ["ellipse-forward"]=SketchCurve2d.EllipticArc(new(3,-2),5,3,.7,3.7,.4),
    ["ellipse-reverse"]=SketchCurve2d.EllipticArc(new(3,-2),5,3,.7,-3.7,.4),
    ["rational-bezier"]=SketchCurve2d.Bezier([new(0,0),new(2,4),new(4,7),new(6,6),new(8,3),new(10,0)],[1,.7,1.1,.8,1.2,1]).Trim(.2,.85),
    ["rational-spline"]=SketchCurve2d.BSpline([new(0,0),new(2,4),new(4,7),new(6,6),new(8,3),new(10,0)],[0,.35,1],[5,1,5],4,weights:[1,.7,1.1,.8,1.2,1]).Trim(.1,.9),
    ["periodic-rational"]=SketchCurve2d.BSpline([new(0,0),new(4,4),new(8,0),new(4,-4)],[0,1,2,3,4],[1,1,1,1,1],3,true,[1,.7,1.2,.8])
};
var rows=new List<object>();
void Near(double actual,double expected,string name)
{
    if(!double.IsFinite(actual)||!double.IsFinite(expected)||Math.Abs(actual-expected)>1e-6*Math.Max(1,Math.Abs(expected)))
        throw new InvalidOperationException($"{name}: {actual:R} != {expected:R}");
}
double Derivative(Func<double,double> value,double parameter,double h)=>
    (8*(value(parameter+h)-value(parameter-h))-(value(parameter+2*h)-value(parameter-2*h)))/(12*h);
foreach(var(name,definition) in cases)
foreach(bool reversed in new[]{false,true})
{
    var curve=reversed?definition.Reverse():definition;
    using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);
    if(!edge.IsValid)throw new InvalidOperationException("Invalid native edge: "+name);
    var interval=edge.GetEdgeCurveSnapshot();double range=interval.LastParameter-interval.FirstParameter;
    double sign=curve.Reversed?-1:1,scale=sign*range;
    foreach(double t in new[]{.07,.27,.57,.91})
    {
        double u=interval.FirstParameter+(curve.Reversed?1-t:t)*range,h=range*1e-4;
        if(u-2*h<=interval.FirstParameter||u+2*h>=interval.LastParameter)throw new InvalidOperationException("Numerical oracle would leave finite domain.");
        var jet=edge.EvaluateEdgeDerivatives(u);
        double ThirdX(double step)=>Derivative(v=>edge.EvaluateEdgeDerivatives(v).SecondDerivative.X,u,step);
        double ThirdY(double step)=>Derivative(v=>edge.EvaluateEdgeDerivatives(v).SecondDerivative.Y,u,step);
        double nativeX=ThirdX(h/2),nativeY=ThirdY(h/2);
        Near(nativeX,ThirdX(h),name+" third step convergence X");Near(nativeY,ThirdY(h),name+" third step convergence Y");
        if(curve.Kind is SketchCurveKind.Circle or SketchCurveKind.Ellipse)
        {
            double angle=curve.FirstParameter+(curve.Reversed?1-t:t)*(curve.LastParameter-curve.FirstParameter);
            double cosine=Math.Cos(curve.AxisAngle),sine=Math.Sin(curve.AxisAngle);
            Near(nativeX,curve.MajorRadius*Math.Sin(angle)*cosine+curve.MinorRadius*Math.Cos(angle)*sine,name+" analytic third X");
            Near(nativeY,curve.MajorRadius*Math.Sin(angle)*sine-curve.MinorRadius*Math.Cos(angle)*cosine,name+" analytic third Y");
        }
        double vx=jet.FirstDerivative.X*scale,vy=jet.FirstDerivative.Y*scale;
        double ax=jet.SecondDerivative.X*scale*scale,ay=jet.SecondDerivative.Y*scale*scale;
        double jx=nativeX*scale*scale*scale,jy=nativeY*scale*scale*scale;
        double speed=double.Hypot(vx,vy),cross=vx*ay-vy*ax;
        double curvatureDerivative=(vx*jy-vy*jx)/(speed*speed*speed)-3*cross*(vx*ax+vy*ay)/Math.Pow(speed,5);
        double Curvature(double parameter)
        {
            var g=edge.EvaluateEdgeDerivatives(interval.FirstParameter+(curve.Reversed?1-parameter:parameter)*range);
            double s=double.Hypot(g.FirstDerivative.X,g.FirstDerivative.Y);
            return sign*(g.FirstDerivative.X*g.SecondDerivative.Y-g.FirstDerivative.Y*g.SecondDerivative.X)/(s*s*s);
        }
        double observed=Derivative(Curvature,t,1e-4);
        Near(curvatureDerivative,observed,name+" signed curvature parameter derivative");
        rows.Add(new{name,reversed,t,first=new{X=vx,Y=vy},second=new{X=ax,Y=ay},thirdNumericalOracle=new{X=jx,Y=jy},curvatureDerivative,independentCurvatureDerivative=observed});
    }
}
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
    .Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),Path.Combine(AppContext.BaseDirectory,"occt"),StringComparison.OrdinalIgnoreCase))
    .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
if(modules.Length!=62)throw new InvalidOperationException("Unexpected isolated native payload: "+modules.Length);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
File.WriteAllText(args[0],JsonSerializer.Serialize(new{passed=true,
    scope="SDK-only numerical parameter derivative oracle using native edge D1/D2; five-point third derivative checked at two steps, conics independently analytic, curvature derivative independently differenced. No application implementation or exact native D3 API claim.",
    rows,nativeModules=modules},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Sliding contact SDK numerical derivative oracle PASS: {rows.Count} finite directed samples; 62 isolated native modules. Application not implemented.");
