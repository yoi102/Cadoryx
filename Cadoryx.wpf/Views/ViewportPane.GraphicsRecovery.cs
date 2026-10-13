using System.Windows.Threading;
using Cadoryx.Rendering;
using Cadoryx.Rendering.Occt;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.Lang.Strings;

namespace Cadoryx.wpf.Views;

public partial class ViewportPane
{
    private DispatcherOperation? pendingGraphicsRecovery;
    private DispatcherTimer? graphicsRecoveryTimer;
    private long graphicsAttachmentEpoch;
    private long graphicsRecoveryEpoch;
    private bool graphicsRecoveryAttempted;
    private bool replacingGraphicsHost;
    private bool retiringGraphicsHost;
    private bool recoveringGraphicsHost;
    private string? graphicsRecoveryStatus;
    private MainWindowViewModel? graphicsStatusOwner;
    private CadDocumentViewModel? graphicsRecoveryDocument;
    private Exception? graphicsPrimaryFailure;
    internal int AutomaticGraphicsRecoveryAttempts {get;private set;}
    internal int AutomaticGraphicsRecoveryCompleted {get;private set;}
    internal bool IsAutomaticallyRecoveringGraphics=>pendingGraphicsRecovery is not null||recoveringGraphicsHost;

    private void BeginGraphicsAttachment()
    {
        graphicsAttachmentEpoch++;
        if(replacingGraphicsHost)graphicsRecoveryEpoch=graphicsAttachmentEpoch;
        else graphicsRecoveryAttempted=false;
    }

    private void EndGraphicsAttachment()
    {
        graphicsAttachmentEpoch++;
        pendingGraphicsRecovery?.Abort();pendingGraphicsRecovery=null;
        if(!replacingGraphicsHost)FinishGraphicsRecovery(null);
    }

