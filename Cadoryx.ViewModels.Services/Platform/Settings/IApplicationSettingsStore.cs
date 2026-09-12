namespace Cadoryx.ViewModels.Services.Platform.Settings;

public interface IApplicationSettingsStore
{
    CadoryxApplicationSettings Load();

    void Save(CadoryxApplicationSettings settings);
}
