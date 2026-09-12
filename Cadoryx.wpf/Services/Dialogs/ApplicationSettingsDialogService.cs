using System.Windows;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using Cadoryx.wpf.Views.Settings;

namespace Cadoryx.wpf.Services.Dialogs;

internal sealed class ApplicationSettingsDialogService : IApplicationSettingsDialogService
{
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly DialogService _dialogs;

    public ApplicationSettingsDialogService(IApplicationSettingsStore settingsStore, DialogService dialogs)
    {
        _settingsStore = settingsStore;
        _dialogs = dialogs;
    }

    public void Show(
        CadoryxApplicationSettings settings,
        Action<CadoryxApplicationSettings> applySettings)
    {
        var dialog = new ApplicationSettingsWindow
        {
            DataContext = new ApplicationSettingsViewModel(settings, _settingsStore, applySettings)
        };
        _ = _dialogs.ShowDialogAsync(dialog);
    }
}
