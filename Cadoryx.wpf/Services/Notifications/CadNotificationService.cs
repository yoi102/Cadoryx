using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.wpf.Views.Toasts;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using Notifications;
using Notifications.Enums;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.wpf.Services.Toasts;

public sealed class CadNotificationService : ICadNotificationService,IDisposable
{
    private readonly ICadMessageLog log;
    private readonly NotificationManager manager=new(new NotificationManagerOptions
    {
        ShowCountdownBar=false,PauseOnHover=true,PauseOnKeyboardFocus=true
    });
    private NotificationHostWindow? host;
    internal CadNotificationAnchor Anchor {get;private set;}=CadNotificationAnchor.ApplicationWindow;
    public void SetAnchor(CadNotificationAnchor anchor)
    {
        Anchor=Enum.IsDefined(anchor)?anchor:CadNotificationAnchor.ApplicationWindow;
        host?.SetAnchor(Anchor);
    }
    private bool disposed;
    private readonly HashSet<INotificationHandle> active=[];
    internal CadNotification? ActiveProgress {get;private set;}
    internal event Action<CadNotification>? Shown;
    internal event Action<Exception>? Failed;
    public CadNotificationService(ICadMessageLog log){this.log=log;log.MessageAdded+=OnMessageAdded;}

    private async void OnMessageAdded(object? sender,CadMessageEntry entry)
    {
        // Detailed exchange diagnostics remain in the log; one completion card summarizes the open.
        if(entry.Level==CadMessageLevel.Information&&entry.Source?.StartsWith("IMPORT.",StringComparison.Ordinal)==true)return;
        try{await OnUiAsync(async()=>{if(!disposed)await ShowMessageAsync(entry);});}
        catch(Exception ex){Trace.TraceError("Notification failed: {0}",ex);Failed?.Invoke(ex);} // Never recurse into the message log.
    }
    private async Task ShowMessageAsync(CadMessageEntry entry)
    {
        bool success=entry.Source=="Open.Completed";
        var model=new NotificationModel
        {
            Title=success?Strings.NotificationCompleted:entry.Level switch
            {CadMessageLevel.Error=>Strings.NotificationError,CadMessageLevel.Warning=>Strings.NotificationWarning,_=>Strings.NotificationInformation},
            Message=entry.Text,
            Accent=new SolidColorBrush((Color)ColorConverter.ConvertFromString(success?"#43B985":entry.Level switch
            {CadMessageLevel.Error=>"#E66D78",CadMessageLevel.Warning=>"#DDA647",_=>"#579FE8"})),
            Icon=success?PackIconKind.CheckCircleOutline:entry.Level switch
            {CadMessageLevel.Error=>PackIconKind.AlertCircleOutline,CadMessageLevel.Warning=>PackIconKind.AlertOutline,_=>PackIconKind.InformationOutline}
        };
        await ShowAsync(model,entry.Level==CadMessageLevel.Error?TimeSpan.FromSeconds(10):TimeSpan.FromSeconds(5));
    }
    public Task RunWithProgressAsync(Func<CancellationToken,Task> operation,string message,CancellationToken cancellationToken=default)
        =>OnUiAsync(async()=>
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool requested=false;
            var model=new NotificationModel{Title=Strings.NotificationBackgroundOpening,Message=message,
                Accent=new SolidColorBrush(Color.FromRgb(87,159,232)),Icon=PackIconKind.FolderOpenOutline,IsWorking=true};
            model.CancelCommand=new RelayCommand(()=>
            {
                if(requested)return;requested=true;model.CancelCommand!.NotifyCanExecuteChanged();
                model.Message=Strings.ProgressCancelling;cancellation.Cancel();
            },()=>!requested);
            var (card,handle)=await ShowAsync(model,TimeSpan.MaxValue);
            ActiveProgress=card;
            try{await operation(cancellation.Token);}
            finally{ActiveProgress=null;await handle.CloseAsync();}
        });
    private async Task<(CadNotification Card,INotificationHandle Handle)> ShowAsync(NotificationModel model,TimeSpan duration)
    {
        host??=new NotificationHostWindow(System.Windows.Application.Current.MainWindow,Anchor);
        await host.PrepareAsync();
        ObjectDisposedException.ThrowIf(disposed,this);
        var card=new CadNotification(model);
        var handle=await manager.ShowAsync(new NotificationRequest(card)
        {
            Target=NotificationTarget.Area(host.AreaIdentifier),
            ExpirationTime=duration,ShowCloseButton=!model.IsWorking,ShowCountdownBar=false
        });
        card.Handle=handle;active.Add(handle);_ = ForgetClosedAsync(handle);
        try{await host.PresentAsync();card.Present();Shown?.Invoke(card);}
        catch{await handle.CloseAsync();throw;}
        return(card,handle);
    }
    private async Task ForgetClosedAsync(INotificationHandle handle)
    {await handle.Completion;active.Remove(handle);if(!disposed)host?.SetHasItems(active.Count>0);}
    private static Task OnUiAsync(Func<Task> action)=>System.Windows.Application.Current.Dispatcher.CheckAccess()?action():
        System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task.Unwrap();
    public async Task StopAsync()
    {
        Unsubscribe();
        try{await manager.DisposeAsync();}
        finally{manager.Dispose();host?.Close();host=null;}
    }
    private void Unsubscribe(){if(disposed)return;disposed=true;log.MessageAdded-=OnMessageAdded;}
    public void Dispose(){Unsubscribe();manager.Dispose();host?.Close();host=null;}
}
