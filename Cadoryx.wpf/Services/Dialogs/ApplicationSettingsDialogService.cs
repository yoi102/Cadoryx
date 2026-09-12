using System.Windows;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using Cadoryx.wpf.Views.Settings;

namespace Cadoryx.wpf.Services.Dialogs;

internal sealed class ApplicationSettingsDialogService : IApplicationSettingsDialogService
{
    private readonly IApplicationSettingsStore _settingsStore;

    public ApplicationSettingsDialogService(IApplicationSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public void Show(
        CadoryxApplicationSettings settings,
        Action<CadoryxApplicationSettings> applySettings)
    {
        var dialog = new ApplicationSettingsWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow,
            DataContext = new ApplicationSettingsViewModel(settings, _settingsStore, applySettings)
        };
        dialog.ShowDialog();
    }
}
