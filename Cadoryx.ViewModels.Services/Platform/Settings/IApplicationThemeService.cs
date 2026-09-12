using System;
using System.Collections.Generic;
using System.Text;

namespace Cadoryx.ViewModels.Services.Platform.Settings;

public interface IApplicationThemeService
{
    bool IsDarkTheme { get; }
    uint PrimaryColor { get; }
    uint SecondaryColor { get; }

    void ToggleThemeLightDark();

    void ApplyThemeLightDark(bool isDarkTheme);

    void ApplyThemeColors(uint primaryColor, uint secondaryColor);

    void ApplyTheme(bool isDarkTheme, uint primaryColor, uint secondaryColor);
}
