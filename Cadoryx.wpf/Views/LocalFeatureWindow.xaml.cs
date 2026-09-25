using System.ComponentModel;
using System.Windows;
using Cadoryx.ViewModels;
using Strings = Cadoryx.Lang.Strings.Strings;
using Cadoryx.wpf.Controls;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class LocalFeatureWindow:MetroWindow
{
    public LocalFeatureViewModel Editor {get;}
    public OcctViewportHost Host {get;}
    private bool closing,closeReady;
    public LocalFeatureWindow(LocalFeatureViewModel editor)
    {
        Editor=editor;InitializeComponent();DataContext=editor;Host=new(editor.Assets);ViewportContainer.Child=Host;
        Host.Ready+=OnReady;Host.Error+=OnError;Editor.SceneChanged+=OnScene;Editor.CloseRequested+=OnClose;Closing+=OnClosing;
    }
    private void OnReady(object? sender,EventArgs e)
    {
        Host.Viewport!.BoxSubshapeSelected+=OnPick;
        Host.Viewport.LocalTopologySelected+=OnLocalPick;
        Refresh();Host.Viewport.FitAll();
    }
    private void OnPick(object? sender,(Cadoryx.Db.BoxBoundary First,Cadoryx.Db.BoxBoundary? Second)? value)
    {if(Editor.HasCandidate)return;if(value is {} selected)Editor.Pick(selected.First,selected.Second);else Editor.RejectPick(Strings.AmbiguousSelection);}
    private void OnLocalPick(object? sender,Cadoryx.Rendering.Occt.LocalTopologyPick value)
    {
        if(Editor.HasCandidate)return;
        if(Editor.SelectionKind==Cadoryx.Db.TopologyKind.Face&&Editor.IsChamfer)
        {
            if(value.Exact is {} face)Editor.PickExact(face);else Editor.RejectPick(Strings.AmbiguousSelection);
        }
        else if(Editor.IsReselecting&&value.Exact is {} reselected)Editor.PickExact(reselected);
        else if(value.Box is {} semantic)Editor.Pick(semantic.First,semantic.Second);
        else if(value.Exact is {} exact)Editor.PickExact(exact);
        else Editor.RejectPick(Strings.AmbiguousSelection);
    }
    private void OnError(object? sender,Exception e)=>Editor.RejectPick(e.Message);
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    private void Refresh()
    {
        if(Host.Viewport is not {} viewport||Editor.Scene is not {} scene)return;
        viewport.SetScene(scene);
        if(Editor.ReselectSourceFeatureId is {} source)viewport.SetExactSelectionSource(Editor.Snapshot.Id,source);
        else if(Editor.IsChainedSource&&Editor.SelectedBox is {} selected)viewport.SetExactSelectionSource(Editor.Snapshot.Id,selected.FeatureId);
        else viewport.ClearExactSelectionSource();
        viewport.SetBoxSelection(Editor.HasCandidate?null:Editor.Box,Editor.HasCandidate?null:Editor.SelectionKind);
        viewport.HighlightBoxSelections(Editor.HasCandidate?[]:Editor.HighlightEdges());
        if(!Editor.HasCandidate)viewport.HighlightExactSelections(new[]{Editor.ExactEdge,Editor.SupportFace}.OfType<Cadoryx.Db.ExactTopologySelection>());
    }
    private void OnFit(object sender,RoutedEventArgs e)=>Host.Viewport?.FitAll();
    private void OnClose(object? sender,EventArgs e)=>Close();
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;
        Editor.SceneChanged-=OnScene;Editor.CloseRequested-=OnClose;
        Host.Dispose();ViewportContainer.Child=null;
        try{await Editor.DisposeAsync();}finally{closeReady=true;Close();}
    }
}
public sealed class LocalFeatureHost:ILocalFeatureHost
{
    public Task ShowAsync(LocalFeatureViewModel editor)
    {new LocalFeatureWindow(editor){Owner=System.Windows.Application.Current.MainWindow}.ShowDialog();return Task.CompletedTask;}
}
