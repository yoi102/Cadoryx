using Cadoryx.ViewModels.Services.Events;
using Cadoryx.ViewModels.Services.Platform.Settings;
using MaterialDesignThemes.Wpf;
using MessagePipe;
using System;
using System.Collections.Generic;
using System.Text;
using MediaColor = System.Windows.Media.Color;

namespace Cadoryx.wpf.Services.Application;

internal sealed class ApplicationThemeService : IApplicationThemeService
{
    private readonly PaletteHelper _paletteHelper = new();
    private readonly IPublisher<ThemeChangedEvent> _publisher;

    public ApplicationThemeService(IPublisher<ThemeChangedEvent> publisher)
    {
        _publisher = publisher;
    }

    public bool IsDarkTheme => CurrentTheme.GetBaseTheme() == BaseTheme.Dark;

    public uint PrimaryColor => ToUintColor(CurrentTheme.PrimaryMid.Color);

    public uint SecondaryColor => ToUintColor(CurrentTheme.SecondaryMid.Color);

    private Theme CurrentTheme => _paletteHelper.GetTheme();

    public void ApplyThemeLightDark(bool isDarkTheme)
    {
        var theme = CurrentTheme;
        SetBaseTheme(theme, isDarkTheme);
        Commit(theme, isDarkTheme);
    }

    public void ApplyThemeColors(uint primaryColor, uint secondaryColor)
    {
        var theme = CurrentTheme;
        theme.SetPrimaryColor(ToMediaColor(primaryColor));
        theme.SetSecondaryColor(ToMediaColor(secondaryColor));
        Commit(theme, theme.GetBaseTheme() == BaseTheme.Dark);
    }

    public void ApplyTheme(bool isDarkTheme, uint primaryColor, uint secondaryColor)
    {
        var theme = CurrentTheme;
        SetBaseTheme(theme, isDarkTheme);
        theme.SetPrimaryColor(ToMediaColor(primaryColor));
        theme.SetSecondaryColor(ToMediaColor(secondaryColor));
        Commit(theme, isDarkTheme);
    }

    public void ToggleThemeLightDark()
    {
        ApplyThemeLightDark(!IsDarkTheme);
    }

    private void Commit(Theme theme, bool isDarkTheme)
    {
        _paletteHelper.SetTheme(theme);
        _publisher.Publish(new ThemeChangedEvent(isDarkTheme));
    }

    private static void SetBaseTheme(Theme theme, bool isDarkTheme)
    {
        if (isDarkTheme)
            theme.SetDarkTheme();
        else
            theme.SetLightTheme();
    }

    private static MediaColor ToMediaColor(uint color) =>
    MediaColor.FromArgb(
        (byte)(color >> 24),
        (byte)(color >> 16),
        (byte)(color >> 8),
        (byte)color);

    private static uint ToUintColor(MediaColor color) =>
    ((uint)color.A << 24) |
    ((uint)color.R << 16) |
    ((uint)color.G << 8) |
    color.B;
}