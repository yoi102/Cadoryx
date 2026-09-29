using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.wpf.Views.Dialogs;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class ProgressDialogSmokeRunner
{
    internal static async Task RunAsync(MainWindow window, MainWindowViewModel workspace, IServiceProvider services, string output)
    {
        var dialogs=services.GetRequiredService<IDialogService>();
        var storage=services.GetRequiredService<IDocumentStorage>();
        var host=Find<DialogHost>(window)!;var original=workspace.ActiveDocument!;
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running=dialogs.RunWithProgressAsync(async token=>{entered.SetResult(token);await release.Task;});
        Require(!(await entered.Task).CanBeCanceled,"Non-cancellable operation received a cancellable token.");
        await Visible();var progress=Current();Require(!progress.ShowCancelButton&&progress.CancelButton.Visibility==Visibility.Collapsed,"Default cancel button must be hidden.");
        Capture(progress,Path.Combine(output,"progress-no-cancel.png"));
        DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)!.Close();
        Require(host.IsOpen&&!running.IsCompleted,"Running progress was dismissed prematurely.");
        release.SetResult();await running;Require(!host.IsOpen,"Successful progress did not close.");

        var isolated=new MemoryAssetStore();release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        running=dialogs.RunWithProgressAsync(async token=>
        {
            using var loaded=await Task.Run(()=>storage.LoadAsync(Path.Combine(output,"smoke.cadoryx"),isolated,token));
            entered.SetResult(token);await release.Task;token.ThrowIfCancellationRequested();
        },canCancel:true);
        var cancellation=await entered.Task;await Visible();progress=Current();
        Require(progress.CancelButton.Visibility==Visibility.Visible&&progress.CancelButton.IsEnabled,"Cancellable progress button missing.");
        Capture(progress,Path.Combine(output,"progress-cancel.png"));
        progress.CancelButton.Command.Execute(null);progress.CancelButton.Command.Execute(null);await Idle();
        Require(cancellation.IsCancellationRequested&&!progress.CancelButton.IsEnabled&&host.IsOpen&&!running.IsCompleted,"Cancel must disable itself and wait for the operation.");
        Capture(progress,Path.Combine(output,"progress-cancelling.png"));
        release.SetResult();try{await running;throw new InvalidOperationException("Cancellation was swallowed.");}catch(OperationCanceledException){}
        Require(isolated.Count==0&&!host.IsOpen,"Cancellation leaked assets or progress.");

        await dialogs.RunWithProgressAsync(_=>Task.CompletedTask);
        try{await dialogs.RunWithProgressAsync(_=>Task.FromException(new IOException("Progress smoke failure")));throw new InvalidOperationException("Failure was swallowed.");}
        catch(IOException){}
        Require(!host.IsOpen,"Failed operation left progress open.");
        using(var hidden=dialogs.ShowProgressBarDialog())
        {await Visible();Require(!Current().ShowCancelButton,"Legacy scope default is not hidden.");}
        await Idle();int requests=0;
        var scope=dialogs.ShowProgressBarDialog(showCancelButton:true,cancel:()=>requests++);
        await Visible();Current().CancelButton.Command.Execute(null);Current().CancelButton.Command.Execute(null);
        Require(requests==1&&host.IsOpen,"Scoped cancel callback must run once without closing early.");
        scope.Dispose();await Idle();
        var other=DialogHost.Show(new MessageDialog("Scope ownership","Other content",MessageDialogButton.OK),ViewServiceIdentifiers.RootDialogHost);
        await Idle();scope.Dispose();Require(host.IsOpen,"Old progress scope closed a later dialog.");
        DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)!.Close();await other;

        string ownPath=Path.Combine(output,"progress-open.cadoryx");
        await storage.SaveAsync(DocumentSnapshot.Create("Progress open"),original.Session.Assets,ownPath);
        int docs=workspace.Documents.Count;var log=services.GetRequiredService<ICadMessageLog>();
        int errors=log.Entries.Count(e=>e.Level==CadMessageLevel.Error);int opens=0;
        DialogOpenedEventHandler cancelOpen=(_,e)=>
        {
            if(e.Session.Content is ProgressDialog dialog){opens++;Require(dialog.ShowCancelButton,"File open must offer cancel.");dialog.CancelCommand!.Execute(null);}
        };
        host.DialogOpened+=cancelOpen;
        try
        {
            foreach(string path in new[]{ownPath})
            {
                await workspace.OpenPathAsync(path);
                Require(workspace.Documents.Count==docs&&ReferenceEquals(workspace.ActiveDocument,original)&&!workspace.IsBusy&&!host.IsOpen,"Cancelled open changed the workspace or stayed busy.");
            }
        }
        finally{host.DialogOpened-=cancelOpen;}
        Require(opens==1&&log.Entries.Count(e=>e.Level==CadMessageLevel.Error)==errors,"Cancelled reads must not log failures.");
        await workspace.OpenPathAsync(Path.Combine(output,"missing-progress.cadoryx"));
        Require(workspace.Documents.Count==docs&&!workspace.IsBusy&&!host.IsOpen&&log.Entries.Count(e=>e.Level==CadMessageLevel.Error)==errors+1,"Failed read did not clean up.");
        await workspace.OpenPathAsync(ownPath);var opened=workspace.ActiveDocument!;
        Require(workspace.Documents.Count==docs+1&&opened.Session.FilePath==ownPath&&!host.IsOpen,"Native document did not open through progress.");
        await workspace.OpenPathAsync(ownPath);Require(workspace.Documents.Count==docs+1,"Duplicate file open created a second document.");
        opened.OnClose();await Until(()=>!workspace.Documents.Contains(opened));
        original.IsActive=true;await Idle();
        await File.WriteAllTextAsync(Path.Combine(output,"progress-dialog-result.txt"),"PASS: hidden/visible cancellation, callback once, deferred cancellation and real asset cleanup, early-close rejection, success/failure/fast completion, scope ownership, cancellable native/exchange open, cancelled workspace preservation, failure recovery, native open and duplicate activation.");
    }
    private static ProgressDialog Current()=>DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Content as ProgressDialog
        ??throw new InvalidOperationException("No progress dialog is open.");
    private static Task Visible()=>Until(()=>DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Content is ProgressDialog{IsLoaded:true,IsVisible:true});
    private static async Task Until(Func<bool> done)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(!done()){if(timer.Elapsed.TotalSeconds>5)throw new TimeoutException("Progress UI did not settle.");await Idle();}
        await Idle();
    }
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(60);}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T value)return value;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
    private static void Capture(FrameworkElement view,string path)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
