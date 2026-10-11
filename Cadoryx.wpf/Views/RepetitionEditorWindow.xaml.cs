using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Cadoryx.ViewModels;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class RepetitionEditorWindow:MetroWindow
{
    public RepetitionEditorViewModel Editor {get;}
    private bool closing,closeReady;
    public RepetitionEditorWindow(RepetitionEditorViewModel editor)
    {Editor=editor;InitializeComponent();DataContext=editor;editor.CloseRequested+=OnClose;Closing+=OnClosing;}
    private void OnClose(object? sender,EventArgs e){if(!closing)Close();}
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;Editor.CloseRequested-=OnClose;
        try{await Editor.DisposeAsync();}finally{closeReady=true;_=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(Close));}
    }
}
public sealed class RepetitionEditorHost:IRepetitionEditorHost
{
    public Task ShowAsync(RepetitionEditorViewModel editor)
    {new RepetitionEditorWindow(editor){Owner=Application.Current.MainWindow}.ShowDialog();return Task.CompletedTask;}
}
