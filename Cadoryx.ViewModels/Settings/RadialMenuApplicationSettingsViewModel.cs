using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadoryx.ViewModels.Settings;

public sealed record RadialActionOption(CadoryxRadialAction Action, string Name);

public partial class RadialMenuApplicationSettingsViewModel : ApplicationSettingsSectionViewModel
{
    public RadialMenuApplicationSettingsViewModel(CadoryxRadialMenuSettings settings) : base(Strings.RadialMenu)
    {
        ActionOptions = Enum.GetValues<CadoryxRadialAction>()
            .Select(action => new RadialActionOption(action, Name(action))).ToArray();
        Pages =
        [
            new(CadoryxRadialPage.Middle, Strings.RadialMiddle, ActionOptions),
            new(CadoryxRadialPage.Shift, Strings.RadialShift, ActionOptions),
            new(CadoryxRadialPage.Control, Strings.RadialControl, ActionOptions),
            new(CadoryxRadialPage.Alt, Strings.RadialAlt, ActionOptions)
        ];
        SelectedPage = Pages[0];
        Load(settings);
    }

    public IReadOnlyList<RadialActionOption> ActionOptions { get; }
    public IReadOnlyList<RadialPageViewModel> Pages { get; }
    [ObservableProperty] public partial RadialPageViewModel SelectedPage { get; set; }
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    internal override bool TryApplyTo(CadoryxApplicationSettings settings)
    {
        settings.RadialMenu.IsEnabled = IsEnabled;
        foreach (var page in Pages)
            settings.RadialMenu.Set(page.Page, page.Slots.Select(slot => slot.SelectedAction.Action));
        return true;
    }

    internal override void ResetToDefaults()
    { Load(new CadoryxRadialMenuSettings()); SelectedPage = Pages[0]; }

    private void Load(CadoryxRadialMenuSettings settings)
    {
        IsEnabled = settings.IsEnabled;
        foreach (var page in Pages) page.Load(settings.Get(page.Page));
    }

    public static string Name(CadoryxRadialAction action)
    {
        var key = action switch
        {
            CadoryxRadialAction.None => "RadialEmpty",
            CadoryxRadialAction.Fit => "Fit",
            CadoryxRadialAction.FocusSelection => "ReviewFocus",
            CadoryxRadialAction.Top => "Top",
            CadoryxRadialAction.Front => "Front",
            CadoryxRadialAction.Right => "Right",
            CadoryxRadialAction.Axonometric => "Axonometric",
            CadoryxRadialAction.Undo => "Undo",
            CadoryxRadialAction.Redo => "Redo",
            CadoryxRadialAction.Save => "Save",
            CadoryxRadialAction.Isolate => "ReviewIsolate",
            CadoryxRadialAction.Hide => "ReviewHide",
            CadoryxRadialAction.ShowAll => "ReviewShowAll",
            CadoryxRadialAction.NewSketch => "NewSketch",
            CadoryxRadialAction.Box => "Box",
            CadoryxRadialAction.Cylinder => "Cylinder",
            _ => "RadialEmpty"
        };
        return Strings.ResourceManager.GetString(key, System.Globalization.CultureInfo.CurrentUICulture) ?? action.ToString();
    }
}

public sealed class RadialPageViewModel
{
    public RadialPageViewModel(CadoryxRadialPage page, string title, IReadOnlyList<RadialActionOption> options)
    {
        Page = page; Title = title;
        Slots = Enumerable.Range(0, CadoryxRadialMenuSettings.SectorCount)
            .Select(i => new RadialSlotViewModel(i + 1, options)).ToArray();
    }
    public CadoryxRadialPage Page { get; }
    public string Title { get; }
    public IReadOnlyList<RadialSlotViewModel> Slots { get; }
    public void Load(IReadOnlyList<CadoryxRadialAction> actions)
    {
        for (var i = 0; i < Slots.Count; i++)
            Slots[i].SelectedAction = Slots[i].Options.First(option => option.Action == actions[i]);
    }
}

public partial class RadialSlotViewModel(int number, IReadOnlyList<RadialActionOption> options) : ObservableObject
{
    public string Label { get; } = number.ToString(System.Globalization.CultureInfo.CurrentUICulture);
    public int Index { get; } = number - 1;
    public IReadOnlyList<RadialActionOption> Options { get; } = options;
    [ObservableProperty] public partial RadialActionOption SelectedAction { get; set; } = options[0];
    public void Clear() => SelectedAction = Options[0];
}
