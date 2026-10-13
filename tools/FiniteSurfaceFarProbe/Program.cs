using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Occt;
using OcctSharp;

if(args.Length!=2)throw new ArgumentException("Usage: FiniteSurfaceFarProbe frozen-candidate fresh-output");
string candidate=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
if(Directory.Exists(output))throw new InvalidOperationException("Preserve existing evidence; use a fresh output.");
Directory.CreateDirectory(output);
var rows=new List<object>();int failures=0;
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
foreach(var file in Directory.GetFiles(AppContext.BaseDirectory,"OcctSharp*.dll"))
    Require(Hash(file)==Hash(Path.Combine(candidate,Path.GetFileName(file))),"SDK differs from frozen candidate.");
foreach(var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"occt"),"*.dll"))
    Require(Hash(file)==Hash(Path.Combine(candidate,"occt",Path.GetFileName(file))),"Native disk payload differs.");
foreach(string kind in new[]{"sphere","cylinder"})
foreach(string pose in new[]{"identity","negative","tilted"})
foreach(string material in new[]{"plain","hole","island"})
foreach(string scenario in new[]{"raw-full-near","clip-full-near","clip-full-far","raw-far-only","clip-near-only-far"})
{
    string name=$"{kind}-{pose}-{scenario}"+(material=="plain"?"":$"-{material}");
    string folder=Path.Combine(output,name);Directory.CreateDirectory(folder);
    File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.UtcNow:O} {name}{Environment.NewLine}");
    bool far=scenario.EndsWith("far",StringComparison.Ordinal)||scenario=="raw-far-only";
    bool emptyExpected=scenario=="clip-near-only-far";
    bool clip=scenario.StartsWith("clip-",StringComparison.Ordinal);
    var rotation=pose switch
    {
        "negative"=>Quaterniond.FromAxisAngle(new Vector3d(1,0,0),Math.PI),
        "tilted"=>Quaterniond.FromAxisAngle(new Vector3d(1,1,0).Normalized(),.42),
        _=>Quaterniond.Identity
    };
    var placement=new RigidTransform3d(pose=="tilted"?new(10,-3,7):Vector3d.Zero,rotation);
    using var transform=OcctGeometryBridge.ToNative(placement);
    using var inverse=OcctGeometryBridge.ToNative(placement.Inverse());
    using var localBase=ShapeFactory.CreateBox(10,10,5);using var basis=localBase.Transformed(transform);
    using var localProfile=Profile(kind=="sphere",material);using var profile=localProfile.Transformed(transform);
    var baseFaces=basis.GetFaces();
    try
    {
        var support=baseFaces.Single(f=>{using var local=f.Transformed(inverse);return local.GetBoundingBox().Minimum.Z>4.999;});
        bool nearOnly=scenario=="clip-near-only-far",farOnly=scenario=="raw-far-only";
        using var localStop=kind=="sphere"?
            SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Sphere,new(new(5,5,18),new(1,0,0),new(0,1,0)),
                new(0,Math.Tau,farOnly?0:-Math.PI/2,nearOnly?0:Math.PI/2),6):
            SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,new(new(5,0,18),new(1,0,0),new(0,0,-1)),
                new(farOnly?Math.PI:0,nearOnly?Math.PI:Math.Tau,0,10),6);
        using var stop=localStop.Transformed(transform);
        ShapeExchange.WriteBrep(basis,Path.Combine(folder,"source-before.brep"));
        ShapeExchange.WriteBrep(stop,Path.Combine(folder,"stop-before.brep"));
        try
        {
            // The fixture's exact analytic equatorial plane is local Z=18.
            // A conservative box contains the full original finite face in X/Y
            // and the chosen half-domain in Z. This is a private Boolean limiter,
            // never a replacement user selection or inferred topology identity.
            using var localBox=ShapeFactory.CreateBox(16,16,8);
            using var boxTranslation=OcctGeometryBridge.ToNative(placement*RigidTransform3d.Translate(-3,-3,far?18:10));
            using var box=localBox.Transformed(boxTranslation);
            // TopoDS Clone retains the original TShape. Common may add edge
            // representations even when the intersection has no material.
            // Isolate both construction and coverage operations with the existing
            // exact geometry/topology copier; neither copy supplies user identity.
            using var selectedStopSnapshot=RepairSnapshot.Create(stop);
            using var branchStop=selectedStopSnapshot.CopyShape();
            using var coverageStop=selectedStopSnapshot.CopyShape();
            using var effective=clip?branchStop.Common(box):branchStop.Clone();
            var effectiveFaces=effective.GetFaces();
            double effectiveArea;
            try{effectiveArea=effectiveFaces.Sum(Area);}finally{foreach(var f in effectiveFaces)f.Dispose();}
            if(emptyExpected)
            {
                Require(effectiveArea<=1e-8,"A near-only finite face supplied far-side material.");
                rows.Add(new{name,passed=true,negative=true,effectiveArea,buildStarted=false});
                continue;
            }
            Require(effectiveArea>1e-8,"Private branch limiter has no original finite material.");
            var direction=rotation.Rotate(Vector3d.UnitZ);
            using var plan=LimitedFeaturePlan.Create(basis,profile,support,new()
            {Limit=LocalFeatureLimit.Until,AddMaterial=true,Direction=new(direction.X,direction.Y,direction.Z)},until:effective);
            using var built=plan.Build();
            Require(built.Diagnostics.AlgorithmDone&&built.Diagnostics.ShapeIsValid,built.Diagnostics.Message);
            var shape=built.RequireShape();
            using var delta=shape.Cut(basis);
            double Measure(Func<double,double> f)=>f(2)-(material=="plain"?0:f(1))+(material=="island"?f(.5):0);
            double integral=kind=="sphere"?Measure(r=>2*Math.PI/3*(216-Math.Pow(36-r*r,1.5))):
                Measure(a=>2*a*(a*Math.Sqrt(36-a*a)+36*Math.Asin(a/6)));
            double expectedStart=kind=="sphere"?Measure(r=>Math.PI*r*r):Measure(a=>4*a*a);
            double expected=500+expectedStart*13+(far?integral:-integral);
            double volume=Volume(shape);
            double volumeError=Math.Abs(volume-expected),volumeTolerance=Math.Max(1e-6,500*1e-9);
            Require(volumeError<=volumeTolerance,$"Analytic {far} branch volume differs: {volume:R}/{expected:R}.");
            using var start=delta.Common(profile);
            double startArea=FaceArea(start),startAreaTolerance=Math.Max(1e-7,expectedStart*1e-9);
            Require(Math.Abs(startArea-expectedStart)<=startAreaTolerance,"Complete starting material changed.");
            using var vector=GpVec.Create(direction.X*25,direction.Y*25,direction.Z*25);
            using var envelope=profile.Extrude(vector);using var outside=delta.Cut(envelope);
            Require(Volume(outside)<=1e-6,"Native output left the complete directional profile.");
            using var retained=basis.Common(shape);Require(Math.Abs(Volume(retained)-500)<=1e-6,"Base material changed.");
            double capArea=0;var deltaFaces=delta.GetFaces();
            try
            {
                foreach(var face in deltaFaces)
                {
                    var type=face.GetFaceSurfaceSnapshot().SurfaceType;
                    if(type!=(kind=="sphere"?SurfaceGeometryType.Sphere:SurfaceGeometryType.Cylinder))continue;
                    double area=Area(face);using var covered=face.Common(coverageStop);
                    Require(Math.Abs(FaceArea(covered)-area)<=Math.Max(1e-7,area*1e-9),"Original finite face only partly covers a cap.");
                    using var local=face.Transformed(inverse);var bounds=local.GetBoundingBox();
                    Require(far?bounds.Minimum.Z>18+1e-6:bounds.Maximum.Z<18-1e-6,"Cap uses the wrong branch or reaches directional tangency.");
                    capArea+=area;
                }
            }
            finally{foreach(var face in deltaFaces)face.Dispose();}
            double expectedCap=kind=="sphere"?Measure(r=>12*Math.PI*(6-Math.Sqrt(36-r*r))):
                Measure(a=>24*a*Math.Asin(a/6));
            double capAreaError=Math.Abs(capArea-expectedCap),capAreaTolerance=Math.Max(1e-7,expectedCap*1e-9);
            Require(capAreaError<=capAreaTolerance,"Complete independent cap area differs.");
            Require(shape.GetValidationReport(true,true).IsValid&&shape.GetSelfInterferenceReport().IsClear,"Exact solid validation failed.");
            int expectedDeltaSolids=material=="island"?2:1;
            Require(delta.GetTopologySummary().UniqueCounts.SolidCount==expectedDeltaSolids,"Complete profile material components changed.");
            ShapeExchange.WriteBrep(shape,Path.Combine(folder,"result.brep"));
            rows.Add(new{name,material,passed=true,negative=false,volume,expected,volumeError,volumeTolerance,expectedDeltaSolids,
                startArea,expectedStart,startAreaTolerance,capArea,expectedCap,capAreaError,capAreaTolerance,effectiveArea,buildStarted=true});
        }
        catch(Exception error){failures++;rows.Add(new{name,passed=false,error=error.ToString()});}
        finally
        {
            ShapeExchange.WriteBrep(basis,Path.Combine(folder,"source-after.brep"));
            ShapeExchange.WriteBrep(stop,Path.Combine(folder,"stop-after.brep"));
            File.WriteAllText(Path.Combine(output,"partial-result.json"),JsonSerializer.Serialize(new
            {planned=90,actual=rows.Count,failures,complete=false,rows},new JsonSerializerOptions{WriteIndented=true}));
            Require(Hash(Path.Combine(folder,"source-before.brep"))==Hash(Path.Combine(folder,"source-after.brep")),"Source representation mutated.");
            Require(Hash(Path.Combine(folder,"stop-before.brep"))==Hash(Path.Combine(folder,"stop-after.brep")),"Original selected stop mutated.");
        }
    }
    finally{foreach(var f in baseFaces)f.Dispose();}
}
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
    .Where(m=>Path.GetDirectoryName(m.FileName)==Path.Combine(AppContext.BaseDirectory,"occt"))
    .Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Hash(m.FileName)}).ToArray();
