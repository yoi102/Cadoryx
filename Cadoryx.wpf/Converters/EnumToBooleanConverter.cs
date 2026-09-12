using System.Globalization;
using System.Windows.Data;

namespace Cadoryx.wpf.Converters;

internal sealed class EnumToBooleanConverter : IValueConverter
{
    public static EnumToBooleanConverter Instance { get; } = new();

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        var enumType = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        if (!enumType.IsEnum)
            return false;

        var parameterValue = parameter is string parameterString
            ? Enum.Parse(enumType, parameterString)
            : parameter;
        return value.Equals(parameterValue);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (parameter is null ||
            value is not bool isChecked ||
            !isChecked)
        {
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (!enumType.IsEnum)
            return Binding.DoNothing;

        return parameter is string parameterString
            ? Enum.Parse(enumType, parameterString)
            : parameter;
    }
}
