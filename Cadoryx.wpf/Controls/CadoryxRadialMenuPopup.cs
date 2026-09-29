using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Controls;

internal sealed class CadoryxRadialMenuPopup : IDisposable
{
    private const double Diameter = 296;
    private readonly RadialVisual visual = new();
    private readonly Popup popup;

    public CadoryxRadialMenuPopup() => popup = new Popup
    {
        AllowsTransparency = true, IsHitTestVisible = false, StaysOpen = true,
        Placement = PlacementMode.Relative, Child = visual
    };

    public void Show(FrameworkElement target, Point point, IReadOnlyList<CadoryxRadialAction> actions)
    {
        visual.SetActions(actions);
        visual.UpdatePointer(new(Diameter / 2, Diameter / 2));
        popup.PlacementTarget = target;
        popup.HorizontalOffset = point.X - Diameter / 2;
        popup.VerticalOffset = point.Y - Diameter / 2;
        popup.IsOpen = true;
    }

    public void SetActions(IReadOnlyList<CadoryxRadialAction> actions) => visual.SetActions(actions);
    public void Update(Point screenPoint)
    { if (popup.IsOpen) visual.UpdatePointer(visual.PointFromScreen(screenPoint)); }
    public CadoryxRadialAction Complete(Point screenPoint)
    {
        if (!popup.IsOpen) return CadoryxRadialAction.None;
        Update(screenPoint);
        var action = visual.SelectedAction;
        Close();
        return action;
    }
    public void Close() => popup.IsOpen = false;
    public void Dispose() { Close(); popup.Child = null; popup.PlacementTarget = null; }

    private sealed class RadialVisual : Canvas
    {
        private const double Inner = 45, Outer = 137, IconRadius = 91;
        private readonly PackIcon[] icons = new PackIcon[CadoryxRadialMenuSettings.SectorCount];
        private readonly TextBlock center = new()
        {
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        private readonly Grid centerContent = new()
        {
            Width = Inner * 2, Height = Inner * 2, IsHitTestVisible = false
        };
        private CadoryxRadialAction[] actions = new CadoryxRadialAction[CadoryxRadialMenuSettings.SectorCount];
        private int selected = -1;
        private static readonly Point Mid = new(Diameter / 2, Diameter / 2);

        public RadialVisual()
        {
            Width = Height = Diameter; IsHitTestVisible = false;
            for (var i = 0; i < icons.Length; i++)
            {
                var icon = new PackIcon { Width = 25, Height = 25, Foreground = Brushes.White, IsHitTestVisible = false };
                icons[i] = icon; Children.Add(icon);
                var p = Circle(IconRadius, -Math.PI / 2 + i * Math.PI / 4);
                SetLeft(icon, p.X - 12.5); SetTop(icon, p.Y - 12.5);
            }
            centerContent.Children.Add(center);
            Children.Add(centerContent);
            SetLeft(centerContent, Mid.X - Inner); SetTop(centerContent, Mid.Y - Inner);
            UpdateIcons();
        }

        public CadoryxRadialAction SelectedAction => selected < 0 ? CadoryxRadialAction.None : actions[selected];

        public void SetActions(IReadOnlyList<CadoryxRadialAction> next)
        {
            for (var i = 0; i < actions.Length; i++) actions[i] = i < next.Count ? next[i] : CadoryxRadialAction.None;
            UpdateIcons(); InvalidateVisual();
        }

        public void UpdatePointer(Point point)
        {
            var dx = point.X - Mid.X; var dy = point.Y - Mid.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var index = distance < Inner ? -1 : ((int)Math.Floor((Math.Atan2(dy, dx) + Math.PI / 2 + Math.PI / 8 + 2 * Math.PI) / (Math.PI / 4))) % 8;
            if (selected == index) return;
            selected = index; UpdateIcons(); InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            for (var i = 0; i < 8; i++)
                dc.DrawGeometry(Brush(i == selected ? 0xF2663AB7 : 0xED20242C),
                    new Pen(Brush(i == selected ? 0xFFE4C6FF : 0xBC737984), i == selected ? 1.5 : 1), Sector(i));
            dc.DrawEllipse(Brush(0xF52B3039), new Pen(Brush(0xD5A9ACB3), 1), Mid, Inner, Inner);
            dc.DrawEllipse(null, new Pen(Brush(0xA0FFFFFF), 1), Mid, Outer, Outer);
        }

        private void UpdateIcons()
        {
            for (var i = 0; i < icons.Length; i++)
            {
                icons[i].Visibility = actions[i] == CadoryxRadialAction.None ? Visibility.Hidden : Visibility.Visible;
                icons[i].Kind = CadoryxRadialActionIcon.KindFor(actions[i]);
                icons[i].Foreground = i == selected ? Brushes.White : Brush(0xDDE5E5EA);
            }
            center.Text = selected < 0 ? string.Empty : RadialMenuApplicationSettingsViewModel.Name(actions[selected]);
        }

        private static Geometry Sector(int index)
        {
            var a = -Math.PI / 2 + index * Math.PI / 4 - Math.PI / 8;
            var b = a + Math.PI / 4;
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(Circle(Inner, a), true, true);
                c.LineTo(Circle(Outer, a), true, false);
                c.ArcTo(Circle(Outer, b), new(Outer, Outer), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(Circle(Inner, b), true, false);
                c.ArcTo(Circle(Inner, a), new(Inner, Inner), 0, false, SweepDirection.Counterclockwise, true, false);
            }
            geometry.Freeze(); return geometry;
        }
        private static Point Circle(double radius, double angle) => new(Mid.X + radius * Math.Cos(angle), Mid.Y + radius * Math.Sin(angle));
        private static SolidColorBrush Brush(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }
}
