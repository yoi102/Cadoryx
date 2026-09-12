namespace Cadoryx.ViewModels.Services.Platform.Settings;

public sealed class CadoryxApplicationSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public CadoryxGeneralSettings General { get; set; } = new();
    public CadoryxViewportSettings Viewport { get; set; } = new();
    public CadoryxInteractionSettings Interaction { get; set; } = new();

    public static CadoryxApplicationSettings CreateDefault() => new();

    public void Normalize()
    {
        Version = CurrentVersion;
        General ??= new CadoryxGeneralSettings();
        Viewport ??= new CadoryxViewportSettings();
        Interaction ??= new CadoryxInteractionSettings();
        General.Normalize();
        Viewport.Normalize();
        Interaction.Normalize();
    }

    public CadoryxApplicationSettings Clone()
    {
        var clone = CreateDefault();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(CadoryxApplicationSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Normalize();

        Version = CurrentVersion;
        General = new CadoryxGeneralSettings
        {
            IsDarkTheme = source.General.IsDarkTheme,
            CultureLcid = source.General.CultureLcid,
            PrimaryColor = source.General.PrimaryColor,
            SecondaryColor = source.General.SecondaryColor
        };
        Viewport = new CadoryxViewportSettings
        {
            Projection = source.Viewport.Projection,
            BackgroundColor = source.Viewport.BackgroundColor,
            ShowAxes = source.Viewport.ShowAxes,
            ShowViewCube = source.Viewport.ShowViewCube,
            ShowFramesPerSecond = source.Viewport.ShowFramesPerSecond,
            IsAntialiasingEnabled = source.Viewport.IsAntialiasingEnabled
        };
        Interaction = new CadoryxInteractionSettings
        {
            SelectionMode = source.Interaction.SelectionMode,
            EnablePreselection = source.Interaction.EnablePreselection,
            InvertOrbitVerticalAxis = source.Interaction.InvertOrbitVerticalAxis,
            InvertZoom = source.Interaction.InvertZoom,
            OrbitSensitivity = source.Interaction.OrbitSensitivity
        };
        Normalize();
    }
}

public sealed class CadoryxGeneralSettings
{
    public bool IsDarkTheme { get; set; } = true;
    public int CultureLcid { get; set; } = 1033;
    public uint PrimaryColor { get; set; } = 0xFF3F51B5;
    public uint SecondaryColor { get; set; } = 0xFF03A9F4;

    internal void Normalize()
    {
        if (CultureLcid is not (1033 or 1041 or 2052))
            CultureLcid = 1033;

        PrimaryColor = NormalizeColor(PrimaryColor, 0xFF3F51B5);
        SecondaryColor = NormalizeColor(SecondaryColor, 0xFF03A9F4);
    }

    private static uint NormalizeColor(uint color, uint fallback) =>
        color == 0 ? fallback : color | 0xFF000000;
}

public enum CadoryxProjection
{
    Isometric,
    Orthographic,
    Perspective
}

public sealed class CadoryxViewportSettings
{
    public CadoryxProjection Projection { get; set; } = CadoryxProjection.Isometric;
    public uint BackgroundColor { get; set; } = 0xFF20242A;
    public bool ShowAxes { get; set; } = true;
    public bool ShowViewCube { get; set; } = true;
    public bool ShowFramesPerSecond { get; set; }
    public bool IsAntialiasingEnabled { get; set; } = true;

    internal void Normalize()
    {
        if (!Enum.IsDefined(Projection))
            Projection = CadoryxProjection.Isometric;
        BackgroundColor = BackgroundColor == 0 ? 0xFF20242A : BackgroundColor | 0xFF000000;
    }
}

public enum CadoryxSelectionMode
{
    Single,
    Multiple,
    Window
}

public sealed class CadoryxInteractionSettings
{
    public CadoryxSelectionMode SelectionMode { get; set; } = CadoryxSelectionMode.Single;
    public bool EnablePreselection { get; set; } = true;
    public bool InvertOrbitVerticalAxis { get; set; }
    public bool InvertZoom { get; set; }
    public double OrbitSensitivity { get; set; } = 1.0;

    internal void Normalize()
    {
        if (!Enum.IsDefined(SelectionMode))
            SelectionMode = CadoryxSelectionMode.Single;
        if (double.IsNaN(OrbitSensitivity) || double.IsInfinity(OrbitSensitivity))
            OrbitSensitivity = 1.0;
        OrbitSensitivity = Math.Clamp(OrbitSensitivity, 0.1, 5.0);
    }
}
