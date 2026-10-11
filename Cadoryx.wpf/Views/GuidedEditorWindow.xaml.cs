using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Cadoryx.ViewModels;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class GuidedEditorWindow:MetroWindow
{
    public GuidedEditorViewModel Editor {get;}
    private bool closing,closeReady;
    public GuidedEditorWindow(GuidedEditorViewModel editor)
    {Editor=editor;InitializeComponent();Height=Math.Min(Height,SystemParameters.WorkArea.Height-32);DataContext=editor;editor.CloseRequested+=OnClose;Closing+=OnClosing;}
    private void OnClose(object? sender,EventArgs e){if(!closing)Close();}
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;Editor.CloseRequested-=OnClose;
        try{await Editor.DisposeAsync();}finally{closeReady=true;_=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(Close));}
    }
}
public sealed class GuidedEditorHost:IGuidedEditorHost
{
    public Task ShowAsync(GuidedEditorViewModel editor)
    {new GuidedEditorWindow(editor){Owner=Application.Current.MainWindow}.ShowDialog();return Task.CompletedTask;}
}
