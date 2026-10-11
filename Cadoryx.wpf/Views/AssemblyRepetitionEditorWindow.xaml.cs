using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Cadoryx.ViewModels;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class AssemblyRepetitionEditorWindow:MetroWindow
{
    public AssemblyRepetitionEditorViewModel Editor {get;}
    private bool closing,closeReady;
    public AssemblyRepetitionEditorWindow(AssemblyRepetitionEditorViewModel editor)
    {Editor=editor;InitializeComponent();DataContext=editor;editor.CloseRequested+=OnClose;Closing+=OnClosing;}
    private void OnClose(object? sender,EventArgs e){if(!closing)Close();}
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;Editor.CloseRequested-=OnClose;
        try{await Editor.DisposeAsync();}finally{closeReady=true;_=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(Close));}
    }
}
public sealed class AssemblyRepetitionEditorHost:IAssemblyRepetitionEditorHost
{
    public Task ShowAsync(AssemblyRepetitionEditorViewModel editor)
    {new AssemblyRepetitionEditorWindow(editor){Owner=Application.Current.MainWindow}.ShowDialog();return Task.CompletedTask;}
}
