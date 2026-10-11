using System.Diagnostics;
using System.Text.Json;
using OcctSharp;

internal static class ImportedInspectionProbe
{
    internal static int Run(string[] args)
    {
        if(args.Length!=2)throw new ArgumentException("Expected original BRep path and a fresh result path.");
        if(File.Exists(args[1]))throw new ArgumentException("Result already exists.");
        var timer=Stopwatch.StartNew();
        try
        {
            using var shape=ShapeExchange.ReadBrep(Path.GetFullPath(args[0]));
            using var before=RepairSnapshot.Create(shape);
            var measured=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-9);
            using var after=RepairSnapshot.Create(shape);
            if(before.Fingerprint!=after.Fingerprint)throw new InvalidOperationException("Inspection changed geometry.");
            File.WriteAllText(args[1],JsonSerializer.Serialize(new {passed=true,measured,elapsedMs=timer.Elapsed.TotalMilliseconds}));
            return 0;
        }
        catch(Exception error)
        {
            File.WriteAllText(args[1],JsonSerializer.Serialize(new {passed=false,error=error.ToString(),elapsedMs=timer.Elapsed.TotalMilliseconds}));
            return 1;
        }
    }
}
