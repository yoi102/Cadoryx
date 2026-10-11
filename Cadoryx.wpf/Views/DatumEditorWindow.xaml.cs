using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Cadoryx.ViewModels;
using Cadoryx.Db;
using Cadoryx.wpf.Controls;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class DatumEditorWindow:MetroWindow
{
    public DatumEditorViewModel Editor {get;}
    public OcctViewportHost Host {get;}
    private bool closing,closeReady;
    public DatumEditorWindow(DatumEditorViewModel editor)
    {Editor=editor;InitializeComponent();Height=Math.Min(Height,SystemParameters.WorkArea.Height-32);DataContext=editor;Host=new(editor.Assets);ViewportContainer.Child=Host;Host.Ready+=OnReady;Host.Error+=OnError;editor.SceneChanged+=OnScene;editor.CloseRequested+=OnClose;Closing+=OnClosing;}
    private void OnReady(object? sender,EventArgs e){Host.Viewport!.LocalTopologySelected+=OnPick;Refresh();Host.Viewport.FitAll();}
    private async void OnPick(object? sender,Cadoryx.Rendering.Occt.LocalTopologyPick pick)
    {if(Editor.CanPickNative&&pick.Exact is {} selection){await Editor.PickExactAsync(selection);Refresh();}}
    private void OnError(object? sender,Exception e)=>Editor.Status=e.Message;
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    private void Refresh()
    {
        if(Host.Viewport is not {} viewport||Editor.Scene is not {} scene)return;viewport.SetScene(scene);
        if(Editor.NativeSource is {} source)viewport.SetExactSelectionSource(Editor.Snapshot.Id,source.Feature);else viewport.ClearExactSelectionSource();
        viewport.SetBoxSelection(null,Editor.CanPickNative?(Editor.PickEdges?TopologyKind.Edge:TopologyKind.Face):null);viewport.HighlightExactSelections(Editor.NativeSelections);
    }
    private void OnClose(object? sender,EventArgs e){if(!closing)Close();}
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;Editor.CloseRequested-=OnClose;
        Editor.SceneChanged-=OnScene;Host.Dispose();ViewportContainer.Child=null;
        try{await Editor.DisposeAsync();}finally{closeReady=true;_=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(Close));}
    }
}
public sealed class DatumEditorHost:IDatumEditorHost
{
    public Task ShowAsync(DatumEditorViewModel editor)
    {new DatumEditorWindow(editor){Owner=Application.Current.MainWindow}.ShowDialog();return Task.CompletedTask;}
}
