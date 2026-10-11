using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Kernel.Occt;
using OcctSharp;

/// <summary>NuGet-only proof of the elementary maps needed for composed drilling history.
/// This probe does not claim that Cadoryx has integrated the composition contract yet.</summary>
internal static class ComposedHoleHistoryProbe
{
    private sealed record Entry(int Argument,int Source,ShapeKind SourceKind,LocalFeatureHistoryKind Evolution,int? Target,ShapeKind? TargetKind);
    private sealed record Stage(string SourceFingerprint,string ResultFingerprint,int Arguments,int SourceCount,int ResultCount,Entry[] Entries);
    internal static int Run(string[] args)
    {
        var path=Path.GetFullPath(args[0]);var folder=Path.Combine(Path.GetDirectoryName(path)!,"composed-history-fixtures");Directory.CreateDirectory(folder);
        var results=new List<object>();
        try
        {
            foreach(bool imported in new[]{false,true})foreach(bool through in new[]{false,true})foreach(bool sink in new[]{false,true})
            {
                string name=$"{(imported?"imported":"authored")}-{(sink?"sink":"bore")}-{(through?"through":"blind")}";
                using var box=ShapeFactory.CreateBox(40,30,15);
                Shape original;
                if(imported){string step=Path.Combine(folder,name+".step");ShapeExchange.WriteStep(box,step);original=ShapeExchange.ReadStep(step);}
                else original=box.Clone();
                using(original)
                using(var source=RepairSnapshot.Create(original))
                {
                    var sourceFingerprint=source.Fingerprint;var point=new GpXyz(10,10,15);var axis=new GpXyz(0,0,-1);
                    Shape head;Stage first;
                    if(sink)
                    {
                        using var cone=ShapeFactory.CreateCone(2,1,1);using var placement=GpTrsf.Create(10,10,15,1,0,0,Math.PI);
                        using var tool=cone.Transformed(placement);using var cutter=RepairSnapshot.Create(tool);
                        using var operation=BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut,[source,cutter]);
                        (head,first)=Inspect([source,cutter],operation,Path.Combine(folder,name+"-head.brep"));
                    }
                    else
                    {
                        using var operation=LocalFeatures.Hole(source,new(LocalHoleMode.Blind,point,axis,2,Last:2));
                        (head,first)=Inspect([source],operation,Path.Combine(folder,name+"-head.brep"));
                    }
                    using(head)
                    using(var intermediate=RepairSnapshot.Create(head))
                    using(var operation=LocalFeatures.Hole(intermediate,new(through?LocalHoleMode.ThroughAll:LocalHoleMode.Blind,point,axis,1,Last:6)))
                    {
                        var (final,second)=Inspect([intermediate],operation,Path.Combine(folder,name+"-final.brep"));
                        using(final)
                        {
                            Require(first.ResultFingerprint==second.SourceFingerprint,"Intermediate identity was changed.");
                            var stable=source.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge)
                                .Select(t=>(t.Kind,Target:Unique(first,t.Selection.Index,t.Kind)))
                                .Where(t=>t.Target is not null)
                                .Select(t=>(t.Kind,Target:Unique(second,t.Target!.Value,t.Kind)))
                                .Where(t=>t.Target is not null).ToArray();
                            Require(stable.Any(t=>t.Kind==ShapeKind.Face)&&stable.Any(t=>t.Kind==ShapeKind.Edge),"No unchanged original faces/edges survive both native stages.");
                            using var finalMap=RepairSnapshot.Create(final);var hole=InspectHole(final,finalMap,1);
                            Require(hole.Head==(sink?2:1)&&hole.Through==through&&Math.Abs(hole.Depth-(through?15:6))<1e-6,"Final hole dimensions differ.");
                            using var unchanged=RepairSnapshot.Create(original);Require(unchanged.Fingerprint==sourceFingerprint,"Source changed during drilling.");
                            results.Add(new{name,stages=2,first.Arguments,originalFaceEdges=stable.Length,firstEntries=first.Entries.Length,secondEntries=second.Entries.Length,finalHole=hole,sourceUnchanged=true,normalizedStagesVerified=true});
                        }
                    }
                }
            }
            using(var box=ShapeFactory.CreateBox(40,30,15))
            using(var baseMap=RepairSnapshot.Create(box))
            using(var seed=LocalFeatures.Hole(baseMap,new(LocalHoleMode.ThroughAll,new(5,5,15),new(0,0,-1),1)))
            {
                var (original,seedStage)=Inspect([baseMap],seed,Path.Combine(folder,"pattern-seed.brep"));
                using(original)
                using(var source=RepairSnapshot.Create(original))
                {
                    var author=InspectHole(original,source,1);
                    Shape current=original.Clone();var wall=author.ShaftFace;var stages=new List<Stage>();
                    try
                    {
                        foreach(double x in new[]{15d,25d,35d})
                        {
                            using var map=RepairSnapshot.Create(current);using var operation=LocalFeatures.Hole(map,new(LocalHoleMode.ThroughAll,new(x,5,15),new(0,0,-1),1));
                            var (next,stage)=Inspect([map],operation,Path.Combine(folder,$"pattern-{x}.brep"));
                            current.Dispose();current=next;stages.Add(stage);
                            wall=Unique(stage,wall,ShapeKind.Face)??throw new InvalidOperationException("Generated seed shaft has no unique proven successor.");
                        }
                        using var finalMap=RepairSnapshot.Create(current);var proof=NativeHoleInspector.Inspect(current,wall);
                        Require(Math.Abs(proof.Diameter-2)<1e-6&&proof.Through&&Math.Abs(proof.Depth-15)<1e-6,"Pattern changed the seed hole.");
                        results.Add(new{name="generated-shaft-through-three-drilling-stages",stages=stages.Count,originalIndex=author.ShaftFace,currentIndex=wall,seedFingerprint=seedStage.ResultFingerprint,finalFingerprint=finalMap.Fingerprint,finalHole=proof});
                    }
                    finally{current.Dispose();}
                }
            }
            // Circular replay rotates the cone's analytic frame as well as its entry.
            // Keep a separate witness so a normalization failure cannot be hidden by
            // the simpler axis-aligned prerequisites above.
            using(var plate=ShapeFactory.CreateBox(30,30,5))
            using(var source=RepairSnapshot.Create(plate))
            {
                double angle=Math.Tau/3,x=10-5*Math.Cos(angle)+5*Math.Sin(angle),y=10-5*Math.Sin(angle)-5*Math.Cos(angle);
                using var cone=ShapeFactory.CreateCone(2,1,1);
                using var placement=GpTrsf.Create(x,y,5,Math.Cos(angle/2),Math.Sin(angle/2),0,Math.PI);
                using var tool=cone.Transformed(placement);using var cutter=RepairSnapshot.Create(tool);
                using var head=BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut,[source,cutter]);
                bool rejected=false;string witness=Path.Combine(folder,"circular-sink-head.brep");
                try{var checkedStage=Inspect([source,cutter],head,witness);checkedStage.Shape.Dispose();}
                catch(InvalidOperationException error) when(error.Message=="Normalized topology slot differs."){rejected=true;}
                Require(rejected,"The circular pcurve normalization boundary changed; re-audit its original locators.");
                using(var intermediate=ShapeExchange.ReadBrep(witness))
                using(var map=RepairSnapshot.Create(intermediate))
                using(var shaft=LocalFeatures.Hole(map,new(LocalHoleMode.ThroughAll,new(x,y,5),new(0,0,-1),1)))
                {
                    var final=shaft.RequireShape();Require(final.IsValid,"Circular cone geometry is invalid.");
                    using var finalMap=RepairSnapshot.Create(final);var hole=InspectHole(final,finalMap,1);
                    Require(hole.Head==2&&hole.Through,"Circular cone lost its final manufacturing proof.");
                    results.Add(new{name="circular-sink-pcurve-normalization-rejected",stages=2,historyAvailable=false,
                        geometryValid=true,reason="The original indexed pcurve changes on normalization; no composed locator is authorized.",
                        before=witness+".a",after=witness+".b",finalHole=hole});
                }
            }
            string moduleRoot=Path.GetFullPath(args[1]);var names=new HashSet<string>(Directory.GetFiles(moduleRoot,"*.dll").Select(Path.GetFileName)!,StringComparer.OrdinalIgnoreCase);
            var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>names.Contains(m.ModuleName)).ToArray();
            Require(modules.Any(m=>m.ModuleName.Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase)),"Native bridge is not loaded.");
            foreach(var module in modules)Require(Path.GetDirectoryName(module.FileName)==moduleRoot,"Unexpected native module path.");
            var nativeModules=modules.Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
            File.WriteAllText(path,JsonSerializer.Serialize(new{passed=true,runtime=OcctRuntime.Info,nativeModules,scope="Elementary native history and BRep normalization prerequisite only; application composition is pending",results},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(path,JsonSerializer.Serialize(new{passed=false,error=error.ToString(),results},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
    private static (Shape Shape,Stage History) Inspect(RepairSnapshot[] sources,LocalFeatureResult operation,string path)
    {
        Require(operation.Diagnostics.AlgorithmDone&&!operation.Diagnostics.HasComposedHistory&&operation.Diagnostics.GroupSupport.HasFlag(LocalFeatureGroupSupport.Evolution),"Native evolution map is unavailable.");
        var liveShape=operation.RequireShape();ShapeExchange.WriteBrep(liveShape,path);using(var first=ShapeExchange.ReadBrep(path))ShapeExchange.WriteBrep(first,path);
        Shape normalized=ShapeExchange.ReadBrep(path);
        try
        {
            using var live=RepairSnapshot.Create(liveShape);using var stored=RepairSnapshot.Create(normalized);
            Require(live.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex)).SequenceEqual(stored.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex))),"Normalized topology graph differs.");
            foreach(var item in live.Topology)
            {
                using var a=live.CopySubshape(item.Selection);using var b=stored.CopySubshape(stored.Select(item.Selection.Index));
                Require(BrepDirectionRoundtrip.Matches(Canonical(a,path+".a"),Canonical(b,path+".b")),"Normalized topology slot differs.");
            }
            var entries=new List<Entry>();
            foreach(var relation in operation.History)
            {
                var origin=relation.Source??throw new InvalidOperationException("Native relation has no source.");
                Require(origin.PlanId==operation.PlanId&&origin.ArgumentIndex>=0&&origin.ArgumentIndex<sources.Length&&origin.TopologyIndex>=0&&origin.TopologyIndex<sources[origin.ArgumentIndex].Topology.Count&&sources[origin.ArgumentIndex].Topology[origin.TopologyIndex].Kind==origin.Kind,"Wrong native origin.");
                int? target=relation.ResultTopologyIndex;Require(target is null||target>=0&&target<stored.Topology.Count,"Wrong native result.");
                entries.Add(new(origin.ArgumentIndex,origin.TopologyIndex,origin.Kind,relation.Kind,target,target is {} index?stored.Topology[index].Kind:null));
            }
            return(normalized,new(sources[0].Fingerprint,stored.Fingerprint,sources.Length,sources[0].Topology.Count,stored.Topology.Count,entries.Distinct().ToArray()));
        }
        catch{normalized.Dispose();throw;}
    }
    private static int? Unique(Stage stage,int index,ShapeKind kind)
    {
        var entries=stage.Entries.Where(e=>e.Argument==0&&e.Source==index&&e.SourceKind==kind).ToArray();
        if(entries.Length==0||entries.Any(e=>e.Evolution is LocalFeatureHistoryKind.Unmapped or LocalFeatureHistoryKind.Deleted))return null;
        var candidates=entries.Where(e=>e.Evolution is LocalFeatureHistoryKind.Unchanged or LocalFeatureHistoryKind.Modified&&e.TargetKind==kind&&e.Target is not null).Select(e=>e.Target!.Value).Distinct().ToArray();
        if(candidates.Length!=1||stage.Entries.Any(e=>(e.Argument!=0||e.Source!=index)&&e.Target==candidates[0]))return null;
        return candidates[0];
    }
    private static NativeHoleInspector.Evidence InspectHole(Shape shape,RepairSnapshot map,double radius)
    {
        foreach(var slot in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            using var face=map.CopySubshape(slot.Selection);var snapshot=face.GetFaceSurfaceSnapshot();
            if(snapshot.SurfaceType!=SurfaceGeometryType.Cylinder)continue;
            try
            {
                var proof=NativeHoleInspector.Inspect(shape,slot.Selection.Index);
                if(Math.Abs(proof.Diameter-2*radius)<1e-6)return proof;
            }
            catch(InvalidOperationException){}
        }
        throw new InvalidOperationException("Complete inward shaft proof is unavailable.");
    }
    private static string Canonical(Shape shape,string path)
    {ShapeExchange.WriteBrep(shape,path);using var roundtrip=ShapeExchange.ReadBrep(path);ShapeExchange.WriteBrep(roundtrip,path);return File.ReadAllText(path);}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
