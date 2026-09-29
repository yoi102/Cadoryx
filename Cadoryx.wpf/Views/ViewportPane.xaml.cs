using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.ViewModels;
using Cadoryx.Rendering;
using Cadoryx.Rendering.Occt;
using OcctSharp;
using Cadoryx.Editor;
using Cadoryx.Lang.Strings;
using Cadoryx.wpf.Controls;
using Cadoryx.ViewModels.Services.Platform.Settings;
namespace Cadoryx.wpf.Views;
public partial class ViewportPane
{
    public bool IsSecondary {get;set;}
    private bool IsActive=>document?.Review.ActivePane==(IsSecondary?1:0);
    private CadCamera? SavedCamera {get=>IsSecondary?document?.Review.SecondaryCamera:document?.Camera;set {if(document is {} vm){if(IsSecondary)vm.Review.SecondaryCamera=value;else vm.Camera=value;}}}
    private OcctViewportHost? host;
    private CadDocumentViewModel? document;
    private long lastGhostTick;
    private DispatcherTimer? cameraTimer;
    private Stopwatch? cameraClock;
    private CadCamera? cameraFrom,cameraTo;
    private OcctViewport? cameraViewport;
    private MainWindowViewModel? main;
    private readonly CadoryxRadialMenuPopup radialMenu = new();
    private CadoryxRadialPage radialPage;
    private bool radialWheelOverride;
    private int radialWheelAccumulated;
    private int radialWheelEmitted;
    private int radialWheelDirection;
    private long radialWheelLastTick;
    internal int RadialPageNumber=>radialMenu.Page+1;
    internal bool IsRadialPopupOpen=>radialMenu.IsOpen;
    internal CadoryxRadialAction RadialSelectedAction=>radialMenu.SelectedAction;
    internal CadoryxRadialAction LastRadialCompletedAction {get;private set;}
    internal CadoryxRadialAction LastRadialExecutedAction {get;private set;}
    internal Point RadialCenterScreen=>radialMenu.ScreenCenter;
    internal FrameworkElement RadialVisual=>radialMenu.Visual;
    internal nint RadialPopupHandle=>radialMenu.WindowHandle;
    private HwndSource? radialWindowSource;
    private bool radialThreadHookAttached;
    private int sceneLoadVersion;
    private string? sceneLoadStatus;
    internal static event Action<ProgressiveSceneTiming>? SceneLoadTimed;
    private DocumentStateId? occurrenceHandleState;
    public ViewportPane()
    {
        radialMenu.Wheel+=OnRadialWheel;
        InitializeComponent();Loaded+=(_,_)=>Attach();Unloaded+=(_,_)=>Detach();
        DataContextChanged+=(_,_)=>{if(IsLoaded){Detach();Attach();}};
    }
    private void Attach()
    {
        if(host is not null||DataContext is not CadDocumentViewModel vm||vm.IsDetached||vm.Session.IsClosing)return;
        document=vm;host=new(vm.Session.Assets);
        main=System.Windows.Application.Current.MainWindow?.DataContext as MainWindowViewModel;
        host.RadialMenuEnabled=main?.ApplicationSettings.RadialMenu.IsEnabled??false;
        if(main is not null)main.PropertyChanged+=OnApplicationSettingsChanged;
        host.Ready+=OnReady;host.Error+=OnError;
        host.Destroying+=OnHostDestroying;
        host.ShortcutPressed+=OnShortcut;
        host.ContextMenuRequested+=OnContextMenu;
        host.RadialStarted+=OnRadialStarted;host.RadialMoved+=OnRadialMoved;
        host.RadialModifiersChanged+=OnRadialModifiersChanged;
        host.RadialCompleted+=OnRadialCompleted;host.RadialCancelled+=OnRadialCancelled;
        host.RadialWheel+=OnRadialWheel;
        vm.SceneChanged+=OnScene;vm.PreviewChanged+=OnPreview;vm.FitRequested+=OnFit;
        vm.ProjectionRequested+=OnProjection;vm.DisplayModeRequested+=OnDisplay;vm.Selection.Changed+=OnSelection;
        vm.ViewportSettingsChanged+=OnViewportSettings;vm.PropertyChanged+=OnDocumentProperty;
        vm.Detaching+=OnDocumentDetaching;vm.Review.ViewChanged+=OnReview;vm.Review.FocusRequested+=OnFocus;
        vm.AssemblyConstraints.DatumPickRequested+=OnDatumPickRequested;
        vm.DrawingDatumPickRequested+=OnDatumPickRequested;
        vm.Review.CaptureCamerasRequested+=OnCaptureCameras;vm.Review.RestoreCamerasRequested+=OnRestoreCameras;
        HostPanel.Children.Add(host);
    }
    private void OnReady(object? sender,EventArgs e)
    {
        if(host?.Viewport is not {} viewport||document is not {} vm)return;
        AttachRadialWindowHook();
        Guard(()=>{viewport.SetDisplayMode(vm.CurrentDisplayMode);if(IsSecondary&&SavedCamera is null)viewport.SetProjection(CadProjection.Top);if(SavedCamera is {} c)viewport.RestoreCamera(c);viewport.SetBackgroundGradient(vm.BackgroundTopArgb,vm.BackgroundBottomArgb);viewport.SetWorkGrid(vm.GridVisible,vm.GridSpacingMm,vm.WorkPlaneSettings);viewport.SetOriginAxes(vm.OriginSettings);viewport.SetConstructionMode(vm.IsViewportConstructing);viewport.SetSection(vm.Review.Section);viewport.SetViewCubeAppearance(ViewCubeAppearance());viewport.SetViewCubeVisible(main?.ApplicationSettings.Viewport.ShowViewCube??true);ShowScene(vm.Review.Filter(vm.PreviewScene??vm.Scene),SavedCamera is null);});
        OnSelection(this,EventArgs.Empty);
        viewport.SelectionChanged+=OnNativeSelection;
        viewport.AssemblyDatumSelected+=OnAssemblyDatumSelected;
        viewport.AssemblyDatumPickRejected+=OnAssemblyDatumPickRejected;
        viewport.ConstructionPointer+=OnConstructionPointer;
        viewport.NavigationStarted+=OnNavigationStarted;
        viewport.ViewCubeOrientationRequested+=OnCubeOrientation;
        viewport.ViewCubeTurnRequested+=OnCubeTurn;
        viewport.SolidHandleChanged+=OnSolidHandleChanged;
        viewport.SolidHandleFinished+=OnSolidHandleFinished;
        viewport.OccurrenceGizmoFinished+=OnOccurrenceGizmoFinished;
        RefreshSolidHandles();
        RefreshOccurrenceHandles();
    }
    private void Detach()
    {
        CancelSceneLoad();
        DetachRadialWindowHook();
        ReviewMenu.IsOpen=false;
        radialMenu.Close();
        ReviewMenu.DataContext=null;
        StopCameraAnimation();
        if(main is not null){main.PropertyChanged-=OnApplicationSettingsChanged;main=null;}
        if(document is {} vm)
        {
            vm.Detaching-=OnDocumentDetaching;vm.Review.ViewChanged-=OnReview;vm.Review.FocusRequested-=OnFocus;
            vm.AssemblyConstraints.DatumPickRequested-=OnDatumPickRequested;
            vm.DrawingDatumPickRequested-=OnDatumPickRequested;
            vm.Review.CaptureCamerasRequested-=OnCaptureCameras;vm.Review.RestoreCamerasRequested-=OnRestoreCameras;
            vm.SceneChanged-=OnScene;vm.PreviewChanged-=OnPreview;vm.FitRequested-=OnFit;
            vm.ProjectionRequested-=OnProjection;vm.DisplayModeRequested-=OnDisplay;vm.Selection.Changed-=OnSelection;
            vm.ViewportSettingsChanged-=OnViewportSettings;vm.PropertyChanged-=OnDocumentProperty;
            if(host?.Viewport is {} viewport){Guard(()=>SavedCamera=viewport.CaptureCamera());viewport.SelectionChanged-=OnNativeSelection;viewport.AssemblyDatumSelected-=OnAssemblyDatumSelected;viewport.AssemblyDatumPickRejected-=OnAssemblyDatumPickRejected;viewport.ConstructionPointer-=OnConstructionPointer;viewport.NavigationStarted-=OnNavigationStarted;viewport.ViewCubeOrientationRequested-=OnCubeOrientation;viewport.ViewCubeTurnRequested-=OnCubeTurn;viewport.SolidHandleChanged-=OnSolidHandleChanged;viewport.SolidHandleFinished-=OnSolidHandleFinished;viewport.OccurrenceGizmoFinished-=OnOccurrenceGizmoFinished;}
        }
        if(host is not null){host.Ready-=OnReady;host.Error-=OnError;host.Destroying-=OnHostDestroying;host.ShortcutPressed-=OnShortcut;host.ContextMenuRequested-=OnContextMenu;host.RadialStarted-=OnRadialStarted;host.RadialMoved-=OnRadialMoved;host.RadialModifiersChanged-=OnRadialModifiersChanged;host.RadialCompleted-=OnRadialCompleted;host.RadialCancelled-=OnRadialCancelled;host.RadialWheel-=OnRadialWheel;HostPanel.Children.Remove(host);host.Dispose();host=null;}
        document=null;
    }
    private void OnDocumentDetaching(object? sender,EventArgs e)=>Detach();
    private void OnCaptureCameras(object? sender,EventArgs e)=>Guard(()=>{if(host?.Viewport is {} v)SavedCamera=v.CaptureCamera();});
    private void OnRestoreCameras(object? sender,EventArgs e)=>Guard(()=>{if(host?.Viewport is {} v&&SavedCamera is {} c){StopCameraAnimation();v.RestoreCamera(c);v.Redraw();}});
    private void OnHostDestroying(object? sender,EventArgs e){DetachRadialWindowHook();ReviewMenu.IsOpen=false;radialMenu.Close();StopCameraAnimation();CancelSceneLoad();Guard(()=>{if(document is {} vm&&host?.Viewport is {} viewport)SavedCamera=viewport.CaptureCamera();});}
    private void OnScene(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm){ShowScene(vm.Review.Filter(vm.Scene));RefreshSolidHandles();RefreshOccurrenceHandles();}});
    private void OnPreview(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm){ShowScene(vm.Review.Filter(vm.PreviewScene??vm.Scene));RefreshSolidHandles();RefreshOccurrenceHandles();}});
    private void OnFit(object? sender,EventArgs e){if(IsActive)Guard(()=>{if(host?.Viewport is {} viewport)StartCameraAnimation(viewport.CaptureFitTarget());});}
    private void OnReview(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm&&host?.Viewport is {} viewport){ShowScene(vm.Review.Filter(vm.PreviewScene??vm.Scene));viewport.SetSection(vm.Review.Section);RefreshOccurrenceHandles();}});
    private void ShowScene(CadScene scene,bool fit=false)
    {
        CancelSceneLoad();
        if(host?.Viewport is not {} viewport)return;
        if(scene.Items.Length<300||viewport.VisibleBodyCount!=0)
        {viewport.SetScene(scene);if(fit)viewport.FitAll();return;}
        viewport.BeginProgressiveScene(scene);
        if(fit)viewport.RestoreCamera(SceneEnvelope.FitVisible(viewport.CaptureCamera(),scene.Items));
        int version=sceneLoadVersion;
        _=ContinueSceneLoadAsync(viewport,scene,version);
    }
    internal bool IsSceneLoading=>sceneLoadStatus is not null;
    internal void ShowSceneForSmoke(CadScene scene)=>ShowScene(scene);
    private async Task ContinueSceneLoadAsync(OcctViewport viewport,CadScene scene,int version)
    {
        try
        {
            int count=0,batches=0,redraws=0,statusUpdates=1;
            var redrawClock=Stopwatch.StartNew();var statusClock=Stopwatch.StartNew();var sceneClock=Stopwatch.StartNew();double firstVisibleMs=0;
            SetSceneLoadStatus(0,scene.Items.Length);
            await Dispatcher.Yield(DispatcherPriority.Background);
            while(count<scene.Items.Length)
            {
                if(version!=sceneLoadVersion||!ReferenceEquals(host?.Viewport,viewport))return;
                bool redraw=count==0||redrawClock.ElapsedMilliseconds>=1200;
                count=viewport.AppendProgressiveScene(64,TimeSpan.FromMilliseconds(50),redraw);
                batches++;if(redraw)redraws++;
                if(firstVisibleMs==0&&count>0)firstVisibleMs=sceneClock.Elapsed.TotalMilliseconds;
                if(redraw)redrawClock.Restart();
                if(statusClock.ElapsedMilliseconds>=250||count==scene.Items.Length)
                {SetSceneLoadStatus(count,scene.Items.Length);statusUpdates++;statusClock.Restart();}
                // Background priority allows paint, pointer navigation and cancellation between native batches.
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            if(version!=sceneLoadVersion||!ReferenceEquals(host?.Viewport,viewport))return;
            viewport.CompleteProgressiveScene();
            SceneLoadTimed?.Invoke(new(scene.DocumentId,IsSecondary,scene.Items.Length,firstVisibleMs,
                sceneClock.Elapsed.TotalMilliseconds,batches,redraws,statusUpdates));
            ClearSceneLoadStatus();RefreshOccurrenceHandles();
        }
        catch(Exception ex)
        {
            if(version!=sceneLoadVersion)return;
            try{viewport.AbortProgressiveScene();}catch(Exception cleanup){document?.Report(cleanup);}
            ClearSceneLoadStatus();document?.Report(ex);
        }
    }
    private void SetSceneLoadStatus(int count,int total)
    {
        if(main is null)return;
        var format=Strings.ResourceManager.GetString("ViewportLoadingFormat")??"Loading view {0}/{1}…";
        main.StatusText=sceneLoadStatus=string.Format(format,count,total);
    }
    private void ClearSceneLoadStatus()
    {
        if(main is not null&&main.StatusText==sceneLoadStatus)main.StatusText=Strings.Ready;
        sceneLoadStatus=null;
    }
    private void CancelSceneLoad()
    {
        sceneLoadVersion++;
        host?.Viewport?.AbortProgressiveScene();
        ClearSceneLoadStatus();
    }
    private void OnFocus(object? sender,Bounds3d bounds){if(IsActive)Guard(()=>{if(host?.Viewport is {} viewport)StartCameraAnimation(viewport.CaptureFocusTarget(bounds));});}
    private void ActivatePane(){if(document is {} vm)vm.Review.ActivePane=IsSecondary?1:0;}
    internal ContextMenu ReviewMenu=>(ContextMenu)Resources["ReviewContextMenu"];
    private void OnContextMenu(object? sender,Point screenPoint)
    {
        if(document is null||host is null||!IsLoaded)return;
        ActivatePane();
        var point=HostPanel.PointFromScreen(screenPoint);
        ReviewMenu.DataContext=document.Review;
        foreach(var menu in ReviewMenu.Items.OfType<MenuItem>())
        {
            if(menu.Tag is not string key||!key.StartsWith("Gizmo",StringComparison.Ordinal))continue;
            menu.Header=Strings.ResourceManager.GetString(key,Strings.Culture)??key;
            menu.IsChecked=host.Viewport?.GizmoMode==(key switch
            {
                "GizmoRotate"=>OccurrenceGizmoMode.Rotate,
                "GizmoScale"=>OccurrenceGizmoMode.Scale,
                _=>OccurrenceGizmoMode.Move
            });
            menu.IsEnabled=key!="GizmoScale"||host.Viewport?.CanScaleOccurrence==true;
        }
        ReviewMenu.PlacementTarget=HostPanel;ReviewMenu.Placement=PlacementMode.RelativePoint;
        ReviewMenu.HorizontalOffset=point.X;ReviewMenu.VerticalOffset=point.Y;ReviewMenu.IsOpen=true;
    }
    private void OnGizmoModeClicked(object sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem {Tag:string key}||host?.Viewport is not {} viewport)return;
        viewport.SetOccurrenceGizmoMode(key switch
        {
            "GizmoRotate"=>OccurrenceGizmoMode.Rotate,
            "GizmoScale"=>OccurrenceGizmoMode.Scale,
            _=>OccurrenceGizmoMode.Move
        });
    }
    private static CadoryxRadialPage PageFor(int modifiers) =>
        (modifiers&1)!=0 ? CadoryxRadialPage.Shift : (modifiers&2)!=0 ? CadoryxRadialPage.Control :
        (modifiers&4)!=0 ? CadoryxRadialPage.Alt : CadoryxRadialPage.Middle;
    private void AttachRadialWindowHook()
    {
        if(!radialThreadHookAttached)
        {
            ComponentDispatcher.ThreadPreprocessMessage+=OnRadialThreadMessage;
            radialThreadHookAttached=true;
        }
        if(radialWindowSource is not null)return;
        radialWindowSource=PresentationSource.FromVisual(HostPanel) as HwndSource;
        radialWindowSource?.AddHook(OnRadialWindowMessage);
    }
    private void DetachRadialWindowHook()
    {
        if(radialThreadHookAttached)
        {
            ComponentDispatcher.ThreadPreprocessMessage-=OnRadialThreadMessage;
            radialThreadHookAttached=false;
        }
        radialWindowSource?.RemoveHook(OnRadialWindowMessage);
        radialWindowSource=null;
    }
    private void OnRadialThreadMessage(ref MSG message,ref bool handled)
    {
        // Physical wheel input is queued and can target a floating dock window,
        // the popup, or the native child. Consume it before HWND dispatch so the
        // same message cannot be applied again by one of their window hooks.
        if(handled||message.message!=0x20A||host?.IsRadialActive!=true||!radialMenu.IsOpen)return;
        OnRadialWheel(this,unchecked((short)(((long)message.wParam>>16)&0xFFFF)));
        handled=true;
    }
    private nint OnRadialWindowMessage(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(host is null)return 0;
        if((message is 0x100 or 0x101 or 0x104 or 0x105 or 0x290 or 0x291)&&
            OcctViewportHost.IsRadialTrigger((int)w,l)&&
            (host.IsRadialActive||IsActive&&ReferenceEquals(main?.ActiveDocument,document)))
        {
            if((message is 0x100 or 0x104 or 0x290)&&
                Keyboard.FocusedElement is (TextBoxBase or PasswordBox or ComboBox{IsEditable:true}))return 0;
            handled=host.HandleRadialKey((uint)message,(int)w,l);
            return 0;
        }
        if(!host.IsRadialActive)return 0;
        if(message==0x20A) // WM_MOUSEWHEEL can target the WPF owner instead of either child HWND.
        {OnRadialWheel(this,unchecked((short)(((long)w>>16)&0xFFFF)));handled=true;}
        else if((message is 0x104 or 0x105)&&((int)w is 0x12 or 0xA4 or 0xA5))
        {host.NotifyRadialKey((uint)message,(int)w);handled=true;}
        else if(message==0x112&&((long)w&0xFFF0)==0xF100) // SC_KEYMENU
            handled=true;
        return 0;
    }
    private void OnRadialStarted(object? sender,RadialPointerEventArgs e)
    {
        if(document is null||host is null||main is null||!IsLoaded)return;
        ActivatePane();ReviewMenu.IsOpen=false;
        LastRadialCompletedAction=CadoryxRadialAction.None;
        LastRadialExecutedAction=CadoryxRadialAction.None;
        radialWheelOverride=false;radialWheelAccumulated=0;radialWheelEmitted=0;radialWheelDirection=0;radialWheelLastTick=0;
        radialPage=PageFor(e.Modifiers);
        radialMenu.Show(HostPanel,HostPanel.PointFromScreen(e.ScreenPoint),main.ApplicationSettings.RadialMenu.Get(radialPage),(int)radialPage);
        var actualCursor=host.WarpRadialCursor(radialMenu.ScreenCenter);
        if((actualCursor-radialMenu.ScreenCenter).Length>2)
            radialMenu.CenterAtScreen(actualCursor);
    }
    private void OnRadialMoved(object? sender,RadialPointerEventArgs e)
    {
        if(host?.IsRadialActive!=true)return;
        // Pointer motion only changes the selected sector. Page changes come
        // exclusively from wheel or modifier-key events.
        radialMenu.Update(e.ScreenPoint);
    }
    private void OnRadialModifiersChanged(object? sender,int modifiers)
    {
        if(host?.IsRadialActive==true&&!radialWheelOverride)
            SetRadialPage(PageFor(modifiers));
    }
    private void SetRadialPage(CadoryxRadialPage page)
    {
        if(main is null||radialPage==page)return;
        radialPage=page;
        radialMenu.SetPage((int)page,main.ApplicationSettings.RadialMenu.Get(page));
    }
    private void OnRadialWheel(object? sender,int delta)
    {
        if(main is null||host?.IsRadialActive!=true||delta==0)return;
        const int wheelDelta=120;
        const long gesturePauseMs=500;
        int direction=Math.Sign(delta);
        long now=Environment.TickCount64;
        bool newGesture=direction!=radialWheelDirection||now-radialWheelLastTick>gesturePauseMs;
        if(newGesture){radialWheelAccumulated=0;radialWheelEmitted=0;radialWheelDirection=direction;}
        radialWheelLastTick=now;
        radialWheelAccumulated+=Math.Abs(delta);
        int targetSteps=Math.Max(1,radialWheelAccumulated/wheelDelta);
        int steps=targetSteps-radialWheelEmitted;
        if(steps==0)return;
        radialWheelEmitted=targetSteps;
        radialWheelOverride=true;
        SetRadialPage((CadoryxRadialPage)(((int)radialPage+4-direction*(steps%4))%4));
    }
    private void OnRadialCompleted(object? sender,RadialPointerEventArgs e)
    {
        radialMenu.Update(e.ScreenPoint);
        var action=radialMenu.Complete(e.ScreenPoint);
        LastRadialCompletedAction=action;
        if(action==CadoryxRadialAction.None)return;
        var currentDocument=document;
        Dispatcher.BeginInvoke(()=>{if(host is not null&&ReferenceEquals(document,currentDocument))ExecuteRadialAction(action);});
    }
    private void OnRadialCancelled(object? sender,EventArgs e)=>radialMenu.Close();
    private void ExecuteRadialAction(CadoryxRadialAction action)
    {
        if(main is null||document is null||!ReferenceEquals(main.ActiveDocument,document))return;
        LastRadialExecutedAction=action;
        static void Run(ICommand command,object? parameter=null){if(command.CanExecute(parameter))command.Execute(parameter);}
        switch(action)
        {
            case CadoryxRadialAction.Fit: Run(main.FitViewCommand);break;
            case CadoryxRadialAction.FocusSelection: Run(document.Review.FocusCommand);break;
            case CadoryxRadialAction.Top: Run(main.SetViewCommand,"Top");break;
            case CadoryxRadialAction.Front: Run(main.SetViewCommand,"Front");break;
            case CadoryxRadialAction.Right: Run(main.SetViewCommand,"Right");break;
            case CadoryxRadialAction.Axonometric: Run(main.SetViewCommand,"Axonometric");break;
            case CadoryxRadialAction.Undo: Run(main.UndoCommand);break;
            case CadoryxRadialAction.Redo: Run(main.RedoCommand);break;
            case CadoryxRadialAction.Save: Run(main.SaveCommand);break;
            case CadoryxRadialAction.Isolate: Run(document.Review.IsolateCommand);break;
            case CadoryxRadialAction.Hide: Run(document.Review.HideCommand);break;
            case CadoryxRadialAction.ShowAll: Run(document.Review.ShowAllCommand);break;
            case CadoryxRadialAction.NewSketch: Run(main.NewSketchCommand);break;
            case CadoryxRadialAction.Box: Run(main.StartToolCommand,"Box");break;
            case CadoryxRadialAction.Cylinder: Run(main.StartToolCommand,"Cylinder");break;
        }
    }
    private void OnProjection(object? sender,CadProjection p){if(IsActive)Guard(()=>StartCameraAnimation(p));}
    private void OnDatumPickRequested(object? sender,TopologyKind? kind)
    {if(IsActive)Guard(()=>{host?.Viewport?.SetAssemblyDatumSelection(kind);RefreshOccurrenceHandles();});}
    private async void OnAssemblyDatumSelected(object? sender,AssemblyDatumPick pick)
    {
        if(document is not {} vm)return;
        host?.Viewport?.SetAssemblyDatumSelection(null);
        Guard(RefreshOccurrenceHandles);
        if(vm.IsDrawingDatumPickPending)
            await vm.ReceiveDrawingDatumPickAsync(pick.Path,pick.Body,pick.FullTopologyIndex,pick.Fingerprint);
        else await vm.AssemblyConstraints.ReceiveDatumPickAsync(pick.Path,pick.Body,pick.FullTopologyIndex,pick.Fingerprint);
    }
    private void OnAssemblyDatumPickRejected(object? sender,string message)
    {
        if(document?.IsDrawingDatumPickPending==true)document.RejectDrawingDatumPick(message);
        else document?.AssemblyConstraints.ReportDatumPickFailure(message);
    }
    private void OnCubeOrientation(object? sender,ViewerCubeOrientation orientation)=>Guard(()=>StartCameraAnimation(host!.Viewport!.CaptureCubeTarget(orientation)));
    private void OnCubeTurn(object? sender,ViewerCubeTurn turn)=>Guard(()=>
        StartCameraAnimation(host!.Viewport!.CaptureCubeTurnTarget(turn,main?.ApplicationSettings.Viewport.ViewCubeRotationDegrees??45)));
    private void OnApplicationSettingsChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(MainWindowViewModel.ApplicationSettings))
            Guard(()=>{host?.Viewport?.SetViewCubeVisible(main?.ApplicationSettings.Viewport.ShowViewCube??true);if(host is not null)host.RadialMenuEnabled=main?.ApplicationSettings.RadialMenu.IsEnabled??true;radialMenu.Close();});
        else if(e.PropertyName==nameof(MainWindowViewModel.CurrentCultureLCID))
            Guard(()=>host?.Viewport?.SetViewCubeAppearance(ViewCubeAppearance()));
    }
    private ViewerCubeAppearance ViewCubeAppearance()
    {
        var culture=CultureInfo.GetCultureInfo(main?.CurrentCultureLCID??CultureInfo.CurrentUICulture.LCID);
        string Label(string side)=>Strings.ResourceManager.GetString("ViewCube"+side,culture)??side;
        string font=culture.TwoLetterISOLanguageName switch{"zh"=>"Microsoft YaHei","ja"=>"Yu Gothic UI",_=>"Segoe UI"};
        return new(Label("Front"),Label("Back"),Label("Left"),Label("Right"),
            Label("Top"),Label("Bottom"),font,18,0.55);
    }
    private void OnNavigationStarted(object? sender,EventArgs e){ActivatePane();StopCameraAnimation();}
    private void StartCameraAnimation(CadProjection projection)
    {
        if(host?.Viewport is not {} viewport)return;
        var to=viewport.CaptureProjectionTarget(projection);
        StartCameraAnimation(to);
    }
    private void StartCameraAnimation(CadCamera to)
    {
        StopCameraAnimation();
        if(host?.Viewport is not {} viewport)return;
        var from=viewport.CaptureCamera();
        if(from==to)
        {viewport.RestoreCamera(to);viewport.Redraw();return;}
        cameraFrom=from;cameraTo=to;cameraViewport=viewport;cameraClock=Stopwatch.StartNew();
        cameraTimer=new DispatcherTimer(DispatcherPriority.Render,Dispatcher){Interval=TimeSpan.FromMilliseconds(16)};
        cameraTimer.Tick+=OnCameraFrame;cameraTimer.Start();
    }
    private void OnCameraFrame(object? sender,EventArgs e)
    {
        if(cameraViewport is not {} viewport||!ReferenceEquals(host?.Viewport,viewport)||cameraFrom is not {} from||cameraTo is not {} to||cameraClock is not {} clock)
        {StopCameraAnimation();return;}
        try
        {
            double t=Math.Clamp(clock.Elapsed.TotalMilliseconds/320,0,1);
            if(t>=1){viewport.RestoreCamera(to);viewport.Redraw();StopCameraAnimation();return;}
            double eased=t*t*(3-2*t);
            viewport.RestoreCamera(CadCameraAnimation.Interpolate(from,to,eased));viewport.Redraw();
        }
        catch(Exception ex){StopCameraAnimation();document?.Report(ex);}
    }
    private void StopCameraAnimation()
    {
        if(cameraTimer is {} timer){timer.Stop();timer.Tick-=OnCameraFrame;cameraTimer=null;}
        cameraClock=null;cameraFrom=null;cameraTo=null;cameraViewport=null;
    }
    private void OnDisplay(object? sender,CadDisplayMode mode)=>Guard(()=>host?.Viewport?.SetDisplayMode(mode));
    private void OnViewportSettings(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm&&host?.Viewport is {} viewport){viewport.SetBackgroundGradient(vm.BackgroundTopArgb,vm.BackgroundBottomArgb);viewport.SetWorkGrid(vm.GridVisible,vm.GridSpacingMm,vm.WorkPlaneSettings);viewport.SetOriginAxes(vm.OriginSettings);}});
    private void OnDocumentProperty(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(CadDocumentViewModel.EditingFeature) or nameof(CadDocumentViewModel.IsWorking))
            Guard(()=>{RefreshSolidHandles();RefreshOccurrenceHandles();});
        if(e.PropertyName!=nameof(CadDocumentViewModel.IsViewportConstructing))return;
        if(document?.IsViewportConstructing==true)lastGhostTick=0;
        Guard(()=>host?.Viewport?.SetConstructionMode(document?.IsViewportConstructing==true));
        Guard(RefreshSolidHandles);
        Guard(RefreshOccurrenceHandles);
    }
    private async void OnConstructionPointer(object? sender,(int X,int Y,bool Click) sample)
    {
        if(document is not {} vm||host?.Viewport is not {} viewport)return;
        try
        {
            bool hit=viewport.TryWorkplanePoint(sample.X,sample.Y,vm.GridSpacingMm,vm.SnapToGrid,vm.WorkPlaneSettings,out var point);
            if(!hit&&vm.ToolKind is "Box" or "Cylinder")
            {vm.ToolStatus=Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("ViewportPlaneUnavailable")??"Switch to a top or axonometric view.";return;}
            if(!hit)point=vm.WorkPlaneSettings.Origin;
            var update=vm.ConstructionPointer(point,sample.X,sample.Y,viewport.PixelsPerMillimeter(point,vm.WorkPlaneSettings),sample.Click);
            if(sample.Click||Environment.TickCount64-lastGhostTick>=40)
            {
                viewport.SetConstructionGhost(update.Ghost);
                // A click starts the next construction stage; its first move must not inherit this frame's throttle.
                lastGhostTick=sample.Click?0:Environment.TickCount64;
            }
            if(update.PreviewNow)
            {
                // The third viewport click completes a primitive. Keep the isolated preview
                // calculation, then publish it through the normal undoable document command.
                bool finishPrimitive=vm.ToolKind is "Box" or "Cylinder";
                await vm.PreviewCommand.ExecuteAsync(null);
                if(finishPrimitive&&vm.HasPreview)await vm.ConfirmCommand.ExecuteAsync(null);
            }
        }
        catch(Exception ex){vm.Report(ex);vm.CancelViewportConstruction();}
    }
    private void OnNativeSelection(object? sender,IReadOnlyList<SceneItem> items)
    {
        ActivatePane();
        if(document is not {} vm||vm.PreviewScene is not null)return;
        var snapshot=vm.Session.Snapshot;
        vm.Selection.Replace(items.Where(item=>item.Path.DocumentId==snapshot.Id&&
            snapshot.Bodies.TryGetValue(item.BodyId,out var body)&&
            body.Geometry.Revision==item.Geometry.Revision)
            .Select(item=>new SelectionTarget(item.Path,item.BodyId,item.Geometry.Revision)));
    }
    private void OnSelection(object? sender,EventArgs e)=>Guard(()=>
    {
        if(document is not {} vm)return;
        var targets=vm.Selection.Items.Select(i=>(i.Path,i.BodyId));
        if(vm.Selection.Items.IsEmpty&&vm.Selection.Occurrence is {} path)
            targets=vm.Scene.Items.Where(i=>i.Path.DocumentId==path.DocumentId&&
                i.Path.Slots.Length>=path.Slots.Length&&
                i.Path.Slots.Take(path.Slots.Length).SequenceEqual(path.Slots))
                .Select(i=>(i.Path,i.BodyId));
        host?.Viewport?.Highlight(targets);
        RefreshSolidHandles();
        RefreshOccurrenceHandles();
    });
    private SceneItem? SolidHandleItem()
    {
        if(document is not {} vm||vm.EditingFeature is not {} id||
            !vm.Session.Snapshot.Features.TryGetValue(id,out var feature))return null;
        var items=vm.Review.Filter(vm.Scene).Items.Where(i=>i.BodyId==feature.OutputBodyId).ToArray();
        return items.Length==1?items[0]:items.FirstOrDefault(i=>vm.Selection.Items.Any(s=>s.Path==i.Path&&s.BodyId==i.BodyId));
    }
    private void RefreshSolidHandles()
    {
        if(host?.Viewport is not {} viewport||document is not {} vm)return;
        var item=SolidHandleItem();
        viewport.SetSolidHandles(item is null?null:vm.SolidHandleRecipe(),item?.WorldTransform??RigidTransform3d.Identity);
    }
    private void RefreshOccurrenceHandles()
    {
        if(host?.Viewport is not {} viewport||document is not {} vm)return;
        occurrenceHandleState=null;
        OccurrencePath? path=null;
        var selected=vm.Selection.Occurrence;
        if(selected is not null&&!vm.IsReadOnly&&!vm.IsClosingRequested&&!vm.Session.IsClosing&&
            !vm.IsWorking&&!vm.IsViewportConstructing&&vm.EditingFeature is null&&vm.PreviewScene is null&&
            vm.Selection.Items.All(i=>i.Path.Equals(selected)))
        {
            var snapshot=vm.Session.Snapshot;
            try
            {
                if(OccurrenceDrag.CanMove(snapshot,selected)&&
                   vm.Review.Filter(vm.Scene).Items.Any(i=>OccurrenceDrag.Contains(selected,i.Path)))
                {path=selected;occurrenceHandleState=snapshot.StateId;}
            }
            catch(CadValidationException){}
        }
        BodyId? focusBody=vm.Selection.Items.FirstOrDefault(i=>i.Path.Equals(path))?.BodyId;
        bool canScale=focusBody is {} bodyId&&vm.Scene.Items.Count(i=>i.BodyId==bodyId)==1&&
            vm.Session.Snapshot.Bodies.TryGetValue(bodyId,out var body)&&
            body.Producer is {} producer&&vm.Session.Snapshot.Features.TryGetValue(producer,out var feature)&&
            feature.Recipe is BoxRecipe or CylinderRecipe;
        viewport.SetOccurrenceHandles(path,focusBody,canScale);
    }
    private async void OnOccurrenceGizmoFinished(object? sender,
        (OccurrencePath Path,BodyId? FocusBody,ViewerManipulatorMode Mode,double[] WorldMatrix,bool Commit) result)
    {
        if(!result.Commit||document is not {} vm||host?.Viewport is not {} viewport)return;
        try
        {
            var snapshot=vm.Session.Snapshot;
            if(snapshot.StateId!=occurrenceHandleState||vm.IsReadOnly||vm.IsWorking||vm.IsClosingRequested||
                !OccurrenceDrag.CanMove(snapshot,result.Path))
                throw new CadValidationException("Document changed during manipulation; select the instance again.");
            var (delta,scale)=OccurrenceGizmoMath.Decompose(result.WorldMatrix);
            if(result.Mode==ViewerManipulatorMode.Scaling)
            {
                if(result.FocusBody is not {} bodyId||
                    vm.Selection.Items.All(i=>i.Path!=result.Path||i.BodyId!=bodyId)||
                    vm.Scene.Items.Count(i=>i.BodyId==bodyId)!=1||
                    !snapshot.Bodies.TryGetValue(bodyId,out var body)||body.Producer is not {} producer||
                    !snapshot.Features.TryGetValue(producer,out var feature))
                    throw new CadValidationException("Scaling requires one unique Box or Cylinder feature.");
                if(Math.Abs(scale-1)<1e-5){viewport.SetScene(vm.Review.Filter(vm.Scene));RefreshOccurrenceHandles();return;}
                GeometryRecipe next=OccurrenceGizmoMath.ScalePrimitive(feature.Recipe,scale);
                await vm.Session.ExecuteAsync(new RecomputeCommand(producer,next));
            }
            else if(result.Mode is ViewerManipulatorMode.Translation or ViewerManipulatorMode.TranslationPlane or ViewerManipulatorMode.Rotation)
            {
                if(Math.Abs(scale-1)>1e-3)throw new CadValidationException("Unexpected scaling in placement transform.");
                var resolved=OccurrencePlacement.Resolve(snapshot,result.Path);
                var occurrence=snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(result.Path));
                var parentWorld=occurrence.WorldTransform*resolved.Slot.LocalTransform.Inverse();
                var next=parentWorld.Inverse()*(delta*occurrence.WorldTransform);
                if(vm.SnapToGrid&&result.Mode!=ViewerManipulatorMode.Rotation)
                {
                    var t=next.Translation;double spacing=vm.GridSpacingMm;
                    next=next with{Translation=new(Math.Round(t.X/spacing)*spacing,
                        Math.Round(t.Y/spacing)*spacing,Math.Round(t.Z/spacing)*spacing)};
                }
                if(next!=resolved.Slot.LocalTransform)
                    await vm.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(result.Path,next));
                else {viewport.SetScene(vm.Review.Filter(vm.Scene));RefreshOccurrenceHandles();}
            }
        }
        catch(Exception ex){vm.Report(ex);viewport.SetScene(vm.Review.Filter(vm.Scene));RefreshOccurrenceHandles();}
    }
    private void OnSolidHandleChanged(object? sender,(SolidDimension Dimension,double Value) change)
    {
        if(document is not {} vm)return;
        Guard(()=>
        {
            vm.SetSolidHandleValue(change.Dimension,vm.SnapToGrid&&change.Dimension!=SolidDimension.RevolveAngle?
                Math.Max(0.001,Math.Round(change.Value/vm.GridSpacingMm)*vm.GridSpacingMm):change.Value);
            if(host?.Viewport is {} viewport&&SolidHandleItem() is {} item)
            {
                viewport.SetConstructionGhost(vm.SolidHandleRecipe(),item.WorldTransform);
                viewport.UpdateSolidHandlePositions(vm.SolidHandleRecipe(),item.WorldTransform);
            }
        });
    }
    private async void OnSolidHandleFinished(object? sender,(SolidDimension Dimension,double Initial,bool Commit) result)
    {
        if(document is not {} vm||host?.Viewport is not {} viewport)return;
        try
        {
            viewport.SetConstructionGhost(null);
            if(!result.Commit){vm.SetSolidHandleValue(result.Dimension,result.Initial);return;}
            if(vm.SolidHandleRecipe() is not null)await vm.PreviewCommand.ExecuteAsync(null);
        }
        catch(Exception ex){vm.Report(ex);}
    }
    private void OnError(object? sender,Exception ex)=>document?.Report(ex);
    private void OnShortcut(object? sender,int key)
    {
        if(key==27){document?.CancelViewportConstruction();document?.Selection.Replace([]);Guard(()=>host?.Viewport?.ClearSelection());return;}
        if(System.Windows.Application.Current.MainWindow.DataContext is not MainWindowViewModel main)return;
        if(key==83)main.SaveCommand.Execute(null);else if(key==90)main.UndoCommand.Execute(null);else if(key==89)main.RedoCommand.Execute(null);
    }
    private void Guard(Action action){try{action();}catch(Exception ex){document?.Report(ex);}}
}

internal sealed record ProgressiveSceneTiming(DocumentId DocumentId,bool IsSecondary,int VisibleInstances,
    double FirstVisibleMs,double CompleteMs,int Batches,int Redraws,int StatusUpdates);

