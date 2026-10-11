using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

internal static class MulticopyHistoryCapabilityProbe
{
    internal static int Run(string[] args)
    {
        string folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);
        var results=new List<object>();bool passed=true;
        void Probe(string name,Func<object> run)
        {try{results.Add(new{name,passed=true,detail=run()});}catch(Exception error){passed=false;results.Add(new{name,passed=false,error=error.ToString()});}}
        var modeling=typeof(BooleanHistoryModeling).Assembly;
        var historyApi=modeling.GetType("OcctSharp.ShapeHistoryModeling");
        bool transformAvailable=historyApi?.GetMethods(BindingFlags.Public|BindingFlags.Static).Any(m=>m.Name=="Transform"&&m.ReturnType==typeof(LocalFeatureResult))==true;
        bool compoundAvailable=historyApi?.GetMethods(BindingFlags.Public|BindingFlags.Static).Any(m=>m.Name=="Compound"&&m.ReturnType==typeof(LocalFeatureResult))==true;
        Probe("locked-public-contract-inventory",()=>new{
            assembly=modeling.FullName,transformAvailable,compoundAvailable,
            shapeTransformReturn=typeof(Shape).GetMethod("Transformed",[typeof(GpTrsf)])!.ReturnType.FullName,
            compoundReturn=typeof(ShapeFactory).GetMethod("CreateCompound")!.ReturnType.FullName,
            limitation="Shape-only transforms and compounds do not publish complete source/result correspondence. CopyTransformedDefinition publishes a label, not full topology history."});
        foreach(bool reflected in new[]{false,true})Probe("actual-compound-membership-"+(reflected?"reflection":"rigid"),()=>
        {
            using var source=ShapeFactory.CreateBox(4,3,2);using var before=RepairSnapshot.Create(source);
            using var native=reflected?GpTrsf.CreatePlaneMirror(0,0,0,0,0,1):GpTrsf.Create(7,0,0);
            using var copy=source.Transformed(native);using var compound=ShapeFactory.CreateCompound([source,copy]);
            using var map=RepairSnapshot.Create(compound);var members=new HashSet<int>();int independentCopiesRejected=0;
            foreach(var kind in new[]{ShapeKind.Face,ShapeKind.Edge,ShapeKind.Vertex})
            {
                var items=compound.GetSubShapes(kind);
                try{foreach(var item in items){int index=RepairSnapshot.FindTopologyIndex(compound,item);Require(index>=0&&map.Topology[index].Kind==kind,"Actual native member missing.");members.Add(index);}}
                finally{foreach(var item in items)item.Dispose();}
            }
            foreach(var slot in map.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
            {using var detached=map.CopySubshape(slot.Selection);Require(RepairSnapshot.FindTopologyIndex(compound,detached)<0,"Independent repaired shape mistaken for compound membership.");independentCopiesRejected++;}
            Require(members.Count==map.Topology.Count(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex),"Incomplete member graph.");
            using var after=RepairSnapshot.Create(source);Require(before.Fingerprint==after.Fingerprint,"Compound mutated source.");
            return new{members=members.Count,independentCopiesRejected,sourceUnchanged=true,transformHistoryAvailable=transformAvailable};
        });
        foreach(double spacing in new[]{2d,7d})Probe("owned-boolean-complete-source-graph-"+spacing,()=>
        {
            using var box=ShapeFactory.CreateBox(4,3,2);using var source=RepairSnapshot.Create(box);
            using var moved=box.Transformed(ShapeTransform.CreateTranslation(spacing,0,0));using var tool=RepairSnapshot.Create(moved);
            using var operation=BooleanHistoryModeling.Build(TopologyBooleanOperation.Fuse,[source,tool]);
            var shape=operation.RequireShape();Require(operation.Diagnostics.GroupSupport.HasFlag(LocalFeatureGroupSupport.Evolution),"Native history unavailable.");
            using var result=RepairSnapshot.Create(shape);
            for(int argument=0;argument<2;argument++)
            foreach(var slot in (argument==0?source:tool).Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
            {
                var rows=operation.History.Where(h=>h.Source is {} s&&s.PlanId==operation.PlanId&&s.ArgumentIndex==argument&&s.TopologyIndex==slot.Selection.Index&&s.Kind==slot.Kind).ToArray();
                Require(rows.Length>0,"Missing complete input history slot.");
                foreach(var row in rows.Where(h=>h.ResultTopologyIndex.HasValue))Require(result.Topology[row.ResultTopologyIndex!.Value].Kind==row.Shape!.Kind,"Wrong result topology kind.");
            }
            double expected=spacing<4?(4+spacing)*3*2:48;
            Require(Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass-expected)<1e-6,"Wrong fused volume.");
            using var after=RepairSnapshot.Create(box);Require(source.Fingerprint==after.Fingerprint,"Boolean mutated source.");
            return new{historyRows=operation.History.Count,volume=expected,completeInputs=true,transformOriginStillUnproven=!transformAvailable};
        });
        var loaded=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)?.EndsWith(Path.DirectorySeparatorChar+"occt",StringComparison.OrdinalIgnoreCase)==true)
            .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).OrderBy(m=>m.path).ToArray();
        File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new{passed,total=results.Count,results,nativeModules=loaded,
            transformAvailable,compoundAvailable,multicopyHistoryReady=transformAvailable&&compoundAvailable,
            scope="Locked public SDK inventory and exact membership/Boolean prerequisites only; no application or style inheritance completion claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("MULTICOPY_CAPABILITY_COMPLETE passed="+passed+" transform="+transformAvailable+" compound="+compoundAvailable);return passed?0:1;
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
