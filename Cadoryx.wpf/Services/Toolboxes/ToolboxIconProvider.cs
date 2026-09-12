using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Cadoryx.ViewModels.Services.Platform;

namespace Cadoryx.wpf.Services.Toolboxes;

internal sealed class ToolboxIconProvider : IToolboxIconProvider
{
    public object ModelTree => CreateModelTreeIcon();
    public object Properties => CreatePropertiesIcon();
    public object Modeling => CreateModelingIcon();
    public object Messages => CreateMessagesIcon();

    private static Binding ForegroundBinding() => new()
    {
        Path = new PropertyPath(TextElement.ForegroundProperty),
        RelativeSource = new RelativeSource(RelativeSourceMode.Self)
    };

    private static Viewbox CreateIcon(Canvas canvas) => new()
    {
        Width = 18,
        Height = 18,
        Stretch = Stretch.Uniform,
        Child = canvas
    };

    private static void BindStroke(Shape shape)
    {
        shape.SetBinding(Shape.StrokeProperty, ForegroundBinding());
    }

    private static void BindFill(Shape shape)
    {
        shape.SetBinding(Shape.FillProperty, ForegroundBinding());
    }

    private static Viewbox CreateModelTreeIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        var root = new Rectangle
        {
            Width = 7,
            Height = 4,
            RadiusX = 0.7,
            RadiusY = 0.7,
            StrokeThickness = 1.1,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(root, 4.5);
        Canvas.SetTop(root, 1.5);
        BindStroke(root);

        var branch = new Path
        {
            Data = Geometry.Parse("M8,5.5 L8,8 M3,8 L13,8 M3,8 L3,11.5 M8,8 L8,11.5 M13,8 L13,11.5"),
            StrokeThickness = 1.1,
            Fill = Brushes.Transparent,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        BindStroke(branch);

        canvas.Children.Add(root);
        canvas.Children.Add(branch);
        foreach (var left in new[] { 1.5, 6.5, 11.5 })
        {
            var node = new Rectangle
            {
                Width = 3,
                Height = 3,
                RadiusX = 0.5,
                RadiusY = 0.5,
                StrokeThickness = 1,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(node, left);
            Canvas.SetTop(node, 11.5);
            BindStroke(node);
            canvas.Children.Add(node);
        }

        return CreateIcon(canvas);
    }

    private static Viewbox CreatePropertiesIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        foreach (var (top, left) in new[] { (3.0, 5.0), (8.0, 9.0), (13.0, 4.0) })
        {
            var line = new Line
            {
                X1 = 2,
                X2 = 14,
                Y1 = top,
                Y2 = top,
                StrokeThickness = 1.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            BindStroke(line);
            canvas.Children.Add(line);

            var knob = new Ellipse
            {
                Width = 3,
                Height = 3,
                StrokeThickness = 1.1,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(knob, left - 1.5);
            Canvas.SetTop(knob, top - 1.5);
            BindStroke(knob);
            canvas.Children.Add(knob);
        }

        return CreateIcon(canvas);
    }

    private static Viewbox CreateModelingIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        var cube = new Path
        {
            Data = Geometry.Parse("M8,1.5 L14,5 L14,11.5 L8,15 L2,11.5 L2,5 Z M2,5 L8,8.5 L14,5 M8,8.5 L8,15"),
            StrokeThickness = 1.1,
            Fill = Brushes.Transparent,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        BindStroke(cube);

        var plus = new Path
        {
            Data = Geometry.Parse("M11,8.5 L11,13 M8.75,10.75 L13.25,10.75"),
            StrokeThickness = 1.4,
            Fill = Brushes.Transparent,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        BindStroke(plus);
        canvas.Children.Add(cube);
        canvas.Children.Add(plus);
        return CreateIcon(canvas);
    }

    private static Viewbox CreateMessagesIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        var bubble = new Path
        {
            Data = Geometry.Parse("M2,2.5 C2,1.7 2.7,1 3.5,1 L12.5,1 C13.3,1 14,1.7 14,2.5 L14,10 C14,10.8 13.3,11.5 12.5,11.5 L7,11.5 L3.5,14.5 L4.1,11.5 L3.5,11.5 C2.7,11.5 2,10.8 2,10 Z"),
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
            StrokeLineJoin = PenLineJoin.Round
        };
        BindStroke(bubble);
        canvas.Children.Add(bubble);

        foreach (var left in new[] { 5.0, 8.0, 11.0 })
        {
            var dot = new Ellipse { Width = 1.5, Height = 1.5 };
            Canvas.SetLeft(dot, left - 0.75);
            Canvas.SetTop(dot, 5.25);
            BindFill(dot);
            canvas.Children.Add(dot);
        }

        return CreateIcon(canvas);
    }
}
