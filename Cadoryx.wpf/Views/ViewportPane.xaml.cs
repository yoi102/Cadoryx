using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using Cadoryx.Db;
using Cadoryx.ViewModels;
using Cadoryx.Rendering;
using Cadoryx.Rendering.Occt;
using OcctSharp;
using Cadoryx.Editor;
using Cadoryx.wpf.Controls;
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
    public ViewportPane()
    {
        InitializeComponent();Loaded+=(_,_)=>Attach();Unloaded+=(_,_)=>Detach();
        DataContextChanged+=(_,_)=>{if(IsLoaded){Detach();Attach();}};
    }
    private void Attach()
    {
        if(host is not null||DataContext is not CadDocumentViewModel vm||vm.IsDetached||vm.Session.IsClosing)return;
        document=vm;host=new(vm.Session.Assets);
        main=System.Windows.Application.Current.MainWindow?.DataContext as MainWindowViewModel;
        if(main is not null)main.PropertyChanged+=OnApplicationSettingsChanged;
        host.Ready+=OnReady;host.Error+=OnError;
        host.Destroying+=OnHostDestroying;
        host.ShortcutPressed+=OnShortcut;
        host.ContextMenuRequested+=OnContextMenu;
        vm.SceneChanged+=OnScene;vm.PreviewChanged+=OnPreview;vm.FitRequested+=OnFit;
        vm.ProjectionRequested+=OnProjection;vm.DisplayModeRequested+=OnDisplay;vm.Selection.Changed+=OnSelection;
        vm.ViewportSettingsChanged+=OnViewportSettings;vm.PropertyChanged+=OnDocumentProperty;
        vm.Detaching+=OnDocumentDetaching;vm.Review.ViewChanged+=OnReview;vm.Review.FocusRequested+=OnFocus;
        HostPanel.Children.Add(host);
    }
    private void OnReady(object? sender,EventArgs e)
    {
        if(host?.Viewport is not {} viewport||document is not {} vm)return;
        Guard(()=>{viewport.SetDisplayMode(vm.CurrentDisplayMode);viewport.SetScene(vm.Review.Filter(vm.PreviewScene??vm.Scene));if(IsSecondary&&SavedCamera is null)viewport.SetProjection(CadProjection.Top);if(SavedCamera is {} c)viewport.RestoreCamera(c);else viewport.FitAll();viewport.SetBackgroundGradient(vm.BackgroundTopArgb,vm.BackgroundBottomArgb);viewport.SetWorkGrid(vm.GridVisible,vm.GridSpacingMm,vm.WorkPlaneSettings);viewport.SetOriginAxes(vm.OriginSettings);viewport.SetConstructionMode(vm.IsViewportConstructing);viewport.SetSection(vm.Review.Section);viewport.SetViewCubeVisible(main?.ApplicationSettings.Viewport.ShowViewCube??true);});
        OnSelection(this,EventArgs.Empty);
        viewport.SelectionChanged+=OnNativeSelection;
        viewport.ConstructionPointer+=OnConstructionPointer;
        viewport.NavigationStarted+=OnNavigationStarted;
        viewport.ViewCubeOrientationRequested+=OnCubeOrientation;
        viewport.ViewCubeTurnRequested+=OnCubeTurn;
    }
    private void Detach()
    {
        ReviewMenu.IsOpen=false;
        ReviewMenu.DataContext=null;
        StopCameraAnimation();
        if(main is not null){main.PropertyChanged-=OnApplicationSettingsChanged;main=null;}
        if(document is {} vm)
        {
            vm.Detaching-=OnDocumentDetaching;vm.Review.ViewChanged-=OnReview;vm.Review.FocusRequested-=OnFocus;
            vm.SceneChanged-=OnScene;vm.PreviewChanged-=OnPreview;vm.FitRequested-=OnFit;
            vm.ProjectionRequested-=OnProjection;vm.DisplayModeRequested-=OnDisplay;vm.Selection.Changed-=OnSelection;
            vm.ViewportSettingsChanged-=OnViewportSettings;vm.PropertyChanged-=OnDocumentProperty;
            if(host?.Viewport is {} viewport){Guard(()=>SavedCamera=viewport.CaptureCamera());viewport.SelectionChanged-=OnNativeSelection;viewport.ConstructionPointer-=OnConstructionPointer;viewport.NavigationStarted-=OnNavigationStarted;viewport.ViewCubeOrientationRequested-=OnCubeOrientation;viewport.ViewCubeTurnRequested-=OnCubeTurn;}
        }
        if(host is not null){host.Ready-=OnReady;host.Error-=OnError;host.Destroying-=OnHostDestroying;host.ShortcutPressed-=OnShortcut;host.ContextMenuRequested-=OnContextMenu;HostPanel.Children.Remove(host);host.Dispose();host=null;}
        document=null;
    }
    private void OnDocumentDetaching(object? sender,EventArgs e)=>Detach();
    private void OnHostDestroying(object? sender,EventArgs e){ReviewMenu.IsOpen=false;StopCameraAnimation();Guard(()=>{if(document is {} vm&&host?.Viewport is {} viewport)SavedCamera=viewport.CaptureCamera();});}
    private void OnScene(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm)host?.Viewport?.SetScene(vm.Review.Filter(vm.Scene));});
    private void OnPreview(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm)host?.Viewport?.SetScene(vm.Review.Filter(vm.PreviewScene??vm.Scene));});
    private void OnFit(object? sender,EventArgs e){if(IsActive)Guard(()=>{if(host?.Viewport is {} viewport)StartCameraAnimation(viewport.CaptureFitTarget());});}
    private void OnReview(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm&&host?.Viewport is {} viewport){viewport.SetScene(vm.Review.Filter(vm.PreviewScene??vm.Scene));viewport.SetSection(vm.Review.Section);}});
    private void OnFocus(object? sender,Bounds3d bounds){if(IsActive)Guard(()=>{if(host?.Viewport is {} viewport)StartCameraAnimation(viewport.CaptureFocusTarget(bounds));});}
    private void ActivatePane(){if(document is {} vm)vm.Review.ActivePane=IsSecondary?1:0;}
    internal ContextMenu ReviewMenu=>(ContextMenu)Resources["ReviewContextMenu"];
    private void OnContextMenu(object? sender,Point screenPoint)
    {
        if(document is null||host is null||!IsLoaded)return;
        ActivatePane();
        var point=HostPanel.PointFromScreen(screenPoint);
        ReviewMenu.DataContext=document.Review;
        ReviewMenu.PlacementTarget=HostPanel;ReviewMenu.Placement=PlacementMode.RelativePoint;
        ReviewMenu.HorizontalOffset=point.X;ReviewMenu.VerticalOffset=point.Y;ReviewMenu.IsOpen=true;
    }
    private void OnProjection(object? sender,CadProjection p){if(IsActive)Guard(()=>StartCameraAnimation(p));}
    private void OnCubeOrientation(object? sender,ViewerCubeOrientation orientation)=>Guard(()=>StartCameraAnimation(host!.Viewport!.CaptureCubeTarget(orientation)));
    private void OnCubeTurn(object? sender,ViewerCubeTurn turn)=>Guard(()=>
        StartCameraAnimation(host!.Viewport!.CaptureCubeTurnTarget(turn,main?.ApplicationSettings.Viewport.ViewCubeRotationDegrees??45)));
    private void OnApplicationSettingsChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(MainWindowViewModel.ApplicationSettings))
            Guard(()=>host?.Viewport?.SetViewCubeVisible(main?.ApplicationSettings.Viewport.ShowViewCube??true));
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
        if(e.PropertyName!=nameof(CadDocumentViewModel.IsViewportConstructing))return;
        if(document?.IsViewportConstructing==true)lastGhostTick=0;
        Guard(()=>host?.Viewport?.SetConstructionMode(document?.IsViewportConstructing==true));
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
            if(update.PreviewNow)await vm.PreviewCommand.ExecuteAsync(null);
        }
        catch(Exception ex){vm.Report(ex);vm.CancelViewportConstruction();}
    }
    private void OnNativeSelection(object? sender,IReadOnlyList<SceneItem> items){ActivatePane();document?.Selection.Replace(items.Select(i=>new SelectionTarget(i.Path,i.BodyId,i.Geometry.Revision)));}
    private void OnSelection(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm)host?.Viewport?.Highlight(vm.Selection.Items.Select(i=>(i.Path,i.BodyId)));});
    private void OnError(object? sender,Exception ex)=>document?.Report(ex);
    private void OnShortcut(object? sender,int key)
    {
        if(key==27){document?.CancelViewportConstruction();document?.Selection.Replace([]);Guard(()=>host?.Viewport?.ClearSelection());return;}
        if(System.Windows.Application.Current.MainWindow.DataContext is not MainWindowViewModel main)return;
        if(key==83)main.SaveCommand.Execute(null);else if(key==90)main.UndoCommand.Execute(null);else if(key==89)main.RedoCommand.Execute(null);
    }
    private void Guard(Action action){try{action();}catch(Exception ex){document?.Report(ex);}}
}

