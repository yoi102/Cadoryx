namespace Cadoryx.ViewModels.Services.Platform.Settings;

public interface IApplicationSettingsDialogService
{
    void Show(
        CadoryxApplicationSettings settings,
        Action<CadoryxApplicationSettings> applySettings);
}
