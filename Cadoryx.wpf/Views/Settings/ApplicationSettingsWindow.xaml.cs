using Cadoryx.ViewModels.Settings;
using System.Windows;

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
            DialogResult = true;
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
        DialogResult = false;
    }
}
