using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length!=1)throw new ArgumentException("Usage: EndpointCurvatureProbe output.json");
var results=new List<object>();
void Near(double a,double b,double tolerance,string message)
{if(!double.IsFinite(a)||!double.IsFinite(b)||Math.Abs(a-b)>tolerance)throw new InvalidOperationException($"{message}: {a:R} != {b:R}");}
object Verify(SketchCurve2d curve,bool start,double expectedCurvature,SketchPoint2d expectedTangent)
{
    using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);if(!edge.IsValid)throw new InvalidOperationException("Invalid native edge.");
    var interval=edge.GetEdgeCurveSnapshot();var author=curve.Evaluate(start?0:1);
    var actual=edge.EvaluateEdgeDerivatives(start!=curve.Reversed?interval.FirstParameter:interval.LastParameter);
    Near(actual.Point.X,author.Point.X,1e-7,"Native author endpoint X");Near(actual.Point.Y,author.Point.Y,1e-7,"Native author endpoint Y");
    var first=actual.FirstDerivative;var second=actual.SecondDerivative;
    double sign=(first.X*author.FirstDerivative.X+first.Y*author.FirstDerivative.Y<0?-1:1)*(start?1:-1),speed=double.Hypot(first.X,first.Y);
    double curvature=sign*(first.X*second.Y-first.Y*second.X)/(speed*speed*speed);
    Near(curvature,expectedCurvature,Math.Max(1e-8,Math.Abs(expectedCurvature)*1e-7),"Analytic endpoint curvature");
    Near(sign*first.X/speed,expectedTangent.X,1e-7,"Analytic inward tangent X");Near(sign*first.Y/speed,expectedTangent.Y,1e-7,"Analytic inward tangent Y");
    return new{actual.Parameter,point=actual.Point,first,second,curvaturePerMm=curvature};
}
foreach(string kind in new[]{"Bezier","Spline","Nonuniform"})foreach(bool reversed in new[]{false,true})foreach(double scale in new[]{.01,1,1000})
{
    double angle=scale==1?.7:0,translation=scale==1?1000000:0;
    SketchPoint2d P(double x,double y)=>new(translation+scale*(x*Math.Cos(angle)-y*Math.Sin(angle)),translation+scale*(x*Math.Sin(angle)+y*Math.Cos(angle)));
    var center=P(0,4);var arc=SketchCurve2d.CircularArc(center,4*scale,-Math.PI/2+angle,Math.PI/2);
    var parametric=kind switch
    {
        "Bezier"=>SketchCurve2d.Bezier([P(0,0),P(-3,0),P(-6,4.5)]),
        "Nonuniform"=>SketchCurve2d.BSpline([P(0,0),P(-2,0),P(-4,18),P(-5,3),P(-6,4)],[2,2.25,5],[4,1,4],3),
        _=>SketchCurve2d.BSpline([P(0,0),P(-2,0),P(-4,1.5),P(-6,4)],[0,1],[4,4],3)
    };
    if(reversed){arc=arc.Reverse();parametric=parametric.Reverse();}
    var a=Verify(arc,!reversed,.25/scale,new(Math.Cos(angle),Math.Sin(angle)));
    var b=Verify(parametric,!reversed,-.25/scale,new(-Math.Cos(angle),-Math.Sin(angle)));
    results.Add(new{kind,reversed,scale,angle,translation,arc=a,parametric=b});
}
foreach(bool reversed in new[]{false,true})
{
    var line=SketchCurve2d.Segment(new(0,0),new(-6,0));
    var spline=SketchCurve2d.BSpline([new(0,0),new(2,0),new(4,0),new(6,4)],[0,1],[4,4],3);
    if(reversed){line=line.Reverse();spline=spline.Reverse();}
    results.Add(new{kind="zero-curvature",reversed,line=Verify(line,!reversed,0,new(-1,0)),spline=Verify(spline,!reversed,0,new(1,0))});
}
using(var solid=SketchProfile2d.Create(SketchCurveChain2d.Create([
    SketchCurve2d.CircularArc(new(0,4),4,-Math.PI/2,Math.PI/2),
    SketchCurve2d.Segment(new(4,4),new(-6,4)),
    SketchCurve2d.BSpline([new(0,0),new(-2,0),new(-4,1.5),new(-6,4)],[0,1],[4,4],3).Reverse()],true)).Extrude(SketchPlane.XY,2))
{
    if(!solid.IsValid)throw new InvalidOperationException("Invalid exact mixed-loop solid.");
    double volume=solid.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
    Near(volume,31.5+8*Math.PI,1e-7,"Exact mixed-loop volume");results.Add(new{kind="mixed-loop",volume});
}
using(var edge=SketchModeling.CreateEdge(SketchCurve2d.Bezier([new(0,0),new(2,1),new(4,0)]),SketchPlane.XY))
{
    int rejected=0;foreach(double parameter in new[]{double.NaN,-1,2})
    {try{edge.EvaluateEdgeDerivatives(parameter);}catch(ArgumentException){rejected++;}}
    if(rejected!=3)throw new InvalidOperationException("Native derivative range guard failed.");results.Add(new{kind="range-guards",rejected});
}
string nativeDirectory=Path.Combine(AppContext.BaseDirectory,"occt");
var names=Directory.GetFiles(nativeDirectory,"*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>names.Contains(m.ModuleName)).ToArray();
if(native.Any(m=>!string.Equals(Path.GetDirectoryName(m.FileName),nativeDirectory,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Foreign native payload loaded.");
if(!native.Any(m=>m.ModuleName.Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Native bridge not loaded.");
var modules=native.Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
File.WriteAllText(Path.GetFullPath(args[0]),JsonSerializer.Serialize(new{passed=true,total=results.Count,runtime=OcctRuntime.Info,nativeModules=modules,results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Independent endpoint derivative consumer PASS: {results.Count} scenarios.");
