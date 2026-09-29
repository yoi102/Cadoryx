using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using Cadoryx.Db;

namespace Cadoryx.wpf.Controls;

public sealed class DrawingSheetCanvas : FrameworkElement
{
    public const double PixelsPerMm=96d/25.4;
    public DrawingPageLayout? Page {get;set;}
    public event EventHandler<Point2d>? PaperClicked;
    public DrawingSheetCanvas(DrawingPageLayout page)
    {
        Page=page;Width=page.WidthMm*PixelsPerMm;Height=page.HeightMm*PixelsPerMm;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point=e.GetPosition(this);
        PaperClicked?.Invoke(this,new(point.X/PixelsPerMm,point.Y/PixelsPerMm));
    }
    protected override void OnRender(DrawingContext dc)
    {
        if(Page is not {} page)return;
        dc.DrawRectangle(Brushes.White,null,new Rect(0,0,Width,Height));
        dc.PushClip(new RectangleGeometry(new Rect(0,0,Width,Height)));
        foreach(var line in page.Lines)
        {
            var pen=new Pen(Brushes.Black,Math.Max(.35,line.WidthMm*PixelsPerMm));
            if(line.Hidden)pen.DashStyle=DashStyles.Dash;
            pen.Freeze();
            dc.DrawLine(pen,new(line.A.X*PixelsPerMm,line.A.Y*PixelsPerMm),
                new(line.B.X*PixelsPerMm,line.B.Y*PixelsPerMm));
        }
        foreach(var label in page.Labels)
        {
            var text=new FormattedText(label.Text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),label.SizeMm*PixelsPerMm,
                new SolidColorBrush(Color.FromArgb((byte)(label.Argb>>24),(byte)(label.Argb>>16),
                    (byte)(label.Argb>>8),(byte)label.Argb)),1);
            dc.DrawText(text,new(label.Position.X*PixelsPerMm,label.Position.Y*PixelsPerMm-text.Baseline));
        }
        dc.Pop();
    }
}
