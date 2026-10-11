using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

internal static class SolidOffsetProbe
{
    internal static int Run(string[] args)
    {
        string directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
        var results=new List<object>();bool passed=true;
        foreach(string kind in new[]{"box","cylinder","sphere"})foreach(double distance in new[]{-1d,1d})
        {
            string name=kind+(distance<0?"-inward":"-outward");var clock=Stopwatch.StartNew();
            try
            {
                using var source=Create(kind);using var before=RepairSnapshot.Create(source);
                using var offset=source.Offset(distance);Validate(offset);
                double actual=Volume(offset),original=Volume(source),expected=Expected(kind,distance);
                Require(Math.Abs(actual-expected)<=Math.Max(1e-6,expected*1e-6),"Offset volume differs from independent analytic result.");
                using var overlap=source.Common(offset);double common=Volume(overlap);
                Require(Math.Abs(common-Math.Min(original,actual))<=Math.Max(1e-6,original*1e-6),"Offset is not nested in its authored direction.");
                string brep=Path.Combine(directory,name+".brep"),step=Path.Combine(directory,name+".step");
                ShapeExchange.WriteBrep(offset,brep);ShapeExchange.WriteStep(offset,step);
                using var reopened=ShapeExchange.ReadStep(step);Validate(reopened);
                Require(Math.Abs(Volume(reopened)-actual)<=Math.Max(1e-6,actual*1e-6),"STEP offset volume differs.");
                using var after=RepairSnapshot.Create(source);Require(before.Fingerprint==after.Fingerprint,"Source mutated.");
                results.Add(new{name,passed=true,sourceKind=source.Kind.ToString(),resultKind=offset.Kind.ToString(),distance,original,expected,actual,common,sourceUnchanged=true,step,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(step))),elapsedMs=clock.ElapsedMilliseconds});
            }
            catch(Exception error){passed=false;results.Add(new{name,passed=false,error=error.ToString(),elapsedMs=clock.ElapsedMilliseconds});}
        }
        foreach(string kind in new[]{"box","cylinder","sphere"})
        {
            string name=kind+"-collapsed";
            try
            {
                using var source=Create(kind);using var before=RepairSnapshot.Create(source);
                bool rejected=false;string? diagnostic=null;
                try{using var candidate=source.Offset(-20);Validate(candidate);}
                catch(Exception error) when(error is OcctException or InvalidOperationException){rejected=true;diagnostic=error.Message;}
                using var after=RepairSnapshot.Create(source);Require(rejected&&before.Fingerprint==after.Fingerprint,"Collapsed offset was not rejected with unchanged source.");
                results.Add(new{name,passed=true,rejected,diagnostic,sourceUnchanged=true});
            }
            catch(Exception error){passed=false;results.Add(new{name,passed=false,error=error.ToString()});}
        }
        var loaded=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)?.EndsWith(Path.DirectorySeparatorChar+"occt",StringComparison.OrdinalIgnoreCase)==true)
            .Select(m=>new{path=m.FileName,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).OrderBy(m=>m.path).ToArray();
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{passed,results,nativeModules=loaded,
            packageVersion=typeof(Shape).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
            scope="Locked SDK solid skin/join offset; analytic volumes, exact validation, native self-interference, containment, unchanged source, collapse rejection and STEP re-read. Application recipe/UI/history are not implemented by this probe."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("SOLID_OFFSET_PROBE_COMPLETE passed="+passed);return passed?0:1;
    }
    private static Shape Create(string kind)=>kind switch
    {"box"=>ShapeFactory.CreateBox(10,12,15),"cylinder"=>ShapeFactory.CreateCylinder(3,10),_=>ShapeFactory.CreateSphere(3)};
    private static double Expected(string kind,double distance)=>kind switch
    {
        "box" when distance<0=>8*10*13,
        "box"=>1800+900+37*Math.PI+4*Math.PI/3,
        "cylinder" when distance<0=>Math.PI*2*2*8,
        "cylinder"=>Math.PI*16*10+2*Math.PI*(9+3*Math.PI/2+2d/3),
        _=>4*Math.PI*Math.Pow(3+distance,3)/3
    };
    private static double Volume(Shape shape)=>Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass);
    private static void Validate(Shape shape)
    {
        Require(shape.Kind==ShapeKind.Solid&&shape.GetTopologySummary().UniqueCounts.SolidCount==1,"Offset did not return one solid.");
        Require(shape.GetValidationReport(geometryChecks:true,exact:true).IsValid,"Offset BRep invalid.");
        Require(shape.GetSelfInterferenceReport().IsClear,"Offset has native self-interference.");
        Require(double.IsFinite(Volume(shape))&&Volume(shape)>1e-9,"Offset volume is not positive finite.");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
