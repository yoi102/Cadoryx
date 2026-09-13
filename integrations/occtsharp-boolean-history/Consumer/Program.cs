using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

using var box=ShapeFactory.CreateBox(10,20,30);
using var tool=ShapeFactory.CreateBox(2,22,32);
using var transform=GpTrsf.Create(4,-1,-1);
using var placed=tool.Transformed(transform);
using var source=RepairSnapshot.Create(box);
using var cutter=RepairSnapshot.Create(placed);
using var result=BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut,new[]{source,cutter});
source.Dispose();cutter.Dispose();box.Dispose();placed.Dispose();tool.Dispose();
double volume=Math.Abs(result.RequireShape().InspectProperties(InspectionPropertyKind.Volume).Mass);
if(Math.Abs(volume-4800)>1e-6||!result.RequireShape().IsValid)throw new InvalidOperationException("Incorrect Boolean output.");
bool split=result.History.Where(h=>h.Kind==LocalFeatureHistoryKind.Modified&&h.Source?.ArgumentIndex==0&&h.Source?.Kind==ShapeKind.Face)
    .GroupBy(h=>h.Source).Any(g=>g.Select(h=>h.ResultTopologyIndex).Distinct().Count()>1);
if(!split||!result.History.Any(h=>h.Source?.ArgumentIndex==1))throw new InvalidOperationException("Missing per-input history.");
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m=>m.ModuleName.Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase));
string hash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(native.FileName)));
if(hash!="f719823bbb12cb19650656993217744c59feef54903e57951877d28605db8f00")throw new InvalidOperationException("The consumer loaded a different native bridge.");
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,volume,split,relations=result.History.Count,native=native.FileName,nativeSha256=hash,
    module=typeof(BooleanHistoryModeling).Assembly.Location}));
