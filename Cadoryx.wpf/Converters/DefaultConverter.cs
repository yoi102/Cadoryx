using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cadoryx.wpf.Converters;

internal class DefaultConverter<T>(T defaultValue, T nonDefaultValue) : IValueConverter
{
    public T DefaultValue { get; set; } = defaultValue;
    public T NonDefaultValue { get; set; } = nonDefaultValue;

    public virtual object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IsDefaultValue(value) ? DefaultValue : NonDefaultValue;

    public virtual object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static bool IsDefaultValue(object? value)
    {
        if (value is null ||
            ReferenceEquals(value, DependencyProperty.UnsetValue) ||
            ReferenceEquals(value, Binding.DoNothing))
            return true;

        var valueType = value.GetType();
        return valueType.IsValueType && value.Equals(Activator.CreateInstance(valueType));
    }
}
