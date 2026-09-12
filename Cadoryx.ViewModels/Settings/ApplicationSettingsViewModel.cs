using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadoryx.ViewModels.Settings;

public partial class ApplicationSettingsViewModel : ObservableObject
{
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly Action<CadoryxApplicationSettings> _applySettings;

    public ApplicationSettingsViewModel(
        CadoryxApplicationSettings settings,
        IApplicationSettingsStore settingsStore,
        Action<CadoryxApplicationSettings> applySettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));

        var workingCopy = settings.Clone();
        General = new GeneralApplicationSettingsViewModel(workingCopy.General);
        Viewport = new ViewportApplicationSettingsViewModel(workingCopy.Viewport);
        Interaction = new InteractionApplicationSettingsViewModel(workingCopy.Interaction);
        Sections = [General, Viewport, Interaction];
        SelectedSection = Sections[0];
    }

    public GeneralApplicationSettingsViewModel General { get; }
    public ViewportApplicationSettingsViewModel Viewport { get; }
    public InteractionApplicationSettingsViewModel Interaction { get; }
    public IReadOnlyList<ApplicationSettingsSectionViewModel> Sections { get; }

    [ObservableProperty]
    public partial ApplicationSettingsSectionViewModel SelectedSection { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; private set; }

    public bool TryApply()
    {
        var settings = CadoryxApplicationSettings.CreateDefault();
        foreach (var section in Sections)
        {
            if (section.TryApplyTo(settings))
                continue;

            SelectedSection = section;
            ValidationError = Strings.InvalidApplicationSettings;
            return false;
        }

        settings.Normalize();
        try
        {
            _settingsStore.Save(settings);
            _applySettings(settings);
            ValidationError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ValidationError = ex.Message;
            return false;
        }
    }

    public void ResetToDefaults()
    {
        foreach (var section in Sections)
            section.ResetToDefaults();

        ValidationError = null;
    }
}

public abstract class ApplicationSettingsSectionViewModel : ObservableObject
{
    protected ApplicationSettingsSectionViewModel(string title) => Title = title;

    public string Title { get; }

    internal abstract bool TryApplyTo(CadoryxApplicationSettings settings);

    internal abstract void ResetToDefaults();
}

public sealed record ApplicationCultureOption(int Lcid, string DisplayName);

public partial class GeneralApplicationSettingsViewModel : ApplicationSettingsSectionViewModel
{
    public GeneralApplicationSettingsViewModel(CadoryxGeneralSettings settings)
        : base(Strings.General)
    {
        CultureOptions =
        [
            new(1033, Strings.English),
            new(1041, Strings.Japanese),
            new(2052, Strings.Chinese)
        ];
        Load(settings);
    }

    public IReadOnlyList<ApplicationCultureOption> CultureOptions { get; }

    [ObservableProperty] public partial bool IsDarkTheme { get; set; }
    [ObservableProperty] public partial uint PrimaryColor { get; set; } = 0xFF3F51B5;
    [ObservableProperty] public partial uint SecondaryColor { get; set; } = 0xFF03A9F4;
    [ObservableProperty] public partial ApplicationCultureOption? SelectedCulture { get; set; }

    internal override bool TryApplyTo(CadoryxApplicationSettings settings)
    {
        if (SelectedCulture is null)
            return false;

        settings.General.IsDarkTheme = IsDarkTheme;
        settings.General.CultureLcid = SelectedCulture.Lcid;
        settings.General.PrimaryColor = PrimaryColor;
        settings.General.SecondaryColor = SecondaryColor;
        return true;
    }

    internal override void ResetToDefaults() => Load(new CadoryxGeneralSettings());

    private void Load(CadoryxGeneralSettings settings)
    {
        IsDarkTheme = settings.IsDarkTheme;
        PrimaryColor = settings.PrimaryColor;
        SecondaryColor = settings.SecondaryColor;
        SelectedCulture = CultureOptions.FirstOrDefault(option => option.Lcid == settings.CultureLcid)
                          ?? CultureOptions[0];
    }

}

public sealed record ProjectionOption(CadoryxProjection Projection, string DisplayName);

