using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PortPilot.App.Converters;

/// <summary>Visible when the value is truthy (true, non-null, non-empty, non-zero). <c>Invert</c> flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            _ => true,
        };
        return truthy ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Compares an enum (or any value) with the parameter by name. Two-way for radio buttons.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (equal ? Visibility.Visible : Visibility.Collapsed) : equal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name)
        {
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, name);
            }

            if (targetType == typeof(int))
            {
                return int.Parse(name, CultureInfo.InvariantCulture);
            }
        }

        return Binding.DoNothing;
    }
}

/// <summary>Negates a boolean.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}


/// <summary>
/// Produces "Ascending", "Descending" or null for a table header, given the active sort column and
/// direction (bindings 0 and 1) and the header's own column (parameter).
/// </summary>
public sealed class SortTagConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0]?.ToString() != parameter?.ToString())
        {
            return null;
        }

        return values[1] is true ? "Descending" : "Ascending";
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}