using System.ComponentModel;
using Cadoryx.ViewModels;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class HistoryQueryWindow : MetroWindow
{
    public HistoryQueryViewModel Editor {get;}
    private bool closing,closeReady;
    public HistoryQueryWindow(HistoryQueryViewModel editor)
    {
        Editor=editor;InitializeComponent();DataContext=editor;
        Loaded+=(_,_)=>{if(Editor.SelectedQuery is not null)Editor.AnalyzeCommand.Execute(null);};
        Closing+=OnClosing;
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;
        try{await Editor.DisposeAsync();}
        finally
        {
            closeReady=true;
            _=Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}

public sealed class HistoryQueryHost : IHistoryQueryHost
{
    public Task ShowAsync(HistoryQueryViewModel editor)
    {new HistoryQueryWindow(editor){Owner=System.Windows.Application.Current.MainWindow}.ShowDialog();return Task.CompletedTask;}
}
