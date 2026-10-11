using System.Text.Json;
using Cadoryx.Kernel.Occt;
using OcctSharp;

internal static class NativeThreadHistoryProbe
{
    public static void Run(string directory,bool finite=false)
    {
        Directory.CreateDirectory(directory);var rows=new List<object>();bool passed=true;
        foreach(var size in new[]{(8,1.25,6.8,-1),(12,1.75,10.2,1)})
        {
            var(nominal,pitch,pilot,hand)=size;double height=8*pitch;
            using var block=ShapeFactory.CreateBox(40,40,height);
            using var frame=GpTrsf.Create(12,20,height,1,0,0,Math.PI);
            using var baseMap=RepairSnapshot.Create(block);
            using var bore=ShapeFactory.CreateCylinder(pilot/2,height+pitch);
            using var locatedBore=bore.Transformed(frame);using var boreMap=RepairSnapshot.Create(locatedBore);
            using var drilled=finite?BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut,[baseMap,boreMap]):
                LocalFeatures.Hole(baseMap,new(LocalHoleMode.ThroughAll,new(12,20,height),new(0,0,-1),pilot/2));
            using var reopenedPilot=ShapeExchange.ReadBrep(ShapeExchange.WriteBrep(drilled.RequireShape(),Path.Combine(directory,"pilot.brep")));
            using var tool=NativeMetricThreadTool.Build(nominal,pilot,pitch,hand,height+1e-7,out _);
            using var locatedTool=tool.Transformed(frame);
            using var reopenedTool=ShapeExchange.ReadBrep(ShapeExchange.WriteBrep(locatedTool,Path.Combine(directory,"tool.brep")));
            using var pilotMap=RepairSnapshot.Create(reopenedPilot);using var toolMap=RepairSnapshot.Create(reopenedTool);
            using var result=BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut,[pilotMap,toolMap]);
            var shape=result.RequireShape();
            double before=reopenedPilot.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
            double after=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
            double expected=NativeMetricThreadTool.RemovedVolume(nominal,pilot,pitch,height);
            using var direct=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[reopenedPilot],[reopenedTool],new(){NonDestructive=true,RunParallel=false});
            double directVolume=direct.RequireShape().InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
            bool ok=shape.IsValid&&Math.Abs(before-after-expected)<.002;passed&=ok;
            rows.Add(new{nominal,hand,before,after,directVolume,expected,ok,diagnostics=result.Diagnostics,faces=shape.FaceCount});
            File.AppendAllText(Path.Combine(directory,"progress.log"),$"{nominal} history: {before-after:R} direct={before-directVolume:R} expected={expected:R} ok={ok}\n");
        }
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{passed,rows},new JsonSerializerOptions{WriteIndented=true}));
        if(!passed)Environment.ExitCode=1;
    }
}
