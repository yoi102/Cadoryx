using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length==2&&args[1]=="profile"){NativeThreadProfileProbe.Run(args[0]);return;}
if(args.Length==2&&args[1]=="boundary"){NativeThreadBoundaryProbe.Run(args[0]);return;}
if(args.Length==2&&args[1]=="history"){NativeThreadHistoryProbe.Run(args[0]);return;}
if(args.Length==2&&args[1]=="finite-history"){NativeThreadHistoryProbe.Run(args[0],finite:true);return;}
if(args.Length!=1)throw new ArgumentException("Usage: ThreadHelixProbe output-directory [profile]");
string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var rows=new List<object>();var failures=new List<object>();
foreach(int hand in new[]{1,-1})foreach(double length in new[]{3.25,5d})
{
    string label=$"{(hand==1?"right":"left")}-{length}";
    try
    {
        const double radius=2.75,pitch=1,tolerance=1e-7;
        using var support=SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,SketchPlane.XY,
            new(0,Math.Tau,0,length),radius:radius,tolerance:tolerance);
        // An exact UV line on the analytic cylinder; no sampled spine authoring.
        var uv=SketchCurve2d.Segment(new(0,0),new(hand*Math.Tau*length/pitch,length));
        using var edge=SurfaceModeling.LiftCurve(support,uv,build3d:true,tolerance:tolerance);
        var pcurve=edge.GetPcurveSnapshot(support);var curve=edge.GetEdgeCurveSnapshot();
        double maxPointError=0,maxUvError=0,maxTangentError=0;
        for(int i=0;i<=2048;i++)
        {
            double fraction=i/2048d,param=curve.FirstParameter+(curve.LastParameter-curve.FirstParameter)*fraction;
            var p=edge.EvaluateEdge(param);var actualUv=edge.EvaluatePcurve(support,param);
            double angle=hand*Math.Tau*length/pitch*fraction,z=length*fraction;
            maxPointError=Math.Max(maxPointError,Distance(p.Point,new(radius*Math.Cos(angle),radius*Math.Sin(angle),z)));
            maxUvError=Math.Max(maxUvError,Math.Sqrt(Math.Pow(actualUv.Point.X-angle,2)+Math.Pow(actualUv.Point.Y-z,2)));
            double norm=Math.Sqrt(radius*radius*Math.Tau*Math.Tau+pitch*pitch);
            maxTangentError=Math.Max(maxTangentError,Distance(p.Tangent,new(-hand*radius*Math.Tau*Math.Sin(angle)/norm,hand*radius*Math.Tau*Math.Cos(angle)/norm,pitch/norm)));
        }
        if(maxPointError>1e-6||maxUvError>1e-9||maxTangentError>2e-5)throw new InvalidOperationException($"Helix deviations: point {maxPointError:R}, UV {maxUvError:R}, tangent {maxTangentError:R}.");
        using var spine=SurfaceModeling.CreateWire(support,[edge],tolerance);
        const double inner=2.45,outer=3,halfWidth=.30;
        using var section=ShapeFactory.CreatePolygonWire([new(inner,0,-halfWidth),new(outer,0,0),new(inner,0,halfWidth)],close:true);
        using var plan=GuidedSweepPlan.Create(spine,[new(section)],new(){Frame=GuidedSweepFrame.Frenet,
            SolidPolicy=SweepSolidPolicy.RequireSolid,Tolerance3d=1e-6,ToleranceBoundary=1e-6,ToleranceAngular=1e-6,MaximumSegments=256});
        using var swept=plan.Build();var cutter=swept.RequireShape();
        if(!cutter.IsValid||cutter.GetTopologySummary().UniqueCounts.SolidCount!=1)throw new InvalidOperationException("Helical cutter not one valid solid.");
        var diagnostics=swept.Diagnostics;
        var self=cutter.GetSelfInterferenceReport();
        if(!self.IsClear)throw new InvalidOperationException("Helical cutter self-interference not clear.");
        double area=(outer-inner)*halfWidth,meanR=(2*inner+outer)/3;
        double expected=Math.Tau*length/pitch*area*meanR;
        double volume=cutter.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
        if(Math.Abs(volume-expected)>1e-3)throw new InvalidOperationException($"Screw sweep volume {volume:R} != analytic {expected:R}.");
        // Pilot cylinder surrounds the entire swept depth including both terminal caps.
        using var block=ShapeFactory.CreateBox(10,10,length+2);
        using var center=GpTrsf.Create(5,5,1);using var located=cutter.Transformed(center);
        using var pilot=ShapeFactory.CreateCylinder(2.5,length+2);
        using var pilotPlace=GpTrsf.Create(5,5,0);using var pilotTool=pilot.Transformed(pilotPlace);
        using var drilled=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[block],[pilotTool],new(){NonDestructive=true,RunParallel=false});
        using var threaded=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[drilled.RequireShape()],[located],new(){NonDestructive=true,RunParallel=false});
        var result=threaded.RequireShape();if(!result.IsValid||result.GetTopologySummary().UniqueCounts.SolidCount!=1)throw new InvalidOperationException("Threaded block not one valid solid.");
        double before=drilled.RequireShape().InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
        double after=result.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
        // Integrate the part of a straight triangular profile outside the pilot circle.
        double outsideWidth=halfWidth*(outer-2.5)/(outer-inner);
        double outsideArea=(outer-2.5)*outsideWidth,outsideMeanR=(2*2.5+outer)/3;
        double expectedRemoval=Math.Tau*length/pitch*outsideArea*outsideMeanR;
        if(Math.Abs(before-after-expectedRemoval)>2e-3)throw new InvalidOperationException($"Thread removed volume {before-after:R} != analytic {expectedRemoval:R}.");
        ShapeExchange.WriteBrep(result,Path.Combine(output,label+".brep"));
        rows.Add(new{label,hand,pitch,length,radius,maxPointError,maxUvError,maxTangentError,curveType=curve.CurveType.ToString(),
            pcurve,diagnostics,volume,expected,removed=before-after,expectedRemoval,faces=result.FaceCount,solidCount=result.GetTopologySummary().UniqueCounts.SolidCount,
            boundary="Exact analytic cylinder / linear pcurve. BuildCurve3d and sweep representations have explicit tolerances; sampled deviations are evidence, not a certified global maximum."});
        Console.WriteLine($"PASS {label}: helix {maxPointError:R}, sweep {volume:R}, removal {before-after:R}");
    }
    catch(Exception e){failures.Add(new{label,error=e.ToString()});Console.WriteLine($"FAIL {label}: {e.Message}");}
}
string nativeDir=Path.Combine(AppContext.BaseDirectory,"occt");var names=Directory.GetFiles(nativeDir,"*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>names.Contains(m.ModuleName)).ToArray();
if(modules.Any(m=>!string.Equals(Path.GetDirectoryName(m.FileName),nativeDir,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Foreign native module.");
if(Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Length!=0)throw new InvalidOperationException("Application DLL in independent consumer.");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=failures.Count==0,total=rows.Count+failures.Count,runtime=OcctRuntime.Info,
    results=rows,failures,nativeModules=modules.Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))})},new JsonSerializerOptions{WriteIndented=true}));
if(failures.Count!=0)Environment.ExitCode=1;
static double Distance(GpPoint a,GpPoint b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2)+Math.Pow(a.Z-b.Z,2));
