using System.Diagnostics;
using System.Text.Json;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using MathNet.Numerics.Providers.LinearAlgebra;

if(args.Length!=2)throw new ArgumentException("Usage: SketchProbe <cadoryx document> <result.json>");
var assets=new MemoryAssetStore();var solver=new ManagedSketchConstraintSolver();var reports=new List<object>();
using(var loaded=await new CadDocumentStorage().LoadAsync(args[0],assets))
{
    if(loaded.Snapshot.Sketches.Count==0)throw new InvalidDataException("The consumer fixture requires a sketch.");
    foreach(var sketch in loaded.Snapshot.Sketches.Values)
    {
        var report=await solver.SolveAsync(sketch);if(!report.Succeeded)throw new InvalidDataException(report.Diagnostic);
        reports.Add(new{sketchId=sketch.Id,status=report.Status.ToString(),report.DegreesOfFreedom,report.Rank,report.VariableCount,report.EquationCount,report.MaxNormalizedResidual});
    }
}
using var process=Process.GetCurrentProcess();
bool nativeKernelLoaded=process.Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Contains("OcctSharp",StringComparison.OrdinalIgnoreCase));
bool managedProvider=LinearAlgebraControl.Provider is ManagedLinearAlgebraProvider;
if(nativeKernelLoaded||!managedProvider||assets.Count!=0)throw new InvalidOperationException("Managed consumer isolation or asset release failed.");
string result=JsonSerializer.Serialize(new{solver=solver.Version,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    linearAlgebraProvider=LinearAlgebraControl.Provider.GetType().FullName,nativeKernelLoaded,remainingAssets=assets.Count,reports},new JsonSerializerOptions{WriteIndented=true});
string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);await File.WriteAllTextAsync(output,result);Console.WriteLine(result);
