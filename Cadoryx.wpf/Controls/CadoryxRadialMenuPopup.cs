using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Controls;

internal sealed class CadoryxRadialMenuPopup : IDisposable
{
    private const double Diameter = 296;
    private readonly RadialVisual visual = new();
    private readonly Popup popup;
    private HwndSource? popupSource;
    private DispatcherOperation? pendingRedraw;

    public CadoryxRadialMenuPopup()
    {
        popup = new Popup
        {
            AllowsTransparency = true, IsHitTestVisible = false, StaysOpen = true,
            Placement = PlacementMode.Relative, Child = visual
        };
        popup.Opened += (_, _) => AttachPopupHook();
        popup.Closed += (_, _) => DetachPopupHook();
    }

    public event EventHandler<int>? Wheel;
    internal nint WindowHandle => popupSource?.Handle ?? 0;

    private void AttachPopupHook()
    {
        if (popupSource is not null) return;
        popupSource = PresentationSource.FromVisual(visual) as HwndSource;
        popupSource?.AddHook(OnPopupMessage);
    }

    private void DetachPopupHook()
    {
        pendingRedraw?.Abort();
        pendingRedraw = null;
        popupSource?.RemoveHook(OnPopupMessage);
        popupSource = null;
    }

    private nint OnPopupMessage(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (message != 0x20A || !popup.IsOpen) return 0; // WM_MOUSEWHEEL
        handled = true;
        Wheel?.Invoke(this, unchecked((short)(((long)w >> 16) & 0xFFFF)));
        return 0;
    }

    public void Show(FrameworkElement target, Point point, IReadOnlyList<CadoryxRadialAction> actions,int page)
    {
        visual.SetActions(actions);
        visual.SetPage(page);
        visual.UpdatePointer(new(Diameter / 2, Diameter / 2));
        popup.PlacementTarget = target;
        popup.HorizontalOffset = point.X - Diameter / 2;
        popup.VerticalOffset = point.Y - Diameter / 2;
        popup.IsOpen = true;
        visual.UpdateLayout();
        AttachPopupHook();
    }

    public void CenterAtScreen(Point screenPoint)
    {
        if (popup.PlacementTarget is not FrameworkElement target) return;
        // Popup placement may shift by a few physical pixels after it opens,
        // especially across DPI boundaries. Correct against its actual center.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var center = ScreenCenter;
            if ((screenPoint - center).Length <= 1) break;
            var wanted = target.PointFromScreen(screenPoint);
            var current = target.PointFromScreen(center);
            popup.HorizontalOffset += wanted.X - current.X;
            popup.VerticalOffset += wanted.Y - current.Y;
            visual.UpdateLayout();
        }
    }

    public void SetPage(int page, IReadOnlyList<CadoryxRadialAction> actions)
    {
        visual.SetActions(actions);
        visual.SetPage(page);
        if (!popup.IsOpen || pendingRedraw is not null) return;
        // Wheel messages arrive through native HWND hooks. Rebuild the WPF
        // visual and invalidate the layered popup's presentation surface after
        // the input callback returns; do not wait for a pointer/hover update.
        pendingRedraw = visual.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            pendingRedraw = null;
            if (!popup.IsOpen || popupSource is not { IsDisposed: false } source) return;
            visual.UpdateLayout();
            if (popup.IsOpen && ReferenceEquals(source, popupSource) && !source.IsDisposed)
                RedrawWindow(source.Handle, 0, 0, 0x0001 | 0x0100); // RDW_INVALIDATE | RDW_UPDATENOW
        }));
    }
    public int Page => visual.Page;
    public bool IsOpen => popup.IsOpen;
    public CadoryxRadialAction SelectedAction => visual.SelectedAction;
    public Point ScreenCenter => visual.PointToScreen(new(Diameter / 2, Diameter / 2));
    internal FrameworkElement Visual => visual;
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
    public void Dispose() { Close(); DetachPopupHook(); popup.Child = null; popup.PlacementTarget = null; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint window, nint updateRect, nint updateRegion, uint flags);

    private sealed class RadialVisual : Canvas
    {
        private const double Inner = 45, Outer = 137, IconRadius = 91;
        private readonly PackIcon[] icons = new PackIcon[CadoryxRadialMenuSettings.SectorCount];
        private readonly Border[] pages = new Border[4];
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
        public int Page { get; private set; }
        private static readonly Point Mid = new(Diameter / 2, Diameter / 2);

        public RadialVisual()
        {
            Width = Diameter;Height = Diameter + 34; IsHitTestVisible = false;
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
            var footer=new StackPanel{Orientation=Orientation.Horizontal,IsHitTestVisible=false};
            for(int i=0;i<pages.Length;i++)
            {
                var label=new TextBlock
                {Text=(i+1).ToString(),FontSize=12,FontWeight=FontWeights.SemiBold,
                    HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
                pages[i]=new Border{Width=23,Height=22,Margin=new(3,0,3,0),CornerRadius=new(11),Child=label};
                footer.Children.Add(pages[i]);
            }
            Children.Add(footer);
            SetLeft(footer,Mid.X-58);SetTop(footer,Diameter+5);
            SetPage(0);
            UpdateIcons();
        }

        public void SetPage(int page)
        {
            if(page is < 0 or > 3)throw new ArgumentOutOfRangeException(nameof(page));
            Page=page;
            for(int i=0;i<pages.Length;i++)
            {
                pages[i].Background=Brush(i==page?0xF2663AB7:0xED20242C);
                pages[i].BorderBrush=Brush(i==page?0xFFE4C6FF:0xBC737984);
                pages[i].BorderThickness=new(1);
                ((TextBlock)pages[i].Child).Foreground=i==page?Brushes.White:Brush(0xDDE5E5EA);
            }
            // The popup is a layered HWND outside the native viewport. A page
            // change must request its own redraw, even if the pointer stays still.
            InvalidateVisual();
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
