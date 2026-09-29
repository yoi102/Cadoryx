using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.wpf.Services.IO;
using Cadoryx.wpf.Views.Dialogs;
using Cadoryx.wpf.Views.Toasts;
using Cadoryx.wpf.Services.Toasts;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class ExchangeImportSmokeRunner
{
    internal static async Task RunShutdownAsync(MainWindowViewModel workspace,IServiceProvider services,string output)
    {
        Require(workspace.Documents.Count==0,"Shutdown probe requires closed documents.");
        var importer=(IsolatedExchangeImporter)services.GetRequiredService<IExchangeImportService>();
        var ready=new TaskCompletionSource<(int Id,string Directory)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(Process process,string directory)=>ready.TrySetResult((process.Id,directory));
        importer.WorkerStarted+=Started;
        // Start a real worker, then exercise the production close-all path while import is pending.
        Task? opening=null;
        try
        {
            opening=workspace.OpenPathAsync(Path.Combine(output,"smoke.step"));
            var worker=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(workspace.IsOpening,"Shutdown import was not pending.");
            Require(await workspace.CloseAllAsync(),"Close-all failed during background import.");
            await opening;
            Require(!workspace.IsOpening&&workspace.Documents.Count==0&&!Directory.Exists(worker.Directory),"Close-all left an import/document behind.");
            Require(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Shutdown import leaked assets.");
            await File.WriteAllTextAsync(Path.Combine(output,"notification-shutdown-result.txt"),
                $"PASS: CloseAllAsync cancelled and awaited owned import worker {worker.Id}; zero documents/assets, no temporary job directory.");
        }
        finally
        {
            importer.WorkerStarted-=Started;
            if(opening is {IsCompleted:false})
            {
                if(services.GetRequiredService<CadNotificationService>().ActiveProgress?.DataContext is NotificationModel model)
                    model.CancelCommand!.Execute(null);
                await opening;
            }
        }
    }
    internal static async Task RunAsync(MainWindowViewModel workspace,IServiceProvider services,string output)
    {
        var importer=(IsolatedExchangeImporter)services.GetRequiredService<IExchangeImportService>();
        var notifications=services.GetRequiredService<CadNotificationService>();
        var assets=services.GetRequiredService<IAssetStore>();
        var statistics=(IAssetStoreStatistics)assets;
        var log=services.GetRequiredService<ICadMessageLog>();
        int documents=workspace.Documents.Count,assetCount=statistics.Count;
        int errors=log.Entries.Count(e=>e.Level==CadMessageLevel.Error);
        var original=workspace.ActiveDocument;
        string source=Path.Combine(output,"cancel-large.step");
        // Valid STEP syntax with extra unreferenced points keeps the real native parser busy.
        // This is a cancellation workload, not a representative large-assembly benchmark.
        await Task.Run(()=>
        {
            string seed=File.ReadAllText(Path.Combine(output,"smoke.step"));
            int end=seed.LastIndexOf("ENDSEC;",StringComparison.Ordinal);
            Require(end>0,"STEP fixture has no data terminator.");
            int firstId=System.Text.RegularExpressions.Regex.Matches(seed,@"#(\d+)\s*=")
                .Select(m=>int.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)).Max()+1;
            using var writer=new StreamWriter(source,false,Encoding.ASCII,65536);
            writer.Write(seed[..end]);
            for(int i=0;i<2_000_000;i++)writer.WriteLine($"#{firstId+i}=CARTESIAN_POINT('',(1.,2.,3.));");
            writer.Write(seed[end..]);
        });
        string sourceHash=await HashAsync(source);
        var started=new TaskCompletionSource<(int Id,string Directory)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted(Process process,string directory)=>started.TrySetResult((process.Id,directory));
        importer.WorkerStarted+=OnStarted;
        Task opening=workspace.OpenPathAsync(source);
        long cancelMilliseconds;
        int workerId;
        long sourceBytes=new FileInfo(source).Length;
        try
        {
            var worker=await started.Task.WaitAsync(TimeSpan.FromSeconds(15));workerId=worker.Id;
            using var process=Process.GetProcessById(worker.Id);
            // Exclusive access fails only while the worker actually holds the STEP source open.
            // Also observe CPU progress while it is open; reader.started alone is insufficient.
            await Until(()=>File.Exists(Path.Combine(worker.Directory,"reader.started"))&&IsInUse(source),opening);
            var cpu=process.TotalProcessorTime;
            await Until(()=>process.TotalProcessorTime-cpu>TimeSpan.FromMilliseconds(100)&&IsInUse(source),opening);
            Require(!process.HasExited&&!opening.IsCompleted,"Native reader finished before cancellation.");
            var card=notifications.ActiveProgress;
            Require(card?.DataContext is NotificationModel{IsWorking:true},"STEP cancellation notification missing.");
            Require(workspace.IsOpening&&!workspace.IsBusy&&workspace.NewCommand.CanExecute(null)&&
                DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost) is null,"Background import blocked the workspace.");
            var cancel=(System.Windows.Controls.Button)card!.Template.FindName("CancelImportButton",card);
            Require(cancel.IsEnabled&&cancel.IsVisible,"Notification cancel button missing.");
            NotificationSmokeRunner.Capture(card,Path.Combine(output,"notification-opening.png"));
            var timer=Stopwatch.StartNew();
            cancel.Command.Execute(null);
            Require(!cancel.Command.CanExecute(null),"Repeated cancellation is still enabled.");
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            cancelMilliseconds=timer.ElapsedMilliseconds;
            Require(process.HasExited,"Cancelled native reader is still alive.");
            Require(!Directory.Exists(worker.Directory),"Cancelled import temporary directory remains.");
            Require(!workspace.IsBusy&&!workspace.IsOpening&&notifications.ActiveProgress is null&&DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost) is null,
                "Cancellation left the application busy or the dialog open.");
            Require(workspace.Documents.Count==documents&&ReferenceEquals(workspace.ActiveDocument,original)&&statistics.Count==assetCount,
                "Cancelled STEP changed documents or leaked assets.");
            Require(log.Entries.Count(e=>e.Level==CadMessageLevel.Error)==errors,"Cancellation logged an error.");
            Require(await HashAsync(source)==sourceHash,"Cancellation changed the STEP source.");
        }
        finally
        {
            importer.WorkerStarted-=OnStarted;
            if(!opening.IsCompleted)
            {
                if(notifications.ActiveProgress?.DataContext is NotificationModel model)
                    model.CancelCommand!.Execute(null);
                await opening.WaitAsync(TimeSpan.FromSeconds(5));
            }
            File.Delete(source);
        }

        string invalid=Path.Combine(output,"invalid-import.step");
        await File.WriteAllTextAsync(invalid,"not a STEP document");
        await workspace.OpenPathAsync(invalid);
        Require(workspace.Documents.Count==documents&&statistics.Count==assetCount&&!workspace.IsBusy&&
            log.Entries.Count(e=>e.Level==CadMessageLevel.Error)==errors+1&&
            DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost) is null,"Worker failure did not clean up.");

        // Normal imports immediately after cancellation/failure use the same production service.
        foreach(string extension in new[]{"iges","stl"})
        {
            var isolated=new MemoryAssetStore();
            using(var loaded=await importer.ImportAsync(Path.Combine(output,"smoke."+extension),isolated))
            {
                Require(loaded.Snapshot.Bodies.Count>0,"Isolated import returned no geometry: "+extension);
                Require(!loaded.Diagnostics.IsDefaultOrEmpty,"Import diagnostics lost: "+extension);
            }
            Require(isolated.Count==0,"Successful isolated import leaked assets: "+extension);
        }
        await File.WriteAllTextAsync(Path.Combine(output,"exchange-cancel-result.txt"),
            $"PASS: native STEP source open and CPU active before UI cancellation; worker {workerId} exited; cancel-to-idle {cancelMilliseconds} ms; "+
            $"source {sourceBytes} bytes, SHA256 {sourceHash} unchanged; job directory removed, documents/assets unchanged, no cancellation error; "+
            "bad STEP failure cleanup; subsequent IGES/STL imports and asset cleanup passed. STEP success follows in desktop smoke.");
    }
    private static bool IsInUse(string path)
    {
        try{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);return false;}
        catch(IOException ex)when((ex.HResult&0xffff)==32){return true;}
    }
    private static async Task<string> HashAsync(string path)
    {await using var file=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(file));}
    private static async Task Until(Func<bool> ready,Task opening)
    {
        var timer=Stopwatch.StartNew();
        while(!ready())
        {
            Require(!opening.IsCompleted,"Import completed before the native cancellation probe.");
            if(timer.Elapsed.TotalSeconds>15)throw new TimeoutException("Native reader did not enter STEP parsing.");
            await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
            await Task.Delay(10);
        }
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
}
