using Cadoryx.ViewModels.Settings;
using System.Windows;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Views.Settings;

public partial class ApplicationSettingsWindow
{
    public ApplicationSettingsWindow()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ApplicationSettingsViewModel viewModel && viewModel.TryApply())
            DialogHost.CloseDialogCommand.Execute(bool.TrueString, this);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ApplicationSettingsViewModel viewModel)
            viewModel.TryApply();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ApplicationSettingsViewModel viewModel)
            viewModel.ResetToDefaults();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogHost.CloseDialogCommand.Execute(bool.FalseString, this);
    }
}
