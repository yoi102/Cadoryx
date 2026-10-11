using System.Diagnostics;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Kernel.Occt;

/// <summary>Retains exact HLR/section algorithms with an independently owned native lifetime.</summary>
public sealed class IsolatedDrawingExecutor:INativeDrawingExecutor
{
    private const int Contract=1;
    private const long MaximumAssetBytes=512L*1024*1024,MaximumJsonBytes=32L*1024*1024;
    private static readonly string WorkRoot=Path.Combine(Path.GetTempPath(),"Cadoryx","ProjectionJobs");
    private static JsonSerializerOptions Json=>IsolatedMetricThreadExecutor.SharedJson;
    private readonly string executable;private readonly TimeSpan budget;
    public IsolatedDrawingExecutor(string executable,TimeSpan? budget=null)
    {
        this.executable=Path.GetFullPath(executable);this.budget=budget??TimeSpan.FromMinutes(5);
        if(!File.Exists(this.executable))throw new FileNotFoundException("Native drawing worker executable is missing.",this.executable);
        if(this.budget<TimeSpan.FromSeconds(1)||this.budget>TimeSpan.FromMinutes(10))throw new ArgumentOutOfRangeException(nameof(budget));
    }
    private sealed record Request(int Contract,GeometryInstance[] Instances,AssetId[] Assets,DrawingViewKind Kind,
        Vector3d? SectionNormal,double SectionOffsetMm,double HatchSpacingMm,SectionHatchPattern[]? Patterns,AssetId[]? ReferenceAssets=null);
    private sealed record Response(int Contract,string Kernel,DrawingProjectionResult? Projection=null,string? Error=null,bool Validation=false);
    public async Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> instances,DrawingViewKind kind,
        Vector3d? sectionNormal,double sectionOffsetMm,IAssetStore assets,CancellationToken token=default,
        double sectionHatchSpacingMm=2.5,IReadOnlyList<SectionHatchPattern>? sectionHatchPatterns=null)
    {
        token.ThrowIfCancellationRequested();var inputs=instances.ToArray();
        if(inputs.Length is <1 or >256)throw new CadValidationException("Drawing worker requires one to 256 source instances.");
        foreach(var input in inputs){input.Geometry.Validate();input.WorldTransform.Validate();}
        AssetId[] ids=[..inputs.Select(i=>i.Geometry.AssetId).Distinct()];
        AssetId[] references=[..inputs.SelectMany(i=>i.Geometry.BoundaryAssets().Skip(1)).Distinct()];
        var request=new Request(Contract,inputs,ids,kind,sectionNormal,sectionOffsetMm,sectionHatchSpacingMm,sectionHatchPatterns?.ToArray(),references);
        string directory=Path.Combine(WorkRoot,"projection-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using var process=new Process();MetricThreadWorkerJob? job=null;bool started=false,assigned=false;Task drain=Task.CompletedTask;
        try
        {
            long total=0;
            foreach(var id in ids.Concat(references).Distinct())
            {
                token.ThrowIfCancellationRequested();using var hold=assets.Acquire(id);
                if(hold.Length>MaximumAssetBytes||(total+=hold.Length)>2*MaximumAssetBytes)
                    throw new CadValidationException("Native drawing input exceeds the bounded asset transfer budget.");
                await File.WriteAllBytesAsync(Path.Combine(directory,id.Sha256+".brep"),hold.Content.ToArray(),token).ConfigureAwait(false);
            }
            var json=JsonSerializer.SerializeToUtf8Bytes(request,Json);
            if(json.LongLength>MaximumJsonBytes)throw new CadValidationException("Drawing author request exceeds its JSON transfer budget.");
            await File.WriteAllBytesAsync(Path.Combine(directory,"request.json"),json,token).ConfigureAwait(false);
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("--native-drawing-worker");start.ArgumentList.Add(directory);
            start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System)+";"+Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach(string name in start.Environment.Keys.Where(k=>k.StartsWith("CSF_",StringComparison.OrdinalIgnoreCase)||
                k.StartsWith("OCCT",StringComparison.OrdinalIgnoreCase)||k.Equals("CASROOT",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(name);
            process.StartInfo=start;job=new();token.ThrowIfCancellationRequested();
            if(!process.Start())throw new IOException("Native drawing worker could not start.");started=true;job.Assign(process);assigned=true;
            drain=Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),process.StandardError.BaseStream.CopyToAsync(Stream.Null));
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(budget);
            try{await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);}
            catch(OperationCanceledException) when(!token.IsCancellationRequested)
            {throw new CadValidationException($"Native drawing work exceeded {budget.TotalSeconds:0} seconds. Reduce its source scope; no drawing change was committed.");}
            await drain.ConfigureAwait(false);token.ThrowIfCancellationRequested();
            var response=JsonSerializer.Deserialize<Response>(await ReadBounded(Path.Combine(directory,"response.json"),MaximumJsonBytes,token).ConfigureAwait(false),Json)
                ??throw new InvalidDataException("Native drawing worker returned no response.");
            if(response.Contract!=Contract||response.Kernel!=new OcctGeometryKernel().Version)throw new InvalidDataException("Native drawing worker contract or kernel version differs.");
            if(process.ExitCode!=0||response.Error is not null)
            {
                if(response.Validation)throw new CadValidationException(response.Error??"Native drawing validation failed.");
                throw new IOException(response.Error??$"Native drawing worker exited ({process.ExitCode}).");
            }
            var result=response.Projection??throw new InvalidDataException("Native drawing worker omitted its projection.");
            ValidateResult(result);token.ThrowIfCancellationRequested();return result;
        }
        finally
        {
            job?.Dispose();
            if(started)
            {
                if(!assigned&&!process.HasExited)process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync().ConfigureAwait(false);await drain.ConfigureAwait(false);
            }
            Cleanup(directory);
        }
    }
    public static async Task<int> RunWorkerAsync(string directory)
    {
        directory=Path.GetFullPath(directory);RequireDirectory(directory);
        Environment.SetEnvironmentVariable("TEMP",directory);Environment.SetEnvironmentVariable("TMP",directory);
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();var holds=new List<IAssetLease>();
        try
        {
            var request=JsonSerializer.Deserialize<Request>(await ReadBounded(Path.Combine(directory,"request.json"),MaximumJsonBytes,CancellationToken.None),Json)
                ??throw new InvalidDataException("Missing native drawing request.");
            if(request.Contract!=Contract||request.Instances is not {Length:>=1 and <=256}||request.Assets is not {Length:>=1 and <=256}||
                request.Assets.Distinct().Count()!=request.Assets.Length||!request.Instances.Select(i=>i.Geometry.AssetId).ToHashSet().SetEquals(request.Assets))
                throw new InvalidDataException("Invalid native drawing contract or exact input asset set.");
            var references=request.ReferenceAssets??[];
            if(references.Length>256||references.Distinct().Count()!=references.Length||
                !request.Instances.SelectMany(i=>i.Geometry.BoundaryAssets().Skip(1)).ToHashSet().SetEquals(references))
                throw new InvalidDataException("Native drawing reference assets differ from the exact sources.");
            long total=0;
            foreach(var id in request.Assets.Concat(references).Distinct())
            {
                id.Validate();var bytes=await ReadBounded(Path.Combine(directory,id.Sha256+".brep"),MaximumAssetBytes,CancellationToken.None);
                if((total+=bytes.LongLength)>2*MaximumAssetBytes)throw new InvalidDataException("Native drawing input transfer budget exceeded.");
                var lease=assets.Stage(bytes);holds.Add(lease);if(lease.Id!=id)throw new InvalidDataException("Native drawing input hash mismatch.");
            }
            kernel.DrawingNativeStarted=()=>NativeWorkerStartedMarker.Publish(directory);
            var projection=await kernel.ProjectAsync(request.Instances,request.Kind,request.SectionNormal,request.SectionOffsetMm,assets,
                sectionHatchSpacingMm:request.HatchSpacingMm,sectionHatchPatterns:request.Patterns);
            ValidateResult(projection);var json=JsonSerializer.SerializeToUtf8Bytes(new Response(Contract,kernel.Version,projection),Json);
            if(json.LongLength>MaximumJsonBytes)throw new CadValidationException("Native projection exceeds its JSON transfer budget.");
            await File.WriteAllBytesAsync(Path.Combine(directory,"response.json"),json);return 0;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory,"response.json"),JsonSerializer.Serialize(new Response(Contract,kernel.Version,
                Error:error.Message,Validation:error is CadValidationException),Json));return 1;
        }
        finally{foreach(var hold in holds)hold.Dispose();}
    }
    private static void ValidateResult(DrawingProjectionResult result)
    {
        if(result.Strokes.IsDefaultOrEmpty||result.Strokes.Length>100000||!double.IsFinite(result.Minimum.X)||!double.IsFinite(result.Minimum.Y)||
            !double.IsFinite(result.Maximum.X)||!double.IsFinite(result.Maximum.Y)||result.Minimum.X>result.Maximum.X||result.Minimum.Y>result.Maximum.Y)
            throw new InvalidDataException("Invalid native drawing result bounds or stroke count.");
        long count=0;
        foreach(var stroke in result.Strokes)
        {stroke.Validate();if((count+=stroke.Points.Length)>1000000)throw new InvalidDataException("Native drawing point budget exceeded.");}
    }
    private static async Task<byte[]> ReadBounded(string path,long limit,CancellationToken token)
    {
        var info=new FileInfo(path);if(!info.Exists||info.Length<1||info.Length>limit)throw new InvalidDataException("Native drawing payload is missing or exceeds its size budget.");
        var bytes=await File.ReadAllBytesAsync(path,token).ConfigureAwait(false);
        if(bytes.LongLength!=info.Length||bytes.LongLength>limit)throw new InvalidDataException("Native drawing payload changed during transfer.");return bytes;
    }
    private static void RequireDirectory(string directory)
    {
        string boundary=Path.GetFullPath(WorkRoot)+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)||Path.GetDirectoryName(directory)!=Path.GetFullPath(WorkRoot)||
            !Path.GetFileName(directory).StartsWith("projection-",StringComparison.Ordinal))throw new InvalidDataException("Native drawing job must stay in its owned temporary directory.");
    }
    private static void Cleanup(string directory)
    {
        RequireDirectory(Path.GetFullPath(directory));
        try{Directory.Delete(directory,recursive:true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
