using System.ComponentModel;
using System.Windows;
using Cadoryx.Db;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using MahApps.Metro.Controls;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.wpf.Views;

public partial class FaceFeatureEditorWindow:MetroWindow
{
    public FaceFeatureEditorViewModel Editor {get;}
    public OcctViewportHost Host {get;}
    private bool closing,closeReady;
    public FaceFeatureEditorWindow(FaceFeatureEditorViewModel editor)
    {
        Editor=editor;InitializeComponent();Height=Math.Min(Height,SystemParameters.WorkArea.Height-32);DataContext=editor;Host=new(editor.Assets);ViewportContainer.Child=Host;
        Host.Ready+=OnReady;Host.Error+=OnError;Editor.SceneChanged+=OnScene;Editor.CloseRequested+=OnClose;Closing+=OnClosing;
    }
    private void OnReady(object? sender,EventArgs e)
    {Host.Viewport!.LocalTopologySelected+=OnPick;Refresh();Host.Viewport.FitAll();}
    private async void OnPick(object? sender,Cadoryx.Rendering.Occt.LocalTopologyPick pick)
    {if(Editor.HasCandidate||Editor.IsOffset)return;if(pick.Exact is {} face)await Editor.PickExactAsync(face);else Editor.RejectPick(Strings.AmbiguousSelection);}
    private void OnError(object? sender,Exception e)=>Editor.RejectPick(e.Message);
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    private void Refresh()
    {
        if(Host.Viewport is not {} viewport||Editor.Scene is not {} scene)return;
        viewport.SetScene(scene);
        if(!Editor.IsOffset&&!Editor.HasCandidate&&Editor.SelectedSource is {} source)viewport.SetExactSelectionSource(Editor.Snapshot.Id,source.Feature);
        else viewport.ClearExactSelectionSource();
        viewport.SetBoxSelection(null,Editor.HasCandidate||Editor.IsOffset?null:TopologyKind.Face);
        viewport.HighlightExactSelections(Editor.HasCandidate?[]:Editor.Faces.Where(f=>f.Asset==Editor.Snapshot.Features[Editor.SelectedSource!.Feature].Result.AssetId));
    }
    private void OnFit(object sender,RoutedEventArgs e)=>Host.Viewport?.FitAll();
    private void OnClose(object? sender,EventArgs e)=>Close();
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;
        Editor.SceneChanged-=OnScene;Editor.CloseRequested-=OnClose;Host.Dispose();ViewportContainer.Child=null;
        try{await Editor.DisposeAsync();}
        finally{closeReady=true;_=Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(Close));}
    }
}
public sealed class FaceFeatureEditorHost:IFaceFeatureEditorHost
{
    public Task ShowAsync(FaceFeatureEditorViewModel editor)
    {new FaceFeatureEditorWindow(editor){Owner=Application.Current.MainWindow}.ShowDialog();return Task.CompletedTask;}
}
