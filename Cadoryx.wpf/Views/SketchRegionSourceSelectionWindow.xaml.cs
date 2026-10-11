using System.ComponentModel;
using System.Windows;
using Cadoryx.ViewModels;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class SketchRegionSourceSelectionWindow:MetroWindow
{
    private bool closing,closeReady;
    public SketchRegionSourceSelectionViewModel Editor {get;}
    public SketchRegionSourceSelectionWindow(SketchRegionSourceSelectionViewModel editor)
    {
        Editor=editor;InitializeComponent();DataContext=editor;editor.CloseRequested+=OnClose;Closing+=OnClosing;
        editor.PropertyChanged+=OnEditorChanged;
        Loaded+=(_,_)=>Canvas.Fit();
    }
    private void OnEditorChanged(object? sender,PropertyChangedEventArgs e)
    {if(!closing&&e.PropertyName==nameof(Editor.PreviewEditor))_=Dispatcher.BeginInvoke(new Action(Canvas.Fit));}
    private void OnFit(object sender,RoutedEventArgs e)=>Canvas.Fit();
    private void OnClose(object? sender,EventArgs e){if(!closing)Close();}
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;Editor.CloseRequested-=OnClose;Editor.PropertyChanged-=OnEditorChanged;
        try{await Editor.DisposeAsync();}finally{closeReady=true;_=Dispatcher.BeginInvoke(new Action(Close));}
    }
}
public sealed class SketchRegionSourceSelectionHost(Window owner):ISketchRegionSourceSelectionHost
{
    public Task ShowAsync(SketchRegionSourceSelectionViewModel editor)
    {new SketchRegionSourceSelectionWindow(editor){Owner=owner}.ShowDialog();return Task.CompletedTask;}
}
