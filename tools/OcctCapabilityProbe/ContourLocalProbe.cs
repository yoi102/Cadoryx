using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

internal static class ContourLocalProbe
{
    internal static int Run(string[] args)
    {
        string directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
        var results=new List<object>();bool passed=true;
        void Probe(string name,Func<object> action)
        {
            var clock=Stopwatch.StartNew();try{results.Add(new{name,passed=true,detail=action(),elapsedMs=clock.ElapsedMilliseconds});}
            catch(Exception error){passed=false;results.Add(new{name,passed=false,error=error.ToString(),elapsedMs=clock.ElapsedMilliseconds});}
        }
        Probe("independent-constant-contours",()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);string before=source.Fingerprint;
            var recipe=ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(Vertical(source,0,0),.5),FilletContourProgram.Constant(Vertical(source,10,12),1)]);
            using var result=recipe.Build(source);Validate(result.RequireShape());Require(result.Contours.Count==2,"Both contours must execute.");
            Near(Volume(result.RequireShape()),1800-15*(1-Math.PI/4)*1.25,1e-5);VerifyRoundtrip(result,directory,"two-contours");
            using var after=RepairSnapshot.Create(box);Require(after.Fingerprint==before,"Source changed.");return new{volume=Volume(result.RequireShape()),contours=result.Contours.Count,sourceUnchanged=true};
        });
        foreach(string mode in new[]{"linear","smooth","sampled","bspline"})Probe("radius-"+mode,()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);var seed=Vertical(source,0,0);
            var program=mode switch
            {
                "linear"=>FilletContourProgram.FromLaw(seed,ScalarLawDefinition.Linear(new(0,1),.5,1.5)),
                "smooth"=>FilletContourProgram.FromLaw(seed,ScalarLawDefinition.Smooth(new(0,1),.5,1.5)),
                "bspline"=>FilletContourProgram.FromLaw(seed,ScalarLawDefinition.BSpline([.5,1.5,1],[0,1],[3,3],2)),
                _=>FilletContourProgram.Sampled(seed,[new(0,.5),new(.5,1.2),new(1,1.5)])
            };
            var recipe=ContourFilletRecipe.Create(source,[program]);using var discovered=recipe.Discover(source);var contour=discovered.Contours.Single();
            using var start=source.CopySubshape(contour.FirstVertex!.Value);using var end=source.CopySubshape(contour.LastVertex!.Value);
            double first=start.GetBoundingBox().Minimum.Z,last=end.GetBoundingBox().Minimum.Z;
            using var simulation=recipe.Simulate(source);Require(simulation.SimulatedSections.Count>0,"No simulated sections.");
            Require(simulation.SimulatedSections.Max(s=>s.Radius)-simulation.SimulatedSections.Min(s=>s.Radius)>.4,"Radius did not vary.");
            foreach(var section in simulation.SimulatedSections)
            {
                double t=(section.Center.Z-first)/(last-first);Require(t>=-1e-6&&t<=1.000001,"Section outside whole contour domain.");
                if(mode!="sampled")Near(section.Radius,mode switch{"linear"=>.5+t,"smooth"=>.5+3*t*t-2*t*t*t,_=>.5*(1-t)*(1-t)+3*t*(1-t)+t*t},.001);
            }
            using var result=recipe.Build(source);Validate(result.RequireShape());Require(Volume(result.RequireShape()) is >1700 and <1800,"Unexpected removed material.");
            VerifyRoundtrip(result,directory,mode);return new{volume=Volume(result.RequireShape()),sections=simulation.SimulatedSections.Count,wholeDomain=true};
        });
        Probe("ordered-tangent-chain-whole-domain",()=>
        {
            using var lower=ShapeFactory.CreateBox(10,12,7.5);using var upper=lower.Transformed(ShapeTransform.CreateTranslation(0,0,7.5));using var joined=lower.Fuse(upper);using var source=RepairSnapshot.Create(joined);
            var recipe=ContourFilletRecipe.Create(source,[FilletContourProgram.FromLaw(Vertical(source,0,0),ScalarLawDefinition.Linear(new(0,1),.5,1.5))]);
            using var discovery=recipe.Discover(source);var contour=discovery.Contours.Single();Near(contour.Length,15,1e-6);Require(discovery.ContourEdges.Count>=2,"Expected multiple native tangent edges.");
            using var start=source.CopySubshape(contour.FirstVertex!.Value);using var end=source.CopySubshape(contour.LastVertex!.Value);double first=start.GetBoundingBox().Minimum.Z,last=end.GetBoundingBox().Minimum.Z;
            using var simulation=recipe.Simulate(source);foreach(var section in simulation.SimulatedSections)Near(section.Radius,.5+(section.Center.Z-first)/(last-first),.001);
            using var result=recipe.Build(source);Validate(result.RequireShape());VerifyRoundtrip(result,directory,"chain");
            return new{volume=Volume(result.RequireShape()),length=contour.Length,edges=discovery.ContourEdges.Select(e=>new{index=e.Edge.Index,e.Ordinal,e.FirstParameter,e.LastParameter}).ToArray()};
        });
        Probe("duplicate-chain-program-rejected",()=>
        {
            using var lower=ShapeFactory.CreateBox(10,12,7.5);using var upper=lower.Transformed(ShapeTransform.CreateTranslation(0,0,7.5));using var joined=lower.Fuse(upper);using var source=RepairSnapshot.Create(joined);
            var seed=Vertical(source,0,0);using var discovery=ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(seed,1)]).Discover(source);
            Reject(()=>ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(seed,1),FilletContourProgram.Constant(discovery.ContourEdges[1].Edge,1)]).Build(source));return new{rejected=true};
        });
        Probe("closed-constant-and-law-seam",()=>
        {
            using var cylinder=ShapeFactory.CreateCylinder(10,15);using var source=RepairSnapshot.Create(cylinder);var seed=Select(source,ShapeKind.Edge,b=>Math.Abs(b.Maximum.Z)<1e-5);
            using var result=ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(seed,1)]).Build(source);Validate(result.RequireShape());Require(result.Contours.Single().IsClosed,"Circular contour not closed.");
            Reject(()=>ContourFilletRecipe.Create(source,[FilletContourProgram.FromLaw(seed,ScalarLawDefinition.Linear(new(0,1),1,2))]).Build(source));return new{closed=true,seamRejected=true,volume=Volume(result.RequireShape())};
        });
        Probe("multi-two-distance-chamfer",()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
            var left=Select(source,ShapeKind.Face,b=>Math.Abs(b.Maximum.X)<1e-5);var right=Select(source,ShapeKind.Face,b=>Math.Abs(b.Minimum.X-10)<1e-5);
            using var result=ContourChamferRecipe.Create(source,[new(Vertical(source,0,0),left,ChamferDimensions.TwoDistances,1,2),new(Vertical(source,10,12),right,ChamferDimensions.TwoDistances,1,2)]).Build(source);
            Validate(result.RequireShape());Near(Volume(result.RequireShape()),1770,1e-5);VerifyRoundtrip(result,directory,"chamfer");return new{volume=Volume(result.RequireShape()),contours=result.Contours.Count};
        });
        Probe("invalid-law-and-partial-fillet-rejected",()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);var seed=Vertical(source,0,0);
            bool rejected=false;try{FilletContourProgram.FromLaw(seed,ScalarLawDefinition.Linear(new(0,1),-1,1));}catch(ArgumentException){rejected=true;}Require(rejected,"Nonpositive law accepted.");
            using var failed=ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(seed,100)]).Build(source);Require(!failed.Diagnostics.AlgorithmDone||!failed.Diagnostics.ShapeIsValid,"Oversized fillet accepted.");
            bool partialRejected=false;try{failed.RequireShape();}catch(InvalidOperationException){partialRejected=true;}Require(partialRejected,"Partial shape leaked as success.");return new{nonpositiveRejected=true,partialRejected=true};
        });
        var loaded=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)?.EndsWith(Path.DirectorySeparatorChar+"occt",StringComparison.OrdinalIgnoreCase)==true)
            .Select(m=>new{path=m.FileName,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).OrderBy(m=>m.path).ToArray();
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{passed,results,nativeModules=loaded,
            packageVersion=typeof(Shape).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
            scope="Locked SDK contour/local finishing prerequisite; native chain discovery, whole-contour laws, analytic constants, exact/self-interference, STEP, seam/conflict/partial rejection. No application/UI completion claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("CONTOUR_LOCAL_PROBE_COMPLETE passed="+passed);return passed?0:1;
    }
    private static RepairSelection Vertical(RepairSnapshot source,double x,double y)=>Select(source,ShapeKind.Edge,b=>Math.Abs(b.Minimum.X-x)<1e-5&&Math.Abs(b.Maximum.X-x)<1e-5&&Math.Abs(b.Minimum.Y-y)<1e-5&&Math.Abs(b.Maximum.Y-y)<1e-5&&b.SizeZ>7.49);
    private static RepairSelection Select(RepairSnapshot source,ShapeKind kind,Func<BoundingBox3d,bool> predicate)
    {foreach(var t in source.Topology.Where(t=>t.Kind==kind)){using var shape=source.CopySubshape(t.Selection);if(predicate(shape.GetBoundingBox()))return t.Selection;}throw new InvalidOperationException("Fixture selection missing.");}
    private static void VerifyRoundtrip(LocalFeatureResult result,string directory,string name)
    {string path=Path.Combine(directory,name+".step");ShapeExchange.WriteStep(result.RequireShape(),path);using var loaded=ShapeExchange.ReadStep(path);Validate(loaded);Near(Volume(loaded),Volume(result.RequireShape()),1e-4);}
    private static void Validate(Shape shape)
    {Require(shape.GetTopologySummary().UniqueCounts.SolidCount==1&&shape.GetValidationReport(true,true).IsValid&&shape.GetSelfInterferenceReport().IsClear,"Result is not one exact clear solid.");}
    private static double Volume(Shape shape)=>Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass);
    private static void Near(double actual,double expected,double tolerance)=>Require(Math.Abs(actual-expected)<=tolerance,$"Expected {expected:R}, got {actual:R}.");
    private static void Reject(Func<LocalFeatureResult> action)
    {bool rejected=false;try{using var result=action();}catch(ArgumentException){rejected=true;}Require(rejected,"Expected explicit rejection.");}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
