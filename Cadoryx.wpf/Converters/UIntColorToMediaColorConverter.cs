using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Cadoryx.wpf.Converters;

/// <summary>
/// 将设置层使用的 ARGB <see cref="uint"/> 转换为 MahApps ColorPicker 使用的 <see cref="Color"/>。
/// </summary>
internal sealed class UIntColorToMediaColorConverter : IValueConverter
{
    public static readonly UIntColorToMediaColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is uint color
            ? Color.FromArgb(
                (byte)(color >> 24),
                (byte)(color >> 16),
                (byte)(color >> 8),
                (byte)color)
            : DependencyProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Color color
            ? ((uint)color.A << 24) |
              ((uint)color.R << 16) |
              ((uint)color.G << 8) |
              color.B
            : Binding.DoNothing;
    }
}
