using System.Windows;
using System.Windows.Controls;
using Cadoryx.ViewModels.Services.Platform.Settings;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Controls;

public sealed class CadoryxRadialActionIcon : ContentControl
{
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(CadoryxRadialAction), typeof(CadoryxRadialActionIcon),
        new PropertyMetadata(CadoryxRadialAction.None, (target, _) => ((CadoryxRadialActionIcon)target).Refresh()));

    public CadoryxRadialAction Action
    {
        get => (CadoryxRadialAction)GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    public CadoryxRadialActionIcon()
    {
        Width = Height = 28;
        Padding = new Thickness(0);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        Refresh();
    }

    private void Refresh() => Content = new PackIcon
    {
        Kind = KindFor(Action), Width = 28, Height = 28, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Opacity = Action == CadoryxRadialAction.None ? 0.35 : 1
    };

    internal static PackIconKind KindFor(CadoryxRadialAction action)
    {
        var name = action switch
        {
            CadoryxRadialAction.Fit => "FitToScreen", CadoryxRadialAction.FocusSelection => "Target",
            CadoryxRadialAction.Top => "ArrowUpBoldBoxOutline", CadoryxRadialAction.Front => "ViewFront",
            CadoryxRadialAction.Right => "ArrowRightBoldBoxOutline", CadoryxRadialAction.Axonometric => "CubeOutline",
            CadoryxRadialAction.Undo => "Undo", CadoryxRadialAction.Redo => "Redo",
            CadoryxRadialAction.Save => "ContentSaveOutline", CadoryxRadialAction.Isolate => "EyeOutline",
            CadoryxRadialAction.Hide => "EyeOffOutline", CadoryxRadialAction.ShowAll => "EyeCheckOutline",
            CadoryxRadialAction.NewSketch => "PencilOutline", CadoryxRadialAction.Box => "Cube",
            CadoryxRadialAction.Cylinder => "Cylinder", _ => "CircleOutline"
        };
        return Enum.TryParse<PackIconKind>(name, out var kind) ? kind : PackIconKind.CircleOutline;
    }
}
