using System.ComponentModel;
using System.Windows;
using Cadoryx.Db;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using MahApps.Metro.Controls;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.wpf.Views;

public partial class SketchNativeProjectionWindow:MetroWindow
{
    public SketchNativeProjectionPickerViewModel Editor {get;}
    public OcctViewportHost Host {get;}
    private bool closing,closeReady;
    public SketchNativeProjectionWindow(SketchNativeProjectionPickerViewModel editor)
    {
        Editor=editor;InitializeComponent();Height=Math.Min(Height,SystemParameters.WorkArea.Height-32);DataContext=editor;Host=new(editor.Assets);ViewportContainer.Child=Host;
        Host.Ready+=OnReady;Host.Error+=OnError;Editor.SceneChanged+=OnScene;Editor.CloseRequested+=OnClose;Closing+=OnClosing;
    }
    private void OnReady(object? sender,EventArgs e)
    {Host.Viewport!.LocalTopologySelected+=OnPick;Refresh();Host.Viewport.FitAll();}
    private async void OnPick(object? sender,Cadoryx.Rendering.Occt.LocalTopologyPick pick)
    {if(pick.Exact is {} edge)await Editor.PickExactAsync(edge);else Editor.RejectPick(Strings.AmbiguousSelection);}
    private void OnError(object? sender,Exception error)=>Editor.RejectPick(error.Message);
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    private void Refresh()
    {
        if(Host.Viewport is not {} viewport)return;viewport.SetScene(Editor.Scene);
        if(Editor.SelectedSource is {} source)viewport.SetExactSelectionSource(Editor.Snapshot.Id,source.Feature);else viewport.ClearExactSelectionSource();
        viewport.SetBoxSelection(null,TopologyKind.Edge);viewport.HighlightExactSelections(Editor.SelectedEdge is {} selected?[selected]:[]);
    }
    private void OnFit(object sender,RoutedEventArgs e)=>Host.Viewport?.FitAll();
    private void OnClose(object? sender,EventArgs e){if(!closing)Close();}
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;
        Editor.SceneChanged-=OnScene;Editor.CloseRequested-=OnClose;Host.Dispose();ViewportContainer.Child=null;
        try{await Editor.DisposeAsync();}
        finally{closeReady=true;_=Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(Close));}
    }
}
public sealed class SketchNativeProjectionHost(Window owner):ISketchNativeProjectionHost
{
    public Task ShowAsync(SketchNativeProjectionPickerViewModel picker)
    {new SketchNativeProjectionWindow(picker){Owner=owner}.ShowDialog();return Task.CompletedTask;}
}