Require(modules.Length==62,"Actual native62 observation missing.");
foreach(var module in modules)Require(module.sha256==Hash(Path.Combine(candidate,"occt",module.name)),"Loaded native differs.");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new
{planned=90,originalPlainCases=30,actual=rows.Count,failures,passed=failures==0,scope="Native far-domain prerequisite only; no application author/UI/history/storage integration",rows,native=modules},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Far prerequisite90: {rows.Count-failures} passed/{failures} failed; no product completion claim.");
return failures==0?0:1;

static double Volume(Shape shape)=>Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass);
static double Area(Shape shape)=>Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-10).Properties.Mass);
static double FaceArea(Shape shape){var faces=shape.GetFaces();try{return faces.Sum(Area);}finally{foreach(var f in faces)f.Dispose();}}
static Shape Profile(bool circle,string material)
{
    static Shape Wire(bool round,double radius)
    {
        if(round){using var edge=ShapeFactory.CreateCircleEdge(new(5,5,5),new(0,0,1),radius);return ShapeFactory.CreateWire([edge]);}
        return ShapeFactory.CreatePolygonWire([new(5-radius,5-radius,5),new(5+radius,5-radius,5),
            new(5+radius,5+radius,5),new(5-radius,5+radius,5)],true);
    }
    using var outer=Wire(circle,2);
    if(material=="plain")return ShapeFactory.CreatePlanarFace(outer);
    using var inner=Wire(circle,1);using var outerFace=ShapeFactory.CreatePlanarFace(outer);
    using var innerFace=ShapeFactory.CreatePlanarFace(inner);using var ring=outerFace.Cut(innerFace);
    if(material=="hole")return ring.Clone();
    using var islandWire=Wire(circle,.5);using var island=ShapeFactory.CreatePlanarFace(islandWire);
    return ShapeFactory.CreateCompound([ring,island]);
}
