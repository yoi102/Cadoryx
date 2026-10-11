using System.Text.Json;
using Cadoryx.Kernel.Occt;
using OcctSharp;

internal static class NativeThreadBoundaryProbe
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);var rows=new List<object>();bool passed=true;
        foreach(var size in new[]{(8,1.25,6.8),(12,1.75,10.2)})
        foreach(double extra in new[]{0,1e-7,size.Item2})foreach(bool down in new[]{false,true})
        {
            var(nominal,pitch,pilot)=size;double height=8*pitch;
            try
            {
                using var block=ShapeFactory.CreateBox(40,40,height);
                using var frame=GpTrsf.Create(12,20,down?height:0,1,0,0,down?Math.PI:0);
                using var bore=ShapeFactory.CreateCylinder(pilot/2,height);
                using var locatedBore=bore.Transformed(frame);
                using var drilled=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[block],[locatedBore],new(){NonDestructive=true,RunParallel=false});
                // Reopen both operands exactly as the production geometry bridge does.
                using var reopenedPilot=ShapeExchange.ReadBrep(ShapeExchange.WriteBrep(drilled.RequireShape(),Path.Combine(directory,"pilot.brep")));
                using var tool=NativeMetricThreadTool.Build(nominal,pilot,pitch,1,height+extra,out _);
                using var locatedTool=tool.Transformed(frame);
                using var reopenedTool=ShapeExchange.ReadBrep(ShapeExchange.WriteBrep(locatedTool,Path.Combine(directory,"tool.brep")));
                using var result=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[reopenedPilot],[reopenedTool],new(){NonDestructive=true,RunParallel=false});
                var shape=result.RequireShape();
                double before=reopenedPilot.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
                double after=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
                double expected=NativeMetricThreadTool.RemovedVolume(nominal,pilot,pitch,height);
                bool ok=shape.IsValid&&Math.Abs(before-after-expected)<.002;passed&=ok;
                rows.Add(new{nominal,extra,down,before,after,expected,ok});
                File.AppendAllText(Path.Combine(directory,"progress.log"),$"{nominal} extra={extra:R} down={down}: {before-after:R} expected={expected:R} ok={ok}\n");
            }
            catch(Exception e){passed=false;rows.Add(new{nominal,extra,down,error=e.ToString()});}
        }
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{passed,rows},new JsonSerializerOptions{WriteIndented=true}));
        if(!passed)Environment.ExitCode=1;
    }
}
