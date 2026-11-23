using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace PortPilot.Linux.Controls;

/// <summary>
/// True when the bound value's text equals the parameter. Two-way, so a group of radio buttons can
/// bind <c>IsChecked</c> to one enum (or int) property.
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public static EqualsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter as string, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string text)
        {
            return BindingOperations.DoNothing;
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, text);
        }

        return System.Convert.ChangeType(text, Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture);
    }
}
