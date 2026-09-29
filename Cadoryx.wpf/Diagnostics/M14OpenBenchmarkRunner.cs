using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Services.IO;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

/// <summary>Measures the real UI open command and its isolated reader, not a direct kernel import.</summary>
internal static class M14OpenBenchmarkRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string input,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var importer=(IsolatedExchangeImporter)services.GetRequiredService<IExchangeImportService>();
        var first=new TaskCompletionSource<ProgressiveSceneTiming>(TaskCreationOptions.RunContinuationsAsynchronously);
        ImportPipelineTiming? import=null;
        void Imported(ImportPipelineTiming timing)=>import=timing;
        void Scene(ProgressiveSceneTiming timing)
        {
            if(!timing.IsSecondary)first.TrySetResult(timing);
        }
        importer.ImportTimed+=Imported;ViewportPane.SceneLoadTimed+=Scene;
        try
        {
            void Stage(string value)=>File.WriteAllText(Path.Combine(output,"progress.txt"),value);
            var vm=(MainWindowViewModel)window.DataContext;
            Stage("waiting for workspace");
            await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Stage("closing initial document");
            foreach(var initial in vm.Documents)
                await initial.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),Path.Combine(output,"initial.cadoryx"));
            if(!await vm.CloseAllAsync())throw new InvalidOperationException("Could not close the initial workspace.");
            using var source=File.OpenRead(input);
            string hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(source));
            long sourceBytes=source.Length;
            var elapsed=Stopwatch.StartNew();
            Stage("opening STEP");
            await vm.OpenPathAsync(input);
            Stage("waiting for full scene");
            double openedMs=elapsed.Elapsed.TotalMilliseconds;
            var doc=vm.ActiveDocument??throw new InvalidOperationException("Open did not attach a document: "+
                string.Join(" | ",services.GetRequiredService<ICadMessageLog>().Entries.Select(e=>e.Text)));
            if(doc.Scene.Items.Length<300)throw new InvalidOperationException("M14 benchmark needs a large progressive scene.");
            var scene=await first.Task.WaitAsync(TimeSpan.FromMinutes(5));
            if(scene.DocumentId!=doc.Session.Snapshot.Id||scene.VisibleInstances!=doc.Scene.Items.Length)
                throw new InvalidOperationException("Progressive scene belongs to a different document.");
            double completeMs=elapsed.Elapsed.TotalMilliseconds;
            var host=FindHost(window,doc)??throw new InvalidOperationException("Primary native viewport unavailable.");
            var viewport=host.Viewport??throw new InvalidOperationException("Native viewport unavailable.");
            if(viewport.VisibleBodyCount!=scene.VisibleInstances)throw new InvalidOperationException("Full scene was not committed.");
            viewport.SaveScreenshot(Path.Combine(output,"full-scene.png"));
            var stats=(IAssetStoreStatistics)services.GetRequiredService<IAssetStore>();
            var report=new{schemaVersion=1,input=Path.GetFullPath(input),sourceBytes,sourceSha256=hash,
                recordedUtc=DateTimeOffset.UtcNow,openedMs,firstVisibleFromClickMs=completeMs-scene.CompleteMs+scene.FirstVisibleMs,
                fullSceneFromClickMs=completeMs,import,scene,definitions=doc.Session.Snapshot.Definitions.Count,
                bodies=doc.Session.Snapshot.Bodies.Count,visibleInstances=scene.VisibleInstances,
                xdeContexts=viewport.NativeXdeContextCount,nativeGeometry=viewport.NativeGeometryCount,
                submissionProfile=viewport.SubmissionProfile,
                deferredAssets=(services.GetRequiredService<IAssetStore>() as DiskAssetStore)?.DeferredCount,
                assetsAtFullScene=stats.Count,privateBytes=Process.GetCurrentProcess().PrivateMemorySize64,
                handles=Process.GetCurrentProcess().HandleCount};
            await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            await doc.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),Path.Combine(output,"opened.cadoryx"));
            if(!await vm.CloseAllAsync())throw new InvalidOperationException("Could not close imported document.");
            await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(stats.Count!=0||OcctViewportHost.LiveCount!=0)
                throw new InvalidOperationException($"Import leaked {stats.Count} assets or {OcctViewportHost.LiveCount} viewports.");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: production OpenPathAsync, isolated worker, full native scene, screenshot and resource release.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);
            Application.Current.Shutdown(1);
        }
        finally{importer.ImportTimed-=Imported;ViewportPane.SceneLoadTimed-=Scene;}
    }
    private static OcctViewportHost? FindHost(DependencyObject root,CadDocumentViewModel doc)
    {
        if(root is OcctViewportHost host&&ReferenceEquals(host.DataContext,doc))return host;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindHost(VisualTreeHelper.GetChild(root,i),doc) is {} found)return found;
        return null;
    }
}