    private void ReportViewportFailure(Exception error,OcctViewportHost? failedHost,OcctViewport? failedViewport)
    {
        if(failedHost is not null&&!ReferenceEquals(failedHost,host))return;
        var vm=document;vm?.Report(error);
        if(retiringGraphicsHost)
        {if(graphicsPrimaryFailure is {} primary)RecordGraphicsRecoveryFailure(primary,error);return;}
        if(vm is null||failedHost is null||!IsLoaded||vm.IsDetached||vm.Session.IsClosing||
            !ReferenceEquals(DataContext,vm)||!ReferenceEquals(host,failedHost)||!ReferenceEquals(failedHost.Viewport,failedViewport))return;
        if(recoveringGraphicsHost&&graphicsRecoveryAttempted)
        {FailGraphicsRecovery(error,failedHost,failedViewport);return;}
        if(failedViewport is null)return;
        if(graphicsRecoveryAttempted||pendingGraphicsRecovery is not null)return;
        try
        {
            if(!failedViewport.TryObserveRecoverableGraphicsFault(error,out var observation))return;
            CadCamera? camera=failedViewport.LastKnownCamera??SavedCamera;
            if(!observation.OwnerDisposed)
            {
                try{camera=failedViewport.CaptureCamera();}
                catch(Exception capture){RecordGraphicsRecoveryFailure(error,capture);}
            }
            long epoch=graphicsAttachmentEpoch;
            pendingGraphicsRecovery=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
            {
                pendingGraphicsRecovery=null;
                if(epoch!=graphicsAttachmentEpoch||!IsLoaded||vm.IsDetached||vm.Session.IsClosing||
                    !ReferenceEquals(document,vm)||!ReferenceEquals(DataContext,vm)||!ReferenceEquals(host,failedHost)||
                    !ReferenceEquals(failedHost.Viewport,failedViewport))return;
                graphicsRecoveryAttempted=true;recoveringGraphicsHost=true;AutomaticGraphicsRecoveryAttempts++;
                graphicsRecoveryDocument=vm;graphicsPrimaryFailure=error;
                SetGraphicsRecoveryStatus();
                replacingGraphicsHost=true;
                try
                {
                    RetireGraphicsHost();
                    if(!IsLoaded||vm.IsDetached||vm.Session.IsClosing||!ReferenceEquals(DataContext,vm))
                    {FinishGraphicsRecovery(null);return;}
                    if(camera is not null)
                    {if(IsSecondary)vm.Review.SecondaryCamera=camera;else vm.Camera=camera;}
                    // Observe readiness before Attach: a synchronous Ready can
                    // complete and stop the timer while Attach is still returning.
                    graphicsRecoveryTimer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromSeconds(30)};
                    graphicsRecoveryTimer.Tick+=OnGraphicsRecoveryDeadline;graphicsRecoveryTimer.Start();
                    Attach();
                    if(host is null){FinishGraphicsRecovery(false);return;}
                }
                catch(Exception rebuilding)
                {
                    RecordGraphicsRecoveryFailure(error,rebuilding);vm.Report(rebuilding);
                    try{RetireGraphicsHost();}catch(Exception cleanup){RecordGraphicsRecoveryFailure(error,cleanup);vm.Report(cleanup);}
                    FinishGraphicsRecovery(false);
                }
                finally{replacingGraphicsHost=false;}
            }));
        }
        catch(Exception observing){RecordGraphicsRecoveryFailure(error,observing);}
    }

    private static void RecordGraphicsRecoveryFailure(Exception primary,Exception additional)
    {
        const string key="Cadoryx.Viewport.GraphicsRecoveryFailures";
        if(primary.Data[key] is not List<Exception> failures){failures=[];primary.Data[key]=failures;}
        if(!ReferenceEquals(primary,additional))failures.Add(additional);
    }

    private void RetireGraphicsHost()
    {
        retiringGraphicsHost=true;
        try{Detach();}
        finally{retiringGraphicsHost=false;}
    }

    private void FailGraphicsRecovery(Exception failure,OcctViewportHost failedHost,OcctViewport? failedViewport)
    {
        var primary=graphicsPrimaryFailure;var vm=graphicsRecoveryDocument;long epoch=graphicsAttachmentEpoch;
        if(primary is not null)RecordGraphicsRecoveryFailure(primary,failure);
        FinishGraphicsRecovery(false);
        // HwndHost can raise Error during BuildWindowCore. Defer disposal until
        // that callback has unwound, and only retire the exact failed host.
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            if(epoch!=graphicsAttachmentEpoch||!ReferenceEquals(host,failedHost)||
                !ReferenceEquals(failedHost.Viewport,failedViewport)||!ReferenceEquals(document,vm))return;
            replacingGraphicsHost=true;
            try{RetireGraphicsHost();}
            catch(Exception cleanup){if(primary is not null)RecordGraphicsRecoveryFailure(primary,cleanup);vm?.Report(cleanup);}
            finally{replacingGraphicsHost=false;}
        }));
    }

    private void OnGraphicsRecoveryDeadline(object? sender,EventArgs e)
    {
        if(!recoveringGraphicsHost||!ReferenceEquals(sender,graphicsRecoveryTimer)||
            graphicsRecoveryEpoch!=graphicsAttachmentEpoch||!ReferenceEquals(document,graphicsRecoveryDocument))return;
        var failure=new TimeoutException(Strings.ResourceManager.GetString("ViewportRecoveryDeadline"));
        document?.Report(failure);
        if(host is {} failedHost)FailGraphicsRecovery(failure,failedHost,failedHost.Viewport);
        else FinishGraphicsRecovery(false);
        // This is a readiness observation, not preemption of a native call.
    }

    private void CompleteGraphicsRecovery(OcctViewport viewport)
    {
        if(!recoveringGraphicsHost||IsSceneLoading||graphicsRecoveryEpoch!=graphicsAttachmentEpoch||!IsLoaded||
            !ReferenceEquals(document,graphicsRecoveryDocument)||!ReferenceEquals(DataContext,graphicsRecoveryDocument)||
            document is not {} vm||vm.IsDetached||vm.Session.IsClosing||!ReferenceEquals(host?.Viewport,viewport))return;
        try
        {
            if(viewport.TryObserveRecoverableGraphicsFault(out var observation))
            {FailGraphicsRecovery(new CadViewportGraphicsFaultException(observation),host!,viewport);return;}
            if(!viewport.HasObservedValidGraphicsContext)return;
            AutomaticGraphicsRecoveryCompleted++;FinishGraphicsRecovery(true);
        }
        catch(Exception failure){vm.Report(failure);FailGraphicsRecovery(failure,host!,viewport);}
    }

    private void SetGraphicsRecoveryStatus()
    {
        if(main is null||!IsActive||!ReferenceEquals(main.ActiveDocument,document))return;
        graphicsStatusOwner=main;
        graphicsRecoveryStatus=Strings.ResourceManager.GetString("ViewportRecovering");
        if(graphicsRecoveryStatus is not null)main.StatusText=graphicsRecoveryStatus;
    }

    private void FinishGraphicsRecovery(bool? succeeded)
    {
        graphicsRecoveryTimer?.Stop();
        if(graphicsRecoveryTimer is not null)graphicsRecoveryTimer.Tick-=OnGraphicsRecoveryDeadline;
        graphicsRecoveryTimer=null;recoveringGraphicsHost=false;
        if(graphicsStatusOwner is {} owner&&graphicsRecoveryStatus is not null&&owner.StatusText==graphicsRecoveryStatus)
            owner.StatusText=succeeded==false&&ReferenceEquals(owner.ActiveDocument,graphicsRecoveryDocument)
                ?Strings.ResourceManager.GetString("ViewportRecoveryFailed")??Strings.Ready:Strings.Ready;
        graphicsRecoveryStatus=null;graphicsStatusOwner=null;graphicsRecoveryDocument=null;graphicsPrimaryFailure=null;
    }
}
