using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length!=1)throw new ArgumentException("Usage: MixedContactGeometryProbe output.json");
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
    if(!double.IsFinite(actual)||!double.IsFinite(expected)||Math.Abs(actual-expected)>1e-7*Math.Max(1,Math.Abs(expected)))
        throw new InvalidOperationException($"{name}: {actual:R} != {expected:R}");
}
foreach(var (name,definition) in cases)
foreach(bool reversed in new[]{false,true})
{
    var curve=reversed?definition.Reverse():definition;
    using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);
    if(!edge.IsValid)throw new InvalidOperationException("Invalid native edge: "+name);
    var interval=edge.GetEdgeCurveSnapshot();double range=interval.LastParameter-interval.FirstParameter;
    foreach(double t in new[]{.07,.27,.57,.91})
    {
        var value=curve.Evaluate(t);
        var oracle=edge.EvaluateEdgeDerivatives(interval.FirstParameter+(curve.Reversed?1-t:t)*range);
        double direction=curve.Reversed?-1:1;
        Near(value.Point.X,oracle.Point.X,name+" position X");Near(value.Point.Y,oracle.Point.Y,name+" position Y");
        Near(value.FirstDerivative.X,oracle.FirstDerivative.X*direction,name+" derivative X");
        Near(value.FirstDerivative.Y,oracle.FirstDerivative.Y*direction,name+" derivative Y");
        if(curve.Kind is SketchCurveKind.Circle or SketchCurveKind.Ellipse)
        {
            double u=curve.FirstParameter+(curve.Reversed?1-t:t)*(curve.LastParameter-curve.FirstParameter);
            double cosine=Math.Cos(curve.AxisAngle),sine=Math.Sin(curve.AxisAngle);
            Near(oracle.SecondDerivative.X,-curve.MajorRadius*Math.Cos(u)*cosine+curve.MinorRadius*Math.Sin(u)*sine,name+" analytic second X");
            Near(oracle.SecondDerivative.Y,-curve.MajorRadius*Math.Cos(u)*sine-curve.MinorRadius*Math.Sin(u)*cosine,name+" analytic second Y");
        }
        double speed=double.Hypot(oracle.FirstDerivative.X,oracle.FirstDerivative.Y);
        double curvature=direction*(oracle.FirstDerivative.X*oracle.SecondDerivative.Y-oracle.FirstDerivative.Y*oracle.SecondDerivative.X)/(speed*speed*speed);
        if(!double.IsFinite(curvature))throw new InvalidOperationException("Nonfinite native curvature.");
        rows.Add(new{name,reversed,t,point=value.Point,first=value.FirstDerivative,second=oracle.SecondDerivative,curvature});
    }
}
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),Path.Combine(AppContext.BaseDirectory,"occt"),StringComparison.OrdinalIgnoreCase))
    .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
if(modules.Length!=62)
{
    File.WriteAllText(Path.ChangeExtension(args[0],"modules-failed.json"),JsonSerializer.Serialize(modules,new JsonSerializerOptions{WriteIndented=true}));
    throw new InvalidOperationException("Unexpected isolated native payload: "+modules.Length);
}
File.WriteAllText(args[0],JsonSerializer.Serialize(new{passed=true,rows,nativeModules=modules},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Mixed contact SDK derivatives PASS: {rows.Count} directed finite samples; 62 isolated native modules.");
