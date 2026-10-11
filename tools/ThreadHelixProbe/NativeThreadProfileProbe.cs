using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Kernel.Occt;
using OcctSharp;

internal static class NativeThreadProfileProbe
{
    public static void Run(string directory)
    {
        string output=Path.GetFullPath(directory);Directory.CreateDirectory(output);
        var rows=new List<object>();var failures=new List<object>();var negatives=new List<object>();
        foreach(var size in new[]{(3,.5,2.5),(4,.7,3.3),(5,.8,4.2),(6,1d,5d),(8,1.25,6.8),(10,1.5,8.5),(12,1.75,10.2),(16,2d,14d)})
        foreach(int hand in new[]{1,-1})foreach(bool through in new[]{false,true})
        {
            var(nominal,pitch,pilot)=size;string label=$"M{nominal}-{(hand==1?"right":"left")}-{(through?"through":"blind")}";
            try
            {
                double height=8*pitch,length=through?height:4.25*pitch,pilotDepth=through?height:6*pitch;
                using var tool=NativeMetricThreadTool.Build(nominal,pilot,pitch,hand,length,out var proof);
                using var block=ShapeFactory.CreateBox(nominal*3,nominal*3,height);
                using var center=GpTrsf.Create(nominal*1.5,nominal*1.5,0);
                using var located=tool.Transformed(center);
                using var pilotShape=ShapeFactory.CreateCylinder(pilot/2,pilotDepth);
                using var pilotTool=pilotShape.Transformed(center);
                using var drilling=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[block],[pilotTool],new(){NonDestructive=true,RunParallel=false});
                using var cutting=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[drilling.RequireShape()],[located],new(){NonDestructive=true,RunParallel=false});
                var result=cutting.RequireShape();var topology=result.GetTopologySummary();
                if(!topology.IsValid||topology.UniqueCounts.SolidCount!=1||!result.GetSelfInterferenceReport().IsClear)
                    throw new InvalidOperationException("Threaded target validity or self-interference failed.");
                double drilled=drilling.RequireShape().InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
                double final=result.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
                double expected=NativeMetricThreadTool.RemovedVolume(nominal,pilot,pitch,length);
                double independentBlock=nominal*3*nominal*3*height-Math.PI*pilot*pilot/4*pilotDepth;
                if(Math.Abs(drilled-independentBlock)>1e-4)
                    throw new InvalidOperationException($"Pilot volume {drilled:R} != independently derived {independentBlock:R}.");
                if(Math.Abs(drilled-final-expected)>Math.Max(1e-3,expected*1e-5))
                    throw new InvalidOperationException($"Removed volume {drilled-final:R} != independent screw integral {expected:R}.");
                var bounds=tool.GetBoundingBox();
                if(Math.Abs(bounds.Minimum.Z)>2e-5||Math.Abs(bounds.Maximum.Z-length)>2e-5)
                    throw new InvalidOperationException("Thread tool exceeded the authored axial interval.");
                string file=ShapeExchange.WriteBrep(result,Path.Combine(output,label+".brep"));
                using var reopened=ShapeExchange.ReadBrep(file);
                double recovered=reopened.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
                if(!reopened.IsValid||Math.Abs(recovered-final)>1e-6)throw new InvalidOperationException("Thread solid roundtrip failed.");
                rows.Add(new{label,nominal,pitch,pilot,hand,through,length,height,pilotDepth,proof,removed=drilled-final,expected,
                    faces=topology.UniqueCounts.FaceCount,edges=topology.UniqueCounts.EdgeCount,axialBounds=bounds,
                    brep=new{path=file,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}});
                Console.WriteLine($"PASS {label}: {drilled-final:R} / {expected:R}");
            }
            catch(Exception e){failures.Add(new{label,error=e.ToString()});Console.WriteLine($"FAIL {label}: {e.Message}");}
        }
        foreach(var invalid in new[]{("short",6d,5d,1d,1,.5),("turn-budget",6d,5d,1d,1,65d),("nonfinite",double.NaN,5d,1d,1,5d),
            ("hand",6d,5d,1d,0,5d),("pilot",6d,6d,1d,1,5d),("overlapping-profile",6d,1d,1d,1,5d)})
        {
            try{using var tool=NativeMetricThreadTool.Build(invalid.Item2,invalid.Item3,invalid.Item4,invalid.Item5,invalid.Item6,out _);throw new Exception("Invalid thread accepted.");}
            catch(InvalidOperationException e){negatives.Add(new{label=invalid.Item1,error=e.Message});}
        }
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();try{using var tool=NativeMetricThreadTool.Build(6,5,1,1,5,out _,canceled.Token);throw new Exception("Canceled thread accepted.");}
            catch(OperationCanceledException){negatives.Add(new{label="canceled",error="Canceled before native authoring"});}
        }
        string nativeDir=Path.Combine(AppContext.BaseDirectory,"occt");var names=Directory.GetFiles(nativeDir,"*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>names.Contains(m.ModuleName)).ToArray();
        if(modules.Any(m=>!string.Equals(Path.GetDirectoryName(m.FileName),nativeDir,StringComparison.OrdinalIgnoreCase))||Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Length!=0)
            throw new InvalidOperationException("Independent consumer loaded foreign payload.");
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=failures.Count==0,total=rows.Count+failures.Count,
            profile=NativeMetricThreadTool.Profile,runtime=OcctRuntime.Info,results=rows,failures,negatives,
            nativeModules=modules.Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))})},new JsonSerializerOptions{WriteIndented=true}));
        if(failures.Count!=0)Environment.ExitCode=1;
    }
}
