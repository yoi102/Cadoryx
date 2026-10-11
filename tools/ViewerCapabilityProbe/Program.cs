using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

// Read-only public managed surface inventory. This does not instantiate a viewer,
// invoke OCCT, benchmark performance, or prove rendered/selection semantics.
if(args.Length!=2)throw new ArgumentException("Usage: ViewerCapabilityProbe frozen-app new-output");
string frozen=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
if(Directory.Exists(output))throw new InvalidOperationException("Evidence already exists.");
Directory.CreateDirectory(output);
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
var payload=new List<object>();
var assemblies=new List<Assembly>();
foreach(string path in Directory.GetFiles(AppContext.BaseDirectory,"OcctSharp*.dll"))
{
    string name=Path.GetFileName(path);
    Require(Hash(path)==Hash(Path.Combine(frozen,name)),"Frozen SDK mismatch: "+name);
    assemblies.Add(Assembly.LoadFrom(path));
    payload.Add(new{name,sha256=Hash(path)});
}
Require(payload.Count==13,"Expected thirteen public SDK assemblies.");
object Surface(Type type)=>new{type=type.FullName,assembly=type.Assembly.GetName().Name,baseType=type.BaseType?.FullName,
    constructors=type.GetConstructors().Select(c=>c.ToString()).Order().ToArray(),
    methods=type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)
        .Select(m=>m.ToString()).Order().ToArray(),
    properties=type.GetProperties(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)
        .Select(p=>p.ToString()).Order().ToArray()};
var exported=assemblies.SelectMany(a=>a.GetExportedTypes()).ToArray();
var supportingTypes=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "AISShape", "AISColoredShape", "AISInteractiveObject", "V3dViewer", "V3dView",
    "WinNTWindow", "WNTWindow", "WNTWClass", "AspectWindow", "AspectDisplayConnection", "OpenGlGraphicDriver",
    "Graphic3dGraphicDriver", "QuantityColor", "Prs3dDrawer", "ImagePixMap", "ImageAlienPixMap",
    "PrsMgrPresentationManager", "Graphic3dStructure", "SelectMgrSelectableObject",
    "SelectMgrSelectionManager", "SelectMgrEntityOwner", "StdSelectBRepOwner"
};
var relevant=exported.Where(t=>t.Name.Replace("_","").Contains("ConnectedInteractive",StringComparison.OrdinalIgnoreCase)||
    t.Name=="ViewerPresentation"||t.Name=="OcctViewer"||t.Name.Replace("_","")=="AISInteractiveContext"||
    supportingTypes.Contains(t.Name.Replace("_",""))).OrderBy(t=>t.FullName).ToArray();
// Inspect factories throughout the public SDK as well as the selected classes.
// A missing constructor alone does not prove a missing construction path.
var compositionResults=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "AISShape", "AISColoredShape", "AISInteractiveObject", "AspectWindow", "WNTWindow", "WinNTWindow"
};
var factories=exported.SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)
    .Where(m=>compositionResults.Contains(m.ReturnType.Name.Replace("_","")))
    .Select(m=>new{declaringType=t.FullName,isStatic=m.IsStatic,signature=m.ToString()}))
    .OrderBy(m=>m.declaringType).ThenBy(m=>m.signature).ToArray();
Require(relevant.Any(t=>t==typeof(OcctViewer)),"High-level viewer missing from public inventory.");
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>m.ModuleName=="OcctSharp.Native.dll").ToArray();
Require(native.Length==0,"This inventory must remain read-only managed reflection.");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{recordedAt=DateTimeOffset.UtcNow,
    state="PUBLIC_SURFACE_INVENTORIED",sdk=payload,exportedTypeCount=exported.Length,publicTypes=relevant.Select(Surface).ToArray(),
    compositionFactories=factories,
    nativeBridgeInvoked=false,scope="Public managed composition surface only; performance/source-style/occurrence selection require a real native viewer consumer."},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Public viewer surface inventoried: {relevant.Length} types, thirteen frozen SDK assemblies; no native viewer/run/performance acceptance claim.");
