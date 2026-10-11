using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Kernel.Occt;

/// <summary>Bounded native work with owned process lifetime and hash-checked asset transfer.</summary>
public sealed class IsolatedMetricThreadExecutor:IMetricThreadExecutor
{
    private const int Contract=1;
    private const long MaximumAssetBytes=512L*1024*1024,MaximumJsonBytes=32L*1024*1024;
    private static readonly string WorkRoot=Path.Combine(Path.GetTempPath(),"Cadoryx","ThreadJobs");
    private static readonly JsonSerializerOptions Json=new(){MaxDepth=64,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new PathConverter()}};
    internal static JsonSerializerOptions SharedJson=>Json;
    private readonly string executable;
    private readonly TimeSpan budget;
    public IsolatedMetricThreadExecutor(string executable,TimeSpan? budget=null)
    {
        this.executable=Path.GetFullPath(executable);this.budget=budget??TimeSpan.FromSeconds(90);
        if(!File.Exists(this.executable))throw new FileNotFoundException("Native thread worker executable is missing.",this.executable);
        if(this.budget<TimeSpan.FromSeconds(1)||this.budget>TimeSpan.FromMinutes(10))throw new ArgumentOutOfRangeException(nameof(budget));
    }
    private sealed record Request(int Contract,GeometryRecipe Recipe,AssetId[] Assets,GeometryAssetRef? Current=null,DrawingTopologyReference? Selected=null,AssetId[]? ReferenceAssets=null);
    private sealed record Response(int Contract,string Kernel,GeometryAssetRef? Geometry=null,TopologyHistory? History=null,
        ImmutableArray<CadDiagnostic> Diagnostics=default,DrawingHoleEvidence? Proof=null,string? Error=null,bool Validation=false);
    public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        RequireRecipe(recipe);using var result=await ExecuteAsync(recipe,null,null,assets,token).ConfigureAwait(false);
        var response=result.Response;var geometry=response.Geometry??throw new InvalidDataException("Native worker omitted its geometry.");
        geometry.Validate();response.History?.Validate();
        var lease=assets.Stage(await ReadBoundedAsync(Path.Combine(result.Directory,"result.brep"),MaximumAssetBytes,token).ConfigureAwait(false));
        try
        {
            if(lease.Id!=geometry.AssetId)throw new InvalidDataException("Native worker result asset hash differs from its geometry.");
            token.ThrowIfCancellationRequested();
            return new(geometry,lease,response.Diagnostics){TopologyHistory=response.History};
        }
        catch{lease.Dispose();throw;}
    }
    public async Task<DrawingHoleEvidence> InspectAsync(HoleRecipe recipe,GeometryAssetRef current,DrawingTopologyReference selected,
        IAssetStore assets,CancellationToken token)
    {
        RequireRecipe(recipe);selected.Validate(selected.Path.DocumentId);current.Validate();
        using var result=await ExecuteAsync(recipe,current,selected,assets,token).ConfigureAwait(false);
        var proof=result.Response.Proof??throw new InvalidDataException("Native worker omitted its thread proof.");
        proof.Validate(selected.Path.DocumentId);
        if(proof.Source!=selected||proof.Thread!=recipe.Thread)throw new InvalidDataException("Native worker proof belongs to another selection or author.");
        token.ThrowIfCancellationRequested();return proof;
    }
    private static void RequireRecipe(GeometryRecipe recipe)
    {
        recipe.Validate();
        if(recipe is not HoleRecipe {Thread.Representation:ThreadHoleRepresentation.Modeled} and
            not HolePatternRecipe {Template.Thread.Representation:ThreadHoleRepresentation.Modeled})
            throw new CadValidationException("Native thread worker only accepts a modeled hole or its authored replay.");
    }
    private sealed class Result(Response response,string directory):IDisposable
    {
        internal Response Response=>response;internal string Directory=>directory;
        public void Dispose()=>Cleanup(directory);
    }
    private async Task<Result> ExecuteAsync(GeometryRecipe recipe,GeometryAssetRef? current,DrawingTopologyReference? selected,
        IAssetStore assets,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();string directory=Path.Combine(WorkRoot,"thread-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);bool returned=false,started=false,assigned=false;
        using var process=new Process();MetricThreadWorkerJob? job=null;Task drain=Task.CompletedTask;
        try
        {
            AssetId[] inputs=[..recipe.AssetInputs.Select(g=>g.AssetId).Concat(current is null?[]:new[]{current.AssetId}).Distinct()];
            AssetId[] references=[..recipe.AssetInputs.Concat(current is null?[]:new[]{current}).SelectMany(g=>g.BoundaryAssets().Skip(1)).Distinct()];
            long total=0;
            foreach(var id in inputs.Concat(references).Distinct())
            {
                token.ThrowIfCancellationRequested();id.Validate();using var hold=assets.Acquire(id);
                if(hold.Length>MaximumAssetBytes||(total+=hold.Length)>2*MaximumAssetBytes)
                    throw new CadValidationException("Native thread input exceeds the bounded asset transfer budget.");
                await File.WriteAllBytesAsync(Path.Combine(directory,id.Sha256+".brep"),hold.Content.ToArray(),token).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(directory,"request.json"),JsonSerializer.Serialize(new Request(Contract,recipe,inputs,current,selected,references),Json),token).ConfigureAwait(false);
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("--native-thread-worker");start.ArgumentList.Add(directory);
            start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System)+";"+Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach(string name in start.Environment.Keys.Where(k=>k.StartsWith("CSF_",StringComparison.OrdinalIgnoreCase)||
                k.StartsWith("OCCT",StringComparison.OrdinalIgnoreCase)||k.Equals("CASROOT",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(name);
            process.StartInfo=start;job=new();token.ThrowIfCancellationRequested();
            if(!process.Start())throw new IOException("Native thread worker could not start.");started=true;
            job.Assign(process);assigned=true;
            drain=Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),process.StandardError.BaseStream.CopyToAsync(Stream.Null));
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(budget);
            try{await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);}
            catch(OperationCanceledException) when(!token.IsCancellationRequested)
            {throw new CadValidationException($"Native thread work exceeded {budget.TotalSeconds:0} seconds. Reduce its length/count or use symbolic representation; no document change was committed.");}
            await drain.ConfigureAwait(false);token.ThrowIfCancellationRequested();
            var response=JsonSerializer.Deserialize<Response>(await ReadBoundedAsync(Path.Combine(directory,"response.json"),MaximumJsonBytes,token).ConfigureAwait(false),Json)
                ??throw new InvalidDataException("Native thread worker returned no response.");
            if(response.Contract!=Contract||response.Kernel!=new OcctGeometryKernel().Version)throw new InvalidDataException("Native thread worker contract or kernel version differs.");
            if(process.ExitCode!=0||response.Error is not null)
            {
                if(response.Validation)throw new CadValidationException(response.Error??"Native thread validation failed.");
                throw new IOException(response.Error??$"Native thread worker exited ({process.ExitCode}).");
            }
            returned=true;return new(response,directory);
        }
        finally
        {
            job?.Dispose();
            if(started)
            {
                if(!assigned&&!process.HasExited)process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync().ConfigureAwait(false);await drain.ConfigureAwait(false);
            }
            if(!returned)Cleanup(directory);
        }
    }
    public static async Task<int> RunWorkerAsync(string directory)
    {
        directory=Path.GetFullPath(directory);RequireDirectory(directory);var kernel=new OcctGeometryKernel();
        // Resolve and validate the inherited temporary root before isolating native
        // scratch files. Changing TEMP in the parent would change the child's root.
        Environment.SetEnvironmentVariable("TEMP",directory);Environment.SetEnvironmentVariable("TMP",directory);
        var holds=new List<IAssetLease>();var assets=new MemoryAssetStore();
        try
        {
            var request=JsonSerializer.Deserialize<Request>(await ReadBoundedAsync(Path.Combine(directory,"request.json"),MaximumJsonBytes,CancellationToken.None),Json)
                ??throw new InvalidDataException("Missing native thread request.");
            if(request.Contract!=Contract||request.Assets is not {Length:>=1 and <=3}||request.Assets.Distinct().Count()!=request.Assets.Length)
                throw new InvalidDataException("Invalid native thread request contract or assets.");
            RequireRecipe(request.Recipe);
            var expected=request.Recipe.AssetInputs.Select(g=>g.AssetId).Concat(request.Current is null?[]:new[]{request.Current.AssetId}).ToHashSet();
            if(!expected.SetEquals(request.Assets))throw new InvalidDataException("Native thread request assets differ from the exact author inputs.");
            var references=request.ReferenceAssets??[];
            var expectedReferences=request.Recipe.AssetInputs.Concat(request.Current is null?[]:new[]{request.Current})
                .SelectMany(g=>g.BoundaryAssets().Skip(1)).ToHashSet();
            if(references.Length>3||references.Distinct().Count()!=references.Length||!expectedReferences.SetEquals(references))
                throw new InvalidDataException("Native thread reference assets differ from the exact author inputs.");
            long total=0;
            foreach(var id in request.Assets.Concat(references).Distinct())
            {
                id.Validate();var bytes=await ReadBoundedAsync(Path.Combine(directory,id.Sha256+".brep"),MaximumAssetBytes,CancellationToken.None);
                if((total+=bytes.LongLength)>2*MaximumAssetBytes)throw new InvalidDataException("Native thread input transfer budget exceeded.");
                var lease=assets.Stage(bytes);holds.Add(lease);if(lease.Id!=id)throw new InvalidDataException("Native thread input hash mismatch.");
            }
            kernel.ThreadNativeStarted=()=>NativeWorkerStartedMarker.Publish(directory);
            Response response;
            if(request.Current is {} current&&request.Selected is {} selected&&request.Recipe is HoleRecipe hole)
                response=new(Contract,kernel.Version,Diagnostics:[],Proof:await kernel.InspectNativeModeledThreadAsync(hole,current,selected,assets));
            else if(request.Current is null&&request.Selected is null)
            {
                using var result=await kernel.EvaluateAsync(request.Recipe,assets);using var bytes=GeometryAssetLease.Acquire(result.Geometry,assets);
                await File.WriteAllBytesAsync(Path.Combine(directory,"result.brep"),bytes.Content.ToArray());
                response=new(Contract,kernel.Version,result.Geometry,result.TopologyHistory,result.Diagnostics);
            }
            else throw new InvalidDataException("Incomplete native thread proof request.");
            await File.WriteAllTextAsync(Path.Combine(directory,"response.json"),JsonSerializer.Serialize(response,Json));return 0;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory,"response.json"),JsonSerializer.Serialize(new Response(Contract,kernel.Version,
                Diagnostics:[],Error:error.Message,Validation:error is CadValidationException),Json));return 1;
        }
        finally{foreach(var hold in holds)hold.Dispose();}
    }
    private static async Task<byte[]> ReadBoundedAsync(string path,long limit,CancellationToken token)
    {
        var info=new FileInfo(path);if(!info.Exists||info.Length<1||info.Length>limit)throw new InvalidDataException("Native thread payload is missing or exceeds its size budget.");
        var bytes=await File.ReadAllBytesAsync(path,token).ConfigureAwait(false);
        if(bytes.LongLength!=info.Length||bytes.LongLength>limit)throw new InvalidDataException("Native thread payload changed during transfer.");return bytes;
    }
    private static void RequireDirectory(string directory)
    {
        string boundary=Path.GetFullPath(WorkRoot)+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)||Path.GetDirectoryName(directory)!=Path.GetFullPath(WorkRoot)||
            !Path.GetFileName(directory).StartsWith("thread-",StringComparison.Ordinal))throw new InvalidDataException("Native thread job must stay in its owned temporary directory.");
    }
    private static void Cleanup(string directory)
    {
        RequireDirectory(Path.GetFullPath(directory));
        try{Directory.Delete(directory,recursive:true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private sealed record PathData(DocumentId DocumentId,ImmutableArray<ComponentSlotId> Slots);
    private sealed class PathConverter:JsonConverter<OccurrencePath>
    {
        public override OccurrencePath Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
        {var value=JsonSerializer.Deserialize<PathData>(ref reader,options)??throw new JsonException("Missing occurrence path.");return new(value.DocumentId,value.Slots);}
        public override void Write(Utf8JsonWriter writer,OccurrencePath value,JsonSerializerOptions options)
            =>JsonSerializer.Serialize(writer,new PathData(value.DocumentId,value.Slots),options);
    }
}
