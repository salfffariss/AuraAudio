using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Converts a TimeSpan into compact playback duration format (m:ss or h:mm:ss).
/// </summary>
public class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return ts.ToString(@"h\:mm\:ss");
            return ts.ToString(@"m\:ss");
        }
        return "0:00";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => TimeSpan.Zero;
}

/// <summary>
/// Converts boolean to Visibility, supporting ConverterParameter='Inverse'.
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isTrue = value is bool b && b;
        if (parameter is string paramStr && paramStr.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            isTrue = !isTrue;
        }

        return isTrue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

/// <summary>
/// Converts null/empty check to Visibility, supporting ConverterParameter='Inverse'.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNotNull = value != null;
        if (value is string s && string.IsNullOrWhiteSpace(s)) isNotNull = false;

        if (parameter is string paramStr && paramStr.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            isNotNull = !isNotNull;
        }

        return isNotNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}

/// <summary>
/// Converts case-insensitive string equality against ConverterParameter into Visibility.
/// Supports pipe-delimited alternatives (e.g., "Songs|Library").
/// </summary>
public class StringMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string actual && parameter is string expected)
        {
            foreach (string part in expected.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (actual.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Visibility.Visible;
                }
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}

/// <summary>
/// Converts case-insensitive string equality against ConverterParameter into bool
/// for pure-XAML DataTriggers / RadioButton selection without hardcoded view brushes.
/// </summary>
public class StringMatchToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string actual && parameter is string expected)
        {
            foreach (string part in expected.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (actual.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string expected)
        {
            return expected.Split('|')[0].Trim();
        }
        return Binding.DoNothing;
    }
}

/// <summary>
/// Inverts a boolean value.
/// </summary>
public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>
/// Converts boolean to "TrueText|FalseText" string.
/// </summary>
public class BooleanToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        if (parameter is string paramStr && paramStr.Contains('|'))
        {
            var parts = paramStr.Split('|');
            return b ? parts[0] : parts[1];
        }
        return b ? "True" : "False";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => false;
}

/// <summary>
/// Converts numeric count (> 0) to Visibility, supporting ConverterParameter='Inverse'.
/// </summary>
public class NumberToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isPositive = false;
        if (value is int i) isPositive = i > 0;
        else if (value is long l) isPositive = l > 0;
        else if (value is double d) isPositive = d > 0;

        if (parameter is string paramStr && paramStr.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            isPositive = !isPositive;
        }

        return isPositive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}

/// <summary>
/// Converts 0-based index (e.g., AlternationIndex) to 1-based display string.
/// </summary>
public class OneBasedIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int idx && idx >= 0 && idx < 500000)
        {
            return (idx + 1).ToString();
        }
        return "1";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => 0;
}

/// <summary>
/// Compares two strings (e.g., row FilePath vs CurrentTrack.FilePath) for active track state.
/// </summary>
public class StringEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool match = values != null &&
                     values.Length >= 2 &&
                     values[0] is string a &&
                     values[1] is string b &&
                     !string.IsNullOrEmpty(a) &&
                     string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        bool inverse = parameter is string p && p.Equals("Inverse", StringComparison.OrdinalIgnoreCase);
        if (inverse) match = !match;

        if (targetType == typeof(Visibility))
        {
            return match ? Visibility.Visible : Visibility.Collapsed;
        }
        return match;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        Array.Empty<object>();
}

/// <summary>
/// Controls sort chevron visibility based on [ActiveSortColumn, IsSortAscending] and "ColumnName:Asc|Desc".
/// </summary>
public class SortIndicatorVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2 || parameter is not string paramStr)
            return Visibility.Collapsed;

        string activeColumn = values[0] as string ?? string.Empty;
        bool isAsc = values[1] is bool b && b;

        var parts = paramStr.Split(':');
        if (parts.Length != 2) return Visibility.Collapsed;

        string expectedColumn = parts[0];
        bool expectedAsc = parts[1].Equals("Asc", StringComparison.OrdinalIgnoreCase);

        bool match = activeColumn.Equals(expectedColumn, StringComparison.OrdinalIgnoreCase) && isAsc == expectedAsc;
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        Array.Empty<object>();
}

/// <summary>
/// Evaluates whether a track row is both active and actively playing ([TrackFilePath, CurrentTrackFilePath, IsPlaying]).
/// </summary>
public class ActiveTrackPlayingMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool activeAndPlaying = values != null &&
                                values.Length >= 3 &&
                                values[0] is string a &&
                                values[1] is string b &&
                                values[2] is bool isPlaying &&
                                isPlaying &&
                                !string.IsNullOrEmpty(a) &&
                                string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        bool inverse = parameter is string p && p.Equals("Inverse", StringComparison.OrdinalIgnoreCase);
        if (inverse) activeAndPlaying = !activeAndPlaying;

        if (targetType == typeof(Visibility))
        {
            return activeAndPlaying ? Visibility.Visible : Visibility.Collapsed;
        }
        return activeAndPlaying;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        Array.Empty<object>();
}