public partial class ViewportApplicationSettingsViewModel : ApplicationSettingsSectionViewModel
{
    public ViewportApplicationSettingsViewModel(CadoryxViewportSettings settings)
        : base(Strings.Viewport)
    {
        ProjectionOptions =
        [
            new(CadoryxProjection.Isometric, Strings.Axonometric),
            new(CadoryxProjection.Orthographic, Strings.Orthographic),
            new(CadoryxProjection.Perspective, Strings.Perspective)
        ];
        Load(settings);
    }

    public IReadOnlyList<ProjectionOption> ProjectionOptions { get; }

    [ObservableProperty] public partial ProjectionOption? SelectedProjection { get; set; }
    [ObservableProperty] public partial uint BackgroundColor { get; set; } = 0xFF20242A;
    [ObservableProperty] public partial bool ShowAxes { get; set; }
    [ObservableProperty] public partial bool ShowViewCube { get; set; }
    [ObservableProperty] public partial bool ShowFramesPerSecond { get; set; }
    [ObservableProperty] public partial bool IsAntialiasingEnabled { get; set; }

    internal override bool TryApplyTo(CadoryxApplicationSettings settings)
    {
        if (SelectedProjection is null)
            return false;

        settings.Viewport.Projection = SelectedProjection.Projection;
        settings.Viewport.BackgroundColor = BackgroundColor;
        settings.Viewport.ShowAxes = ShowAxes;
        settings.Viewport.ShowViewCube = ShowViewCube;
        settings.Viewport.ShowFramesPerSecond = ShowFramesPerSecond;
        settings.Viewport.IsAntialiasingEnabled = IsAntialiasingEnabled;
        return true;
    }

    internal override void ResetToDefaults() => Load(new CadoryxViewportSettings());

    private void Load(CadoryxViewportSettings settings)
    {
        SelectedProjection = ProjectionOptions.First(option => option.Projection == settings.Projection);
        BackgroundColor = settings.BackgroundColor;
        ShowAxes = settings.ShowAxes;
        ShowViewCube = settings.ShowViewCube;
        ShowFramesPerSecond = settings.ShowFramesPerSecond;
        IsAntialiasingEnabled = settings.IsAntialiasingEnabled;
    }
}

public sealed record SelectionModeOption(CadoryxSelectionMode Mode, string DisplayName);

public partial class InteractionApplicationSettingsViewModel : ApplicationSettingsSectionViewModel
{
    public InteractionApplicationSettingsViewModel(CadoryxInteractionSettings settings)
        : base(Strings.Interaction)
    {
        SelectionModeOptions =
        [
            new(CadoryxSelectionMode.Single, Strings.SingleSelection),
            new(CadoryxSelectionMode.Multiple, Strings.MultipleSelection),
            new(CadoryxSelectionMode.Window, Strings.WindowSelection)
        ];
        Load(settings);
    }

    public IReadOnlyList<SelectionModeOption> SelectionModeOptions { get; }

    [ObservableProperty] public partial SelectionModeOption? SelectedSelectionMode { get; set; }
    [ObservableProperty] public partial bool EnablePreselection { get; set; }
    [ObservableProperty] public partial bool InvertOrbitVerticalAxis { get; set; }
    [ObservableProperty] public partial bool InvertZoom { get; set; }
    [ObservableProperty] public partial double OrbitSensitivity { get; set; }

    internal override bool TryApplyTo(CadoryxApplicationSettings settings)
    {
        if (SelectedSelectionMode is null ||
            double.IsNaN(OrbitSensitivity) ||
            double.IsInfinity(OrbitSensitivity) ||
            OrbitSensitivity is < 0.1 or > 5.0)
            return false;

        settings.Interaction.SelectionMode = SelectedSelectionMode.Mode;
        settings.Interaction.EnablePreselection = EnablePreselection;
        settings.Interaction.InvertOrbitVerticalAxis = InvertOrbitVerticalAxis;
        settings.Interaction.InvertZoom = InvertZoom;
        settings.Interaction.OrbitSensitivity = OrbitSensitivity;
        return true;
    }

    internal override void ResetToDefaults() => Load(new CadoryxInteractionSettings());

    private void Load(CadoryxInteractionSettings settings)
    {
        SelectedSelectionMode = SelectionModeOptions.First(option => option.Mode == settings.SelectionMode);
        EnablePreselection = settings.EnablePreselection;
        InvertOrbitVerticalAxis = settings.InvertOrbitVerticalAxis;
        InvertZoom = settings.InvertZoom;
        OrbitSensitivity = settings.OrbitSensitivity;
    }
}
