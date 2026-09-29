using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels.Services.Platform;

namespace Cadoryx.wpf.Services.IO;

/// <summary>Native exchange parsing is uninterruptible in-process. Only this owned worker is killed on cancel.</summary>
public sealed class IsolatedExchangeImporter(IDocumentStorage storage) : IExchangeImportService
{
    internal static readonly string WorkRoot=Path.Combine(Path.GetTempPath(),"Cadoryx","ImportJobs");
    internal event Action<Process,string>? WorkerStarted;
    internal event Action<ImportPipelineTiming>? ImportTimed;
    public async Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source=Path.GetFullPath(path);
        var directory=Path.Combine(WorkRoot,"import-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var process=new Process();
        ImportWorkerJob? job=null;
        bool started=false,assigned=false;
        Task drain=Task.CompletedTask;
        var elapsed=Stopwatch.StartNew();
        try
        {
            job=new ImportWorkerJob();
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"Cadoryx.wpf.exe"))
            {
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true
            };
            start.ArgumentList.Add("--import-worker");start.ArgumentList.Add(source);start.ArgumentList.Add(directory);
            start.Environment["TEMP"]=directory;start.Environment["TMP"]=directory;
            process.StartInfo=start;
            cancellationToken.ThrowIfCancellationRequested();
            if(!process.Start())throw new IOException("Could not start exchange reader.");
            started=true;
            double launchMs=elapsed.Elapsed.TotalMilliseconds;
            job.Assign(process);assigned=true;
            var stdout=process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            drain=Task.WhenAll(stdout,stderr);
            WorkerStarted?.Invoke(process,directory);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            double workerExitMs=elapsed.Elapsed.TotalMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)
            {
                var error=Path.Combine(directory,"error.txt");
                throw new IOException(File.Exists(error)?await File.ReadAllTextAsync(error,cancellationToken):$"Exchange reader exited ({process.ExitCode}).");
            }
            var diagnostics=JsonSerializer.Deserialize<ImmutableArray<CadDiagnostic>>(
                await File.ReadAllTextAsync(Path.Combine(directory,"diagnostics.json"),cancellationToken));
            using var loaded=await storage.LoadAsync(Path.Combine(directory,"result.cadoryx"),assets,cancellationToken).ConfigureAwait(false);
            double parentLoadMs=elapsed.Elapsed.TotalMilliseconds-workerExitMs;
            var leases=new List<IAssetLease>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach(var id in loaded.Snapshot.ReferencedAssets())leases.Add(assets.Acquire(id));
                var result=new LoadedDocument(loaded.Snapshot,leases,loaded.Diagnostics.AddRange(diagnostics));
                if(ImportTimed is not null)
                {
                    var workerTiming=JsonSerializer.Deserialize<ImportWorkerTiming>(
                        await File.ReadAllTextAsync(Path.Combine(directory,"timing.json"),cancellationToken));
                    ImportTimed(new(launchMs,workerExitMs,parentLoadMs,elapsed.Elapsed.TotalMilliseconds,
                        new FileInfo(Path.Combine(directory,"result.cadoryx")).Length,workerTiming));
                }
                leases.Clear();return result;
            }
            finally{foreach(var lease in leases)lease.Dispose();}
        }
        finally
        {
            // Closing the job kills its reader even inside native ReadFile; never kill the CAD process.
            job?.Dispose();
            try
            {
                if(started)
                {
                    if(!assigned)Stop(process);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    await drain.ConfigureAwait(false);
                }
            }
            finally{Cleanup(directory);}
        }
    }
    private static void Stop(Process process)
    {
        try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}
    }
    private static void Cleanup(string directory)
    {
        var full=Path.GetFullPath(directory);var root=Path.GetFullPath(WorkRoot)+Path.DirectorySeparatorChar;
        var name=Path.GetFileName(full);
        if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!name.StartsWith("import-",StringComparison.Ordinal)||
            !Guid.TryParseExact(name["import-".Length..],"N",out _))return;
        try
        {
            // This private job directory also owns TEMP for the worker's native intermediate files.
            // Reject links rather than following them during recursive cleanup.
            var pending=new Stack<string>();pending.Push(full);
            while(pending.Count>0)
            {
                var folder=pending.Pop();if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)return;
                foreach(var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)return;
                    if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
                }
            }
            Directory.Delete(full,true);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    internal static async Task<int> RunWorkerAsync(string source,string directory)
    {
        // No WPF application, recovery loop, or user settings are initialized in this mode.
        try
        {
            var elapsed=Stopwatch.StartNew();
            var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
            await File.WriteAllTextAsync(Path.Combine(directory,"reader.started"),Environment.ProcessId.ToString());
            using var loaded=await kernel.ImportAsync(source,assets);
            double importMs=elapsed.Elapsed.TotalMilliseconds;
            await new CadDocumentStorage().SaveAsync(loaded.Snapshot,assets,Path.Combine(directory,"result.cadoryx"));
            double saveMs=elapsed.Elapsed.TotalMilliseconds-importMs;
            await File.WriteAllTextAsync(Path.Combine(directory,"diagnostics.json"),JsonSerializer.Serialize(loaded.Diagnostics));
            await File.WriteAllTextAsync(Path.Combine(directory,"timing.json"),JsonSerializer.Serialize(new ImportWorkerTiming(importMs,saveMs,elapsed.Elapsed.TotalMilliseconds)));
            return 0;
        }
        catch(Exception ex)
        {
            try{await File.WriteAllTextAsync(Path.Combine(directory,"error.txt"),ex.Message[..Math.Min(ex.Message.Length,8192)]);}catch(IOException){}
            return 1;
        }
    }
}

internal sealed record ImportWorkerTiming(double KernelImportMs,double TemporarySaveMs,double TotalMs);
internal sealed record ImportPipelineTiming(double LaunchMs,double WorkerExitMs,double ParentLoadMs,double TotalMs,
    long TemporaryBytes,ImportWorkerTiming? Worker);
