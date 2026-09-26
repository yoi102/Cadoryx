using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;

namespace Cadoryx.Cli;

/// <summary>Headless CAD operations; no WPF initialization, user dialogs or recovery session.</summary>
public static class CadCommandLine
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        Converters={new JsonStringEnumConverter()}};
    public const string Help="""
        Cadoryx.Cli inspect INPUT [--exact] [--output REPORT.json] [--overwrite] [--memory]
        Cadoryx.Cli convert INPUT OUTPUT [--overwrite] [--all] [--linear MM] [--angular RAD] [--memory]
        Cadoryx.Cli benchmark INPUT --output REPORT.json [--iterations 3] [--overwrite] [--memory]
        Cadoryx.Cli interference INPUT [--output REPORT.json] [--overwrite] [--memory]
        Cadoryx.Cli cache-clean CACHE_ROOT
        INPUT: .cadoryx, .step/.stp, .iges/.igs, .stl; OUTPUT: .cadoryx, .step/.stp, .iges/.igs, .stl.
        Disk assets are the default. --all includes document-hidden bodies on exchange export.
        Exact inspection is limited to 256 visible body instances; two instances include minimum distance.
        Benchmark measures file load, scene construction, first BRep read and verified save, not a rendered frame.
        Exit: 0 success, 1 operation failure, 2 invalid arguments, 130 cancelled.
        """;
    public static async Task<int> RunAsync(string[] args,TextWriter output,TextWriter error,CancellationToken token=default)
    {
        if(args is ["cache-clean",var cacheRoot])
        {
            try{token.ThrowIfCancellationRequested();await output.WriteLineAsync(JsonSerializer.Serialize(DiskAssetCache.Reclaim(cacheRoot),Json));return 0;}
            catch(OperationCanceledException){return 130;}
            catch(Exception ex){await error.WriteLineAsync(ex.Message);return 1;}
        }
        if(args.Length==0||args is ["--help"] or ["help"]){await output.WriteLineAsync(Help);return 0;}
        Options options;
        try{options=Options.Parse(args);}
        catch(ArgumentException ex){await error.WriteLineAsync(ex.Message);return 2;}
        try
        {
            token.ThrowIfCancellationRequested();
            if(options.Command=="benchmark")
            {
                var report=await BenchmarkAsync(options,token);await WriteReport(report,options,output,token);return 0;
            }
            using var disk=options.Memory?null:new DiskAssetStore(Path.Combine(Path.GetTempPath(),"Cadoryx","CliAssets"));
            IAssetStore assets=(IAssetStore?)disk??new MemoryAssetStore();
            var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
            using var loaded=await Load(options.Input,assets,kernel,storage,token);
            if(options.Command=="convert")
            {
                CadExportReport? export=null;
                await AtomicOutput(options.Output!,options.Overwrite,async path=>
                {
                    if(Path.GetExtension(path).Equals(".cadoryx",StringComparison.OrdinalIgnoreCase))
                        await storage.SaveAsync(loaded.Snapshot,assets,path,token);
                    else export=await kernel.ExportAsync(loaded.Snapshot,assets,path,
                        new(options.Linear,options.Angular,VisibleOnly:!options.All),token);
                },token);
                await output.WriteLineAsync(JsonSerializer.Serialize(new{output=options.Output,diagnostics=loaded.Diagnostics.Concat(export?.Diagnostics??[]).ToArray()},Json));return 0;
            }
            var scene=CadScene.FromDocument(loaded.Snapshot);
            if(options.Command=="interference")
            {
                var report=await kernel.CheckInterferenceAsync(scene.Items.Select(i=>new GeometryInstance(i.Path,i.BodyId,i.Geometry,i.WorldTransform)).ToArray(),loaded.Snapshot.Settings.LinearToleranceMm,assets,token);
                await WriteReport(new{schemaVersion=1,input=options.Input,documentId=loaded.Snapshot.Id,stateId=loaded.Snapshot.StateId,
                    sourceSha256=await Hash(options.Input,token),report,note="Visible instances only; unsigned distances and pairwise overlap volumes. Pair volumes are not a union volume or penetration depth."},options,output,token);
                return 0;
            }
            GeometryInspection? exact=null;
            if(options.Exact&&scene.Items.Length>0)exact=await kernel.InspectAsync(scene.Items.Select(i=>new GeometryInstance(i.Path,i.BodyId,i.Geometry,i.WorldTransform)).ToArray(),assets,token);
            await WriteReport(new{schemaVersion=1,input=options.Input,sourceSha256=await Hash(options.Input,token),
                documentId=loaded.Snapshot.Id,stateId=loaded.Snapshot.StateId,name=loaded.Snapshot.Name,
                scale=DocumentScaleReport.Measure(loaded.Snapshot,assets),visibleBodyInstances=scene.Items.Length,
                extensions=loaded.Snapshot.Extensions.IsDefault?0:loaded.Snapshot.Extensions.Length,diagnostics=loaded.Diagnostics,exact,
                note="Exact sums count each instance independently; distances are unsigned and do not quantify penetration."},options,output,token);
            return 0;
        }
        catch(OperationCanceledException){await error.WriteLineAsync("Cancelled.");return 130;}
        catch(Exception ex){await error.WriteLineAsync(ex.Message);return 1;}
    }
    private static Task<LoadedDocument> Load(string path,IAssetStore assets,OcctGeometryKernel kernel,CadDocumentStorage storage,CancellationToken token)=>
        Path.GetExtension(path).Equals(".cadoryx",StringComparison.OrdinalIgnoreCase)?storage.LoadAsync(path,assets,token):kernel.ImportAsync(path,assets,token);
    private static async Task WriteReport(object report,Options options,TextWriter output,CancellationToken token)
    {
        string json=JsonSerializer.Serialize(report,Json);
        if(options.Output is {} path)await AtomicOutput(path,options.Overwrite,p=>File.WriteAllTextAsync(p,json,token),token);
        else await output.WriteLineAsync(json);
    }
    private static async Task AtomicOutput(string path,bool overwrite,Func<string,Task> write,CancellationToken token)
    {
        if(!overwrite&&File.Exists(path))throw new IOException("Output exists; use --overwrite to replace it.");
        var directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
        var temp=Path.Combine(directory,".cadoryx-"+Guid.NewGuid().ToString("N")+Path.GetExtension(path));
        try{await write(temp);token.ThrowIfCancellationRequested();File.Move(temp,path,overwrite);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static async Task<string> Hash(string path,CancellationToken token)
    {await using var stream=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token));}
    private sealed record Sample(int Iteration,double LoadMs,double SceneMs,double FirstBrepReadMs,double SaveMs,
        long ManagedAllocatedBytes,long PrivateBytesBefore,long PrivateBytesAfter,long PeakWorkingSetBytes,
        int VisibleBodyInstances,DocumentScaleReport Scale,long? DiskReads);
    private static async Task<object> BenchmarkAsync(Options options,CancellationToken token)
    {
        string hash=await Hash(options.Input,token);var samples=new List<Sample>();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        for(int i=0;i<options.Iterations;i++)
        {
            token.ThrowIfCancellationRequested();
            using var disk=options.Memory?null:new DiskAssetStore(Path.Combine(Path.GetTempPath(),"Cadoryx","CliBench"));
            IAssetStore assets=(IAssetStore?)disk??new MemoryAssetStore();
            using var process=Process.GetCurrentProcess();process.Refresh();long before=process.PrivateMemorySize64;
            long allocated=GC.GetTotalAllocatedBytes(false);var clock=Stopwatch.StartNew();
            using var loaded=await Load(options.Input,assets,kernel,storage,token);double loadMs=clock.Elapsed.TotalMilliseconds;
            clock.Restart();var scene=CadScene.FromDocument(loaded.Snapshot);double sceneMs=clock.Elapsed.TotalMilliseconds;
            clock.Restart();if(scene.Items.FirstOrDefault() is {} first){using var shape=OcctGeometryBridge.ReadShape(first.Geometry,assets);_ = shape.GetTopologySummary();}
            double firstMs=clock.Elapsed.TotalMilliseconds;
            string path=Path.Combine(Path.GetTempPath(),"cadoryx-benchmark-"+Guid.NewGuid().ToString("N")+".cadoryx");
            double saveMs;
            try{clock.Restart();await storage.SaveAsync(loaded.Snapshot,assets,path,token);saveMs=clock.Elapsed.TotalMilliseconds;}
            finally{if(File.Exists(path))File.Delete(path);}
            var scale=DocumentScaleReport.Measure(loaded.Snapshot,assets);process.Refresh();
            samples.Add(new(i+1,loadMs,sceneMs,firstMs,saveMs,GC.GetTotalAllocatedBytes(false)-allocated,before,
                process.PrivateMemorySize64,process.PeakWorkingSet64,scene.Items.Length,scale,disk?.ReadCount));
        }
        if(hash!=await Hash(options.Input,token))throw new IOException("Benchmark input changed during measurement.");
        return new{schemaVersion=1,recordedUtc=DateTimeOffset.UtcNow,input=options.Input,sourceSha256=hash,
            sourceBytes=new FileInfo(options.Input).Length,assetStore=options.Memory?"memory":"disk",kernel=kernel.Version,
            os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),runtime=RuntimeInformation.FrameworkDescription,
            processor=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),logicalProcessors=Environment.ProcessorCount,
            samples,limitations="Iteration 1 includes cold process initialization; later iterations may use OS/native caches. No forced GC. Peak working set is process-lifetime, not per-iteration. No Viewer/GPU/frame/interaction timing."};
    }
    private sealed record Options(string Command,string Input,string? Output,bool Overwrite,bool Memory,bool Exact,bool All,double Linear,double Angular,int Iterations)
    {
        public static Options Parse(string[] args)
        {
            if(args.Length<2||args[0] is not ("inspect" or "convert" or "benchmark" or "interference"))throw new ArgumentException("Expected inspect, convert, interference or benchmark and an input file. Use --help.");
            string input=Path.GetFullPath(args[1]);string? output=null;int start=2;
            if(args[0]=="convert")
            {
                if(args.Length<3||args[2].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("convert requires an output file.");
                output=Path.GetFullPath(args[2]);start=3;
            }
            bool overwrite=false,memory=false,exact=false,all=false;double linear=.1,angular=.5;int iterations=3;var seen=new HashSet<string>();
            for(int i=start;i<args.Length;i++)
            {
                string key=args[i];if(!seen.Add(key))throw new ArgumentException("Duplicate option: "+key);
                string Value(){if(++i>=args.Length)throw new ArgumentException("Missing value for "+key);return args[i];}
                switch(key)
                {
                    case "--overwrite":overwrite=true;break;
                    case "--memory":memory=true;break;
                    case "--exact" when args[0]=="inspect":exact=true;break;
                    case "--all" when args[0]=="convert":all=true;break;
                    case "--output" when args[0]!="convert":output=Path.GetFullPath(Value());break;
                    case "--linear" when args[0]=="convert":linear=Number(Value());break;
                    case "--angular" when args[0]=="convert":angular=Number(Value());break;
                    case "--iterations" when args[0]=="benchmark":
                        if(!int.TryParse(Value(),NumberStyles.None,CultureInfo.InvariantCulture,out iterations)||iterations is <1 or >20)throw new ArgumentException("iterations must be 1..20.");break;
                    default:throw new ArgumentException("Unsupported option: "+key);
                }
            }
            if(!Supported(input))throw new ArgumentException("Unsupported input extension.");
            if(output is not null&&(StringComparer.OrdinalIgnoreCase.Equals(input,output)))throw new ArgumentException("Input and output must be different files.");
            if(args[0]=="convert"&&!Supported(output!))throw new ArgumentException("Unsupported output extension.");
            if(args[0]=="benchmark"&&output is null)throw new ArgumentException("benchmark requires --output REPORT.json.");
            if(args[0]!="convert"&&output is not null&&!Path.GetExtension(output).Equals(".json",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Report output must be .json.");
            new CadExportOptions(linear,angular).Validate();
            return new(args[0],input,output,overwrite,memory,exact,all,linear,angular,iterations);
        }
        private static bool Supported(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".cadoryx" or ".step" or ".stp" or ".iges" or ".igs" or ".stl";
        private static double Number(string text)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:throw new ArgumentException("Expected a finite decimal using '.'.");
    }
}
