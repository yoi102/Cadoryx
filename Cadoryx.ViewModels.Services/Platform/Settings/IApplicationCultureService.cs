namespace Cadoryx.ViewModels.Services.Platform.Settings;

public interface IApplicationCultureService
{
    void ChangeCulture(string language);

    void ChangeCulture(int lcid);

    int GetCurrentCultureLCID();
}
