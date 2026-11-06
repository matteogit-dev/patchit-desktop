using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PatchIt.Client.Converters;

public sealed class FilterMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        var active = values[0] as string ?? "";
        var tag = values[1] as string ?? "";
        return string.Equals(active, tag, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        var isNull = value is null;
        var visible = invert ? isNull : !isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = (value as string ?? "").Trim().Length > 0;
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return (invert ? !hasText : hasText) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Toglie una quantità (parameters) a un numero: serve a legare
/// l'altezza dei pannelli allo spazio reale della finestra.</summary>
public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double number) return value ?? 0d;

        var amount = 0d;
        if (parameter is string text)
            double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);

        return Math.Max(0, number - amount);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
