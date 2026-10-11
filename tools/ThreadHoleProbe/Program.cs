using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Kernel.Occt;
using OcctSharp;

if(args.Length!=1)throw new ArgumentException("Usage: ThreadHoleProbe output.json");
var results=new List<object>();
void Near(double a,double b,string label)
{if(!double.IsFinite(a)||Math.Abs(a-b)>1e-6)throw new InvalidOperationException($"{label}: {a:R} != {b:R}");}
// Independent explicit table; no Cadoryx.Db or application DLL is referenced.
foreach(var size in new[]{(3,.5,2.5),(4,.7,3.3),(5,.8,4.2),(6,1d,5d),(8,1.25,6.8),(10,1.5,8.5),(12,1.75,10.2),(16,2d,14d)})
foreach(bool through in new[]{false,true})
{
    var(nominal,pitch,pilot)=size;using var block=ShapeFactory.CreateBox(40,40,20);
    double bottom=through?-1:12,depth=through?22:9;
    using var cylinder=ShapeFactory.CreateCylinder(pilot/2,depth);using var placement=GpTrsf.Create(10,10,bottom);
    using var cutter=cylinder.Transformed(placement);using var cut=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[block],[cutter],new(){NonDestructive=true,RunParallel=false});
    var shape=cut.RequireShape();if(!shape.IsValid)throw new InvalidOperationException("Invalid pilot-hole solid.");
    double volume=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass;
    Near(volume,32000-Math.PI*pilot*pilot/4*(through?20:8),"Independent analytic removed cylinder volume");
    using var map=RepairSnapshot.Create(shape);var wall=map.Topology.Single(t=>
    {if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
    var proof=NativeHoleInspector.Inspect(shape,wall.Selection.Index,default);
    Near(proof.Diameter,pilot,"Native pilot diameter");Near(proof.Depth,through?20:8,"Native drilled depth");
    Near(proof.Entry.X,10,"Entry X");Near(proof.Entry.Y,10,"Entry Y");Near(proof.Entry.Z,20,"Entry Z");
    if(proof.Through!=through||proof.Head!=0)throw new InvalidOperationException("Native pilot mode differs.");
    results.Add(new{nominal,pitch,pilot,through,volume,nativeDiameter=proof.Diameter,nativeDepth=proof.Depth,faces=proof.Faces});
}
var assemblies=Directory.GetFiles(AppContext.BaseDirectory,"OcctSharp*.dll").Where(p=>!Path.GetFileName(p).Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase))
    .Select(Assembly.LoadFrom).Distinct().ToArray();
var types=assemblies.SelectMany(a=>a.GetExportedTypes()).Distinct().ToArray();
var shapeAuthors=types.SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
    .Where(m=>m.ReturnType==typeof(Shape)).ToArray();
var surfacePcurveAuthors=shapeAuthors.Where(m=>m.GetParameters().Any(p=>p.ParameterType.Name.Contains("Geom2dCurve",StringComparison.Ordinal))&&
    m.GetParameters().Any(p=>p.ParameterType.Name.Contains("GeomSurface",StringComparison.Ordinal))).Select(m=>m.ToString()).ToArray();
var bridgeConstructors=typeof(Shape).GetConstructors().Select(c=>c.ToString()).ToArray();
var prerequisite=new{status=surfacePcurveAuthors.Length==0?"TYPED_PCURVE_EDGE_API_MISSING":"CANDIDATE_REQUIRES_RUNTIME_PROOF",
    ownedShapeAuthorSignatures=shapeAuthors.Select(m=>m.DeclaringType!.FullName+"."+m).Order().ToArray(),surfacePcurveAuthors,publicShapeConstructors=bridgeConstructors,
    cylinderSurfaceType=types.SingleOrDefault(t=>t.Name=="GeomCylindricalSurface")?.FullName,
    scope="Inventory of locked public Shape-returning author methods and constructors. Cylinder/2D curves alone do not create an owned topology edge. It does not prove every possible serialization workflow is impossible, nor validate a helical tooth solid."};
var directory=Path.Combine(AppContext.BaseDirectory,"occt");var names=Directory.GetFiles(directory,"*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>names.Contains(m.ModuleName)).ToArray();
if(native.Any(m=>!string.Equals(Path.GetDirectoryName(m.FileName),directory,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Foreign native module loaded.");
if(!native.Any(m=>m.ModuleName.Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Bridge was not loaded.");
if(Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Length!=0)throw new InvalidOperationException("Application DLL leaked into independent consumer.");
File.WriteAllText(Path.GetFullPath(args[0]),JsonSerializer.Serialize(new{passed=true,total=results.Count,runtime=OcctRuntime.Info,results,helicalPrerequisite=prerequisite,
    nativeModules=native.Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))})},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Independent pilot holes PASS: {results.Count}; helical prerequisite {prerequisite.status}.");
