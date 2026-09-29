namespace Cadoryx.ViewModels.Services.Platform.Settings;

public enum CadoryxRadialPage { Middle, Shift, Control, Alt }

public enum CadoryxRadialAction
{
    None, Fit, FocusSelection, Top, Front, Right, Axonometric,
    Undo, Redo, Save, Isolate, Hide, ShowAll, NewSketch, Box, Cylinder
}

public sealed class CadoryxRadialMenuSettings
{
    public const int SectorCount = 8;
    public bool IsEnabled { get; set; } = true;
    public CadoryxRadialAction[] Middle { get; set; } =
        [CadoryxRadialAction.Fit, CadoryxRadialAction.Top, CadoryxRadialAction.Front,
         CadoryxRadialAction.Right, CadoryxRadialAction.Axonometric, CadoryxRadialAction.FocusSelection,
         CadoryxRadialAction.Isolate, CadoryxRadialAction.ShowAll];
    public CadoryxRadialAction[] Shift { get; set; } =
        [CadoryxRadialAction.Undo, CadoryxRadialAction.Redo, CadoryxRadialAction.Save,
         CadoryxRadialAction.Hide, CadoryxRadialAction.ShowAll, CadoryxRadialAction.FocusSelection,
         CadoryxRadialAction.Fit, CadoryxRadialAction.Isolate];
    public CadoryxRadialAction[] Control { get; set; } =
        [CadoryxRadialAction.Box, CadoryxRadialAction.Cylinder, CadoryxRadialAction.NewSketch,
         CadoryxRadialAction.Top, CadoryxRadialAction.Front, CadoryxRadialAction.Right,
         CadoryxRadialAction.Axonometric, CadoryxRadialAction.Fit];
    public CadoryxRadialAction[] Alt { get; set; } =
        [CadoryxRadialAction.Save, CadoryxRadialAction.NewSketch, CadoryxRadialAction.Box,
         CadoryxRadialAction.Cylinder, CadoryxRadialAction.Isolate, CadoryxRadialAction.Hide,
         CadoryxRadialAction.ShowAll, CadoryxRadialAction.Fit];

    public IReadOnlyList<CadoryxRadialAction> Get(CadoryxRadialPage page) => page switch
    {
        CadoryxRadialPage.Shift => Shift,
        CadoryxRadialPage.Control => Control,
        CadoryxRadialPage.Alt => Alt,
        _ => Middle
    };

    public void Set(CadoryxRadialPage page, IEnumerable<CadoryxRadialAction> actions)
    {
        var copy = actions.Take(SectorCount).ToArray();
        switch (page)
        {
            case CadoryxRadialPage.Shift: Shift = copy; break;
            case CadoryxRadialPage.Control: Control = copy; break;
            case CadoryxRadialPage.Alt: Alt = copy; break;
            default: Middle = copy; break;
        }
        Normalize();
    }

    public CadoryxRadialMenuSettings Clone() => new()
    {
        IsEnabled = IsEnabled, Middle = [.. Middle], Shift = [.. Shift], Control = [.. Control], Alt = [.. Alt]
    };

    public void Normalize()
    {
        var defaults = new CadoryxRadialMenuSettings();
        Middle = Normalize(Middle, defaults.Middle);
        Shift = Normalize(Shift, defaults.Shift);
        Control = Normalize(Control, defaults.Control);
        Alt = Normalize(Alt, defaults.Alt);
    }

    private static CadoryxRadialAction[] Normalize(CadoryxRadialAction[]? values, CadoryxRadialAction[] defaults) =>
        Enumerable.Range(0, SectorCount).Select(i => values is not null && i < values.Length
            ? Enum.IsDefined(values[i]) ? values[i] : CadoryxRadialAction.None
            : defaults[i]).ToArray();
}
