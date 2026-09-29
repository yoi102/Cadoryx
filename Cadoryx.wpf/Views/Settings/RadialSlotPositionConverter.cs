using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cadoryx.wpf.Views.Settings;

/// <summary>Places each 54 px badge at the radial midpoint of the 348 px preview's annulus.</summary>
public sealed class RadialSlotPositionConverter : IValueConverter
{
    private const double Center = 174;
    private const double Radius = (46 + 156) / 2.0;
    private const double HalfBadge = 27;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int index || index is < 0 or > 7) return DependencyProperty.UnsetValue;
        var angle = -Math.PI / 2 + index * Math.PI / 4;
        return Center + Radius * (Equals(parameter, "Y") ? Math.Sin(angle) : Math.Cos(angle)) - HalfBadge;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
