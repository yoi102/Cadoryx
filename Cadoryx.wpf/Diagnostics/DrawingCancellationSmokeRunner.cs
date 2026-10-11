using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using OcctSharp;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class DrawingCancellationSmokeRunner
{
    private static readonly string Root=Path.Combine(Path.GetTempPath(),"Cadoryx","ProjectionJobs");
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output,string fixture)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        var rows=new List<object>();
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
        try
        {
            await Idle();var document=main.ActiveDocument!;
            string hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture)));
            Check(hash=="FE2F36DAEAA88198057B88F958980EEB44F919C3FC77693ACB46FF348C1B09FB","Slow fixture hash differs");
            using(var shape=ShapeExchange.ReadBrep(fixture))
            using(var imported=OcctGeometryBridge.StoreShape(shape,document.Session.Assets))
                await document.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(imported.Geometry),"Verified LH teeth"));
            var sheet=DrawingSheetTemplate.Create("Cancellable exact projection");
            await document.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            var storage=services.GetRequiredService<IDocumentStorage>();
            await document.Session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));
            var before=document.Session.Snapshot;int held=((IAssetStoreStatistics)document.Session.Assets).Count;
            string sourceHash=before.Bodies.Values.Single().Geometry.AssetId.Sha256;
            Progress($"fixture {hash}; stored exact source {sourceHash}");
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            foreach(bool close in new[]{false,true})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                main.OpenTechnicalDrawingCommand.Execute(null);await Idle();
                var drawing=Application.Current.Windows.OfType<TechnicalDrawingWindow>().Single();
                drawing.KindChoice.SelectedIndex=(int)DrawingViewKind.Top;
                var previous=Jobs(sourceHash);Progress($"{language} {(close?"close":"cancel")} start");
                await ClickButton(drawing.AddViewButton);
                var(directory,pid)=await Started(sourceHash,previous,drawing);
                using var process=Process.GetProcessById(pid);
                Check(Path.GetFullPath(process.MainModule!.FileName)==Path.Combine(AppContext.BaseDirectory,"Cadoryx.wpf.exe"),"Worker is not frozen WPF executable");
                await Task.Delay(300);await Idle();
                Check(!process.HasExited&&drawing.IsCalculating,"Native HLR must be running");
                Check(drawing.IsEnabled&&drawing.CancelCalculationButton.IsEnabled&&drawing.CancelCalculationButton.IsVisible,"Cancellation control disabled");
                Check(!drawing.AuthorActions.IsEnabled&&!drawing.OutputActions.IsEnabled&&!drawing.DrawingWorkspace.IsEnabled,"Drawing edits/output remain enabled");
                Check(drawing.CancelCalculationButton.Content?.ToString()==Cadoryx.Lang.Strings.Strings.Cancel,"Cancel label differs from active language");
                Save(drawing,Path.Combine(output,$"drawing-computing-{language}-{close}.png"));
                var watch=Stopwatch.StartNew();
                if(close)drawing.Close();else await ClickButton(drawing.CancelCalculationButton);
                await Wait(()=>!drawing.IsCalculating&&!document.Session.IsBusy,TimeSpan.FromSeconds(5));
                double elapsedSeconds=watch.Elapsed.TotalSeconds;
                Check(process.HasExited&&!Directory.Exists(directory)&&previous.SetEquals(Jobs(sourceHash)),"Owned worker/process directory remains");
                Check(ReferenceEquals(before,document.Session.Snapshot)&&document.Session.Snapshot.DrawingSheets[sheet.Id].Views.IsEmpty,"Cancellation committed a partial drawing");
                Check(((IAssetStoreStatistics)document.Session.Assets).Count==held,"Cancellation retained extra assets");
                if(!close)
                {
                    Check(drawing.AuthorActions.IsEnabled&&drawing.OutputActions.IsEnabled&&drawing.DrawingWorkspace.IsEnabled,"Drawing controls not restored");
                    Check(drawing.CancelCalculationButton.Visibility==Visibility.Collapsed&&drawing.Status.Text==Cadoryx.Lang.Strings.Strings.CanceledStatus,"Cancellation status not restored");
                    Save(drawing,Path.Combine(output,$"drawing-cancelled-{language}.png"));
                    await ClickButton(drawing.AddSheetButton);await Wait(()=>!drawing.IsCalculating,TimeSpan.FromSeconds(5));
                    Check(document.Session.Snapshot.DrawingSheets.Count==2,"Parent command queue did not recover");
                    await document.Session.UndoAsync();Check(ReferenceEquals(before,document.Session.Snapshot),"Recovery command undo differs");
                    drawing.Close();
                }
                rows.Add(new{language,action=close?"close":"cancel",pid,elapsedSeconds,originalSnapshot=true,assets=held,workerRemoved=true});
                Progress($"{language} {(close?"close":"cancel")} PASS");
            }
            main.OpenTechnicalDrawingCommand.Execute(null);await Idle();
            var detaching=Application.Current.Windows.OfType<TechnicalDrawingWindow>().Single();
            detaching.KindChoice.SelectedIndex=(int)DrawingViewKind.Top;var detachJobs=Jobs(sourceHash);
            await ClickButton(detaching.AddViewButton);var(detachDirectory,detachPid)=await Started(sourceHash,detachJobs,detaching);
            using(var detachProcess=Process.GetProcessById(detachPid))
            {
                await Task.Delay(300);Check(!detachProcess.HasExited&&detaching.IsCalculating,"Detach must cancel running native HLR");
                var watch=Stopwatch.StartNew();var closing=main.CloseAllAsync();
                Check(await closing.WaitAsync(TimeSpan.FromSeconds(5)),"Close drawing document during native HLR");
                await Wait(()=>!detaching.IsCalculating,TimeSpan.FromSeconds(5));
                Check(!detaching.IsVisible&&detachProcess.HasExited&&!Directory.Exists(detachDirectory)&&detachJobs.SetEquals(Jobs(sourceHash)),"Detach left a drawing window or worker");
                Check(ReferenceEquals(before,document.Session.Snapshot),"Detach committed a partial drawing");
                rows.Add(new{language="ja-JP",action="document-detach",pid=detachPid,elapsedSeconds=watch.Elapsed.TotalSeconds,originalSnapshot=true,workerRemoved=true});
            }
            await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Drawing assets remain after closing");
            await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,input=ButtonInputEvidence,fixtureSha256=hash,sourceHash,rows,assetsAfterClose=0},new JsonSerializerOptions{WriteIndented=true}));
            window.CloseAfterSmoke();
        }
        catch(Exception error)
        {
            foreach(var drawing in Application.Current.Windows.OfType<TechnicalDrawingWindow>().ToArray())drawing.Close();
            await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),rows},new JsonSerializerOptions{WriteIndented=true}));window.CloseAfterSmoke();
        }
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static HashSet<string> Jobs(string hash)=>Directory.Exists(Root)?Directory.GetDirectories(Root,"projection-*").Where(dir=>
    {
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"request.json")));
            return json.RootElement.GetProperty("Assets").EnumerateArray().Any(id=>id.GetProperty("Sha256").GetString()==hash);
        }
        catch(IOException){return false;}catch(JsonException){return false;}
    }).ToHashSet():[];
    private static async Task<(string Directory,int Pid)> Started(string hash,HashSet<string> previous,TechnicalDrawingWindow drawing)
    {
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(30))
        {
            foreach(string dir in Jobs(hash).Except(previous))
            {
                string file=Path.Combine(dir,"native-started.txt");
                if(File.Exists(file)&&int.TryParse(await File.ReadAllTextAsync(file),out int pid))return(dir,pid);
            }
            Check(drawing.IsCalculating,"Drawing ended before native HLR started: "+drawing.Status.Text);await Task.Delay(25);
        }
        throw new InvalidOperationException("Drawing did not enter native HLR");
    }
    private static async Task Wait(Func<bool> ready,TimeSpan budget)
    {var watch=Stopwatch.StartNew();while(!ready()){Check(watch.Elapsed<budget,"Drawing cancellation deadline exceeded");await Task.Delay(25);}await Idle();}
    private static Task Idle()=>Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle).Task;
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
