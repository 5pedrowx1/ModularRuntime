using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ModularRuntime.Desktop.Converters;

/// <summary>
/// Visible when <c>value.ToString()</c> equals one of the '|'-separated values in the parameter.
/// A leading '!' inverts the result.
/// </summary>
public sealed class MatchToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var spec = parameter as string ?? string.Empty;
        var invert = spec.StartsWith('!');
        var options = (invert ? spec[1..] : spec).Split('|');
        var matches = options.Contains(value?.ToString() ?? string.Empty, StringComparer.Ordinal);
        return matches ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Two-way binding between an enum property and a RadioButton (parameter = enum member name).</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Visible for non-null (and non-empty string) values; parameter "!" inverts.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && value is not string { Length: 0 };
        return hasValue ^ (parameter as string == "!") ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible for <see langword="true"/>; parameter "!" inverts.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ (parameter as string == "!") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when an integer is greater than zero; parameter "!" inverts.</summary>
public sealed class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int count && count > 0) ^ (parameter as string == "!") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Short upper-case label of a log level (INFO, WARN, ...).</summary>
public sealed class LevelShortConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() switch
    {
        "Information" => "INFO",
        "Warning" => "WARN",
        "Critical" => "CRIT",
        { } other => other.ToUpperInvariant(),
        null => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Joins a string sequence with ", ".</summary>
public sealed class JoinConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> items ? string.Join(", ", items) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Formats a timestamp in local time.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset time ? time.ToLocalTime().ToString(parameter as string ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
