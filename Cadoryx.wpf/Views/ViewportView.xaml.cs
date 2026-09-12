using System.Windows;
using Cadoryx.ViewModels;
using Cadoryx.Rendering;
using Cadoryx.Editor;
using Cadoryx.wpf.Controls;
namespace Cadoryx.wpf.Views;
public partial class ViewportView
{
    private OcctViewportHost? host;
    private CadDocumentViewModel? document;
    public ViewportView()
    {
        InitializeComponent();Loaded+=(_,_)=>Attach();Unloaded+=(_,_)=>Detach();
        DataContextChanged+=(_,_)=>{if(IsLoaded){Detach();Attach();}};
    }
    private void Attach()
    {
        if(host is not null||DataContext is not CadDocumentViewModel vm||vm.IsDetached||vm.Session.IsClosing)return;
        document=vm;host=new(vm.Session.Assets);
        host.Ready+=OnReady;host.Error+=OnError;
        host.Destroying+=OnHostDestroying;
        host.ShortcutPressed+=OnShortcut;
        vm.SceneChanged+=OnScene;vm.PreviewChanged+=OnPreview;vm.FitRequested+=OnFit;
        vm.ProjectionRequested+=OnProjection;vm.DisplayModeRequested+=OnDisplay;vm.Selection.Changed+=OnSelection;
        vm.Detaching+=OnDocumentDetaching;
        HostPanel.Children.Add(host);
    }
    private void OnReady(object? sender,EventArgs e)
    {
        if(host?.Viewport is not {} viewport||document is not {} vm)return;
        Guard(()=>{viewport.SetDisplayMode(vm.CurrentDisplayMode);viewport.SetScene(vm.PreviewScene??vm.Scene);if(vm.Camera is {} c)viewport.RestoreCamera(c);else viewport.FitAll();});
        OnSelection(this,EventArgs.Empty);
        viewport.SelectionChanged+=OnNativeSelection;
    }
    private void Detach()
    {
        if(document is {} vm)
        {
            vm.Detaching-=OnDocumentDetaching;
            vm.SceneChanged-=OnScene;vm.PreviewChanged-=OnPreview;vm.FitRequested-=OnFit;
            vm.ProjectionRequested-=OnProjection;vm.DisplayModeRequested-=OnDisplay;vm.Selection.Changed-=OnSelection;
            if(host?.Viewport is {} viewport){Guard(()=>vm.Camera=viewport.CaptureCamera());viewport.SelectionChanged-=OnNativeSelection;}
        }
        if(host is not null){host.Ready-=OnReady;host.Error-=OnError;host.Destroying-=OnHostDestroying;host.ShortcutPressed-=OnShortcut;HostPanel.Children.Remove(host);host.Dispose();host=null;}
        document=null;
    }
    private void OnDocumentDetaching(object? sender,EventArgs e)=>Detach();
    private void OnHostDestroying(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm&&host?.Viewport is {} viewport)vm.Camera=viewport.CaptureCamera();});
    private void OnScene(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm)host?.Viewport?.SetScene(vm.Scene);});
    private void OnPreview(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm)host?.Viewport?.SetScene(vm.PreviewScene??vm.Scene);});
    private void OnFit(object? sender,EventArgs e)=>Guard(()=>host?.Viewport?.FitAll());
    private void OnProjection(object? sender,CadProjection p)=>Guard(()=>host?.Viewport?.SetProjection(p));
    private void OnDisplay(object? sender,CadDisplayMode mode)=>Guard(()=>host?.Viewport?.SetDisplayMode(mode));
    private void OnNativeSelection(object? sender,IReadOnlyList<SceneItem> items)=>document?.Selection.Replace(items.Select(i=>new SelectionTarget(i.Path,i.BodyId,i.Geometry.Revision)));
    private void OnSelection(object? sender,EventArgs e)=>Guard(()=>{if(document is {} vm)host?.Viewport?.Highlight(vm.Selection.Items.Select(i=>(i.Path,i.BodyId)));});
    private void OnError(object? sender,Exception ex)=>document?.Report(ex);
    private void OnShortcut(object? sender,int key)
    {
        if(key==27){document?.InvalidatePreview();document?.Selection.Replace([]);Guard(()=>host?.Viewport?.ClearSelection());return;}
        if(System.Windows.Application.Current.MainWindow.DataContext is not MainWindowViewModel main)return;
        if(key==83)main.SaveCommand.Execute(null);else if(key==90)main.UndoCommand.Execute(null);else if(key==89)main.RedoCommand.Execute(null);
    }
    private void Guard(Action action){try{action();}catch(Exception ex){document?.Report(ex);}}
}
