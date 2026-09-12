using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

if(args.Length==2&&args[0]=="--settings-only")
{
    var settings=await new CadDocumentStorage().ReadSettingsAsync(args[1]);
    using var process=Process.GetCurrentProcess();
    bool native=process.Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Contains("OcctSharp.Native",StringComparison.OrdinalIgnoreCase));
    if(native)throw new InvalidOperationException("Settings read initialized the native kernel.");
    Console.WriteLine(JsonSerializer.Serialize(new{settings,nativeKernelLoaded=native}));return;
}
if(args.Length!=3||args[0]!="--case"||args[1] is not ("shared-25000" or "brep-8000" or "opaque-128m"))
    throw new ArgumentException("Usage: --case shared-25000|brep-8000|opaque-128m <output directory>, or --settings-only <document>.");
string name=args[1],directory=Path.GetFullPath(args[2]);Directory.CreateDirectory(directory);
var samples=new List<object>();var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();
var resources=new List<IDisposable>();
try
{
    var document=DocumentSnapshot.Create(name);GeometryAssetRef geometry;
    using(var shape=ShapeFactory.CreateBox(10,20,30))
    {
        if(name=="brep-8000")
        {
            var boxes=new List<Shape>();
            try
            {
                for(int i=0;i<8000;i++)
                {
                    using var box=ShapeFactory.CreateBox(10+(i%7)*0.1,20,30);
                    using var transform=GpTrsf.Create((i%100)*15,(i/100)*25,0,0,0,1,0);
                    boxes.Add(box.Transformed(transform));
                }
                using var compound=ShapeFactory.CreateCompound(boxes);
                var result=OcctGeometryBridge.StoreShape(compound,assets);resources.Add(result);geometry=result.Geometry;
            }
            finally{foreach(var box in boxes)box.Dispose();}
        }
        else {var result=OcctGeometryBridge.StoreShape(shape,assets);resources.Add(result);geometry=result.Geometry;}
    }
    var part=DefinitionId.New();var body=BodyId.New();var feature=FeatureId.New();
    var definition=new PartDefinition(part,name,[body],[feature]);
    var cadBody=new CadBody(body,part,name,geometry,feature,document.Layers.Keys.Single(),new());
    int occurrences=name=="shared-25000"?25000:1;
    var root=(AssemblyDefinition)document.Definitions[document.RootAssemblyId];
    root=root with{Children=Enumerable.Range(0,occurrences).Select(i=>new ComponentSlot(ComponentSlotId.New(),part,"Instance "+i,RigidTransform3d.Translate(i%250*15,i/250*25,0))).ToImmutableArray()};
    document=document with{Definitions=document.Definitions.Add(part,definition).SetItem(root.Id,root),Bodies=document.Bodies.Add(body,cadBody),
        Features=document.Features.Add(feature,new(feature,part,name,new ImportedRecipe(geometry),[],body,geometry))};
    if(name=="opaque-128m")
    {
        byte[] bytes=new byte[128*1024*1024];new Random(20260912).NextBytes(bytes);
        var payload=assets.Stage(bytes);resources.Add(payload);var extension=assets.Stage("opaque storage benchmark, not CAD geometry"u8);resources.Add(extension);
        document=document with{Extensions=[new("benchmark.payload",1,extension.Id,"text")],RetainedAssets=[payload.Id],
            RetainedAssetFormats=ImmutableDictionary<AssetId,AssetFormat>.Empty.Add(payload.Id,AssetFormat.Opaque)};
    }
    document.Validate();string path=Path.Combine(directory,name+".cadoryx");
    // Warm codecs and the filesystem without counting fixture construction as save/load time.
    await storage.SaveAsync(document,assets,path);
    for(int iteration=1;iteration<=3;iteration++)
    {
        var save=await Measure(()=>storage.SaveAsync(document,assets,path));
        var target=new MemoryAssetStore();
        var load=await Measure(async()=>
        {
            using var loaded=await storage.LoadAsync(path,target);
            if(loaded.Snapshot.Id!=document.Id||loaded.Snapshot.StateId!=document.StateId||loaded.Snapshot.Bodies[body]!=cadBody||
                loaded.Snapshot.EnumerateOccurrences().Count()!=occurrences||target.SizeBytes!=assets.SizeBytes)
                throw new InvalidDataException("Benchmark roundtrip mismatch.");
        });
        if(target.Count!=0)throw new InvalidOperationException("Loaded assets were not released.");
        var settings=await Measure(async()=>{if(await storage.ReadSettingsAsync(path)!=document.Settings)throw new InvalidDataException("Settings mismatch.");});
        samples.Add(new{iteration,save,load,settings});
        Console.WriteLine($"{name} iteration {iteration}: save={save.Milliseconds:F1}ms load={load.Milliseconds:F1}ms settings={settings.Milliseconds:F1}ms");
    }
    long rawAssets=assets.SizeBytes;long fileBytes=new FileInfo(path).Length;
    var output=new{name,measuredAt=DateTimeOffset.UtcNow,environment=new{os=RuntimeInformation.OSDescription,dotnet=RuntimeInformation.FrameworkDescription,
        cpu=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),logicalProcessors=Environment.ProcessorCount},
        method="3 measured repetitions after 1 warm save; fixture generation excluded; load includes roundtrip checks; process private memory sampled every 10ms, no forced GC or cold-cache claim",
        counts=new{occurrences,bodies=1,geometryRevisions=1,assets=assets.Count,rawAssets,fileBytes},samples};
    await File.WriteAllTextAsync(Path.Combine(directory,name+".json"),JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true}));
}
finally{foreach(var resource in resources)resource.Dispose();}
if(assets.Count!=0)throw new InvalidOperationException("Source assets were not released.");

static async Task<Metric> Measure(Func<Task> action)
{
    using var process=Process.GetCurrentProcess();process.Refresh();long baseline=process.PrivateMemorySize64,peak=baseline;
    object gate=new();
    using var timer=new Timer(_=>{lock(gate){process.Refresh();peak=Math.Max(peak,process.PrivateMemorySize64);}},null,0,10);
    long allocated=GC.GetTotalAllocatedBytes(true);var clock=Stopwatch.StartNew();await action();clock.Stop();
    long bytes=GC.GetTotalAllocatedBytes(true)-allocated;
    await timer.DisposeAsync();lock(gate){process.Refresh();peak=Math.Max(peak,process.PrivateMemorySize64);}
    return new(clock.Elapsed.TotalMilliseconds,bytes,baseline,peak);
}
internal sealed record Metric(double Milliseconds,long AllocatedBytes,long BaselinePrivateBytes,long PeakPrivateBytes);
