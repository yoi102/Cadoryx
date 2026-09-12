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
    private void OnReady(object? sender,EventArgs e){Host.Viewport!.BoxSubshapeSelected+=OnPick;Refresh();Host.Viewport.FitAll();}
    private void OnPick(object? sender,(Cadoryx.Db.BoxBoundary First,Cadoryx.Db.BoxBoundary? Second)? value)
    {if(Editor.HasCandidate)return;if(value is {} selected)Editor.Pick(selected.First,selected.Second);else Editor.RejectPick(Strings.AmbiguousSelection);}
    private void OnError(object? sender,Exception e)=>Editor.RejectPick(e.Message);
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    private void Refresh()
    {
        if(Host.Viewport is not {} viewport||Editor.Scene is not {} scene)return;
        viewport.SetScene(scene);viewport.SetBoxSelection(Editor.HasCandidate?null:Editor.Box,Editor.HasCandidate?null:Editor.SelectionKind);
        viewport.HighlightBoxSelection(Editor.HasCandidate?null:Editor.Selection);
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
