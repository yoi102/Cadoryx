using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var results=new List<object>();bool passed=true;
void Probe(string name,Func<object> run)
{try{results.Add(new{name,passed=true,detail=run()});}catch(Exception error){passed=false;results.Add(new{name,passed=false,error=error.ToString()});}}
foreach(int mode in new[]{0,1,2,3})Probe("complete-transform-"+mode,()=>
{
    using var box=ShapeFactory.CreateBox(4,3,2);using var source=RepairSnapshot.Create(box);
    using var transform=mode switch{0=>GpTrsf.Identity,1=>GpTrsf.Create(7,-2,3),2=>GpTrsf.Create(4,2,-1,2,1,3,.413),_=>GpTrsf.CreatePlaneMirror(3,2,-1,2,1,3)};
    using var result=ShapeHistoryModeling.Transform(source,transform);Check(result,[source]);
    using var after=RepairSnapshot.Create(box);Require(source.Fingerprint==after.Fingerprint,"Source mutated.");
    return new{tracked=Tracked(source).Count(),rows=result.History.Count,sourceUnchanged=true};
});
foreach(bool fused in new[]{false,true})Probe("compose-transform-"+(fused?"overlapping-fuse":"separated-compound"),()=>
{
    using var box=ShapeFactory.CreateBox(4,3,2);using var source=RepairSnapshot.Create(box);using var transform=GpTrsf.Create(fused?2:7,0,0);
    using var copied=ShapeHistoryModeling.Transform(source,transform);Check(copied,[source]);using var second=RepairSnapshot.Create(copied.RequireShape());
    using var result=fused?BooleanHistoryModeling.Build(TopologyBooleanOperation.Fuse,[source,second]):ShapeHistoryModeling.Compound([source,second]);Check(result,[source,second]);
    int composedRows=0;foreach(var first in copied.History)
    {
        var next=result.History.Where(h=>h.Source?.ArgumentIndex==1&&h.Source?.TopologyIndex==first.ResultTopologyIndex).ToArray();
        Require(next.Length>0,"Native transform target missing from actual Boolean/Compound input map.");composedRows+=next.Length;
    }
    double volume=result.RequireShape().InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
    Require(Math.Abs(volume-(fused?36:48))<1e-6,"Wrong physical result volume.");
    return new{volume,composedRows,actualSourceArguments=2,noGeometricMatching=true};
});
Probe("shared-target-retains-both-input-paths",()=>
{
    using var box=ShapeFactory.CreateBox(4,3,2);using var source=RepairSnapshot.Create(box);using var result=ShapeHistoryModeling.Compound([source,source]);Check(result,[source,source]);
    foreach(var slot in Tracked(source))
    {var rows=result.History.Where(h=>h.Source?.TopologyIndex==slot.Selection.Index).ToArray();Require(rows.Select(h=>h.Source!.Value.ArgumentIndex).Order().SequenceEqual(new[]{0,1}),"Lost ordered source path.");Require(rows.Select(h=>h.ResultTopologyIndex).Distinct().Count()==1,"Shared native target changed.");}
    return new{rows=result.History.Count,multipleSourcePaths=true};
});
Probe("owners-outlive-input-and-reflection",()=>
{
    LocalFeatureResult result;using(var box=ShapeFactory.CreateBox(4,3,2))using(var source=RepairSnapshot.Create(box))using(var mirror=GpTrsf.CreatePlaneMirror(0,0,0,0,0,1))result=ShapeHistoryModeling.Transform(source,mirror);
    using(result){Require(result.RequireShape().IsValid,"Disposed input invalidated result.");foreach(var row in result.History)Require(row.Shape!.IsValid,"Disposed input invalidated history owner.");return new{historyOwners=result.History.Count,independentLifetime=true};}
});
Probe("invalid-inputs-reject-before-publication",()=>
{
    int rejected=0;foreach(var inputs in new[]{Array.Empty<RepairSnapshot>(),new RepairSnapshot[257],new RepairSnapshot[]{null!}})
    {try{using var unexpected=ShapeHistoryModeling.Compound(inputs);}catch(ArgumentException){rejected++;}}
    Require(rejected==3,"Invalid input accepted.");return new{rejected};
});
PresentationMetadataProbe.Run(Probe,output);
var loaded=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)?.EndsWith(Path.DirectorySeparatorChar+"occt",StringComparison.OrdinalIgnoreCase)==true)
    .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).OrderBy(m=>m.path).ToArray();
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed,total=results.Count,results,runtime=OcctRuntime.Info,nativeModules=loaded,
    scope="Independent public immutable SDK prerequisite for full transform/compound/Boolean provenance. Cadoryx application, style inheritance, storage and external CAD remain unverified."},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("MULTICOPY_HISTORY_COMPLETE passed="+passed);Environment.ExitCode=passed?0:1;

static IEnumerable<RepairTopologyItem> Tracked(RepairSnapshot source)=>source.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex);
static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
static void Check(LocalFeatureResult result,IReadOnlyList<RepairSnapshot> inputs)
{
    var shape=result.RequireShape();Require(result.Diagnostics.GroupSupport.HasFlag(LocalFeatureGroupSupport.Evolution),"History unavailable.");
    using var map=RepairSnapshot.Create(shape);
    for(int argument=0;argument<inputs.Count;argument++)foreach(var slot in Tracked(inputs[argument]))
    {
        var rows=result.History.Where(h=>h.Source is {} s&&s.PlanId==result.PlanId&&s.ArgumentIndex==argument&&s.TopologyIndex==slot.Selection.Index&&s.Kind==slot.Kind).ToArray();
        Require(rows.Length>0,"Unaccounted native source slot.");
        foreach(var row in rows.Where(h=>h.ResultTopologyIndex.HasValue))Require(row.Shape is not null&&map.Topology[row.ResultTopologyIndex!.Value].Kind==row.Shape.Kind,"Wrong final native topology index.");
    }
}
