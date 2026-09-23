using System.Windows;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Settings;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views.Settings;

public partial class DocumentSettingsWindow : MetroWindow
{
    public DocumentSettingsViewModel Editor {get;}
    private bool applying;

    public DocumentSettingsWindow(DocumentSettingsViewModel editor)
    {
        Editor=editor;
        InitializeComponent();
        DataContext=editor;
    }

    private async void Ok_Click(object sender,RoutedEventArgs e)
    {
        if(applying)return;
        applying=true;
        try {if(await Editor.TryApplyAsync())Close();}
        finally {applying=false;}
    }

    private async void Apply_Click(object sender,RoutedEventArgs e)
    {
        if(applying)return;
        applying=true;
        try {await Editor.TryApplyAsync();}
        finally {applying=false;}
    }

    private void Reset_Click(object sender,RoutedEventArgs e)=>Editor.ResetToDefaults();
    private void Cancel_Click(object sender,RoutedEventArgs e)=>Close();
}

public sealed class DocumentSettingsHost : IDocumentSettingsHost
{
    public Task ShowAsync(CadDocumentViewModel document)
    {
        new DocumentSettingsWindow(new DocumentSettingsViewModel(document))
        { Owner=Application.Current.MainWindow }.ShowDialog();
        return Task.CompletedTask;
    }
}
