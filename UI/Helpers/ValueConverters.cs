using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Mengubah TimeSpan menjadi string durasi waktu musik yang rapi (mm:ss atau hh:mm:ss).
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
/// Mengubah kode warna Hex (#RRGGBB) menjadi WPF SolidColorBrush secara aman.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    private static readonly Brush FallbackBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Slate-400

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                return FallbackBrush;
            }
        }
        return FallbackBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}

/// <summary>
/// Mengubah boolean menjadi Visibility (Visible / Collapsed), mendukung inverter melalui parameter 'Inverse'.
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
/// Mengubah null check menjadi Visibility (Visible jika tidak null, Collapsed jika null).
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
/// Mengubah perbandingan string menjadi Visibility (Visible jika cocok dengan parameter, Collapsed jika tidak).
/// Sangat bermanfaat untuk navigasi tab dan view switcher.
/// </summary>
public class StringMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string actual && parameter is string expected)
        {
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}

/// <summary>
/// Mengubah boolean menjadi string teks berdasarkan parameter pemisah pipa "TrueText|FalseText".
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
/// Mengubah perbandingan string menjadi Brush (misal untuk highlight navigasi aktif).
/// Parameter format: "ExpectedView:ActiveHex|InactiveHex", contoh "Library:#20202C|#00000000".
/// </summary>
public class StringMatchToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string actual && parameter is string paramStr)
        {
            var parts = paramStr.Split(':');
            if (parts.Length == 2)
            {
                string expected = parts[0];
                string colors = parts[1];
                var colorParts = colors.Split('|');
                string activeHex = colorParts[0];
                string inactiveHex = colorParts.Length > 1 ? colorParts[1] : "#00000000";

                bool isMatch = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
                string chosenHex = isMatch ? activeHex : inactiveHex;

                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(chosenHex);
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                    return Brushes.Transparent;
                }
            }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}

/// <summary>
/// Mengubah angka (int, double, long) menjadi Visibility (Visible jika > 0, Collapsed jika <= 0).
/// Mendukung parameter "Inverse".
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
/// Mengubah indeks 0-based (misalnya AlternationIndex) menjadi nomor urut 1-based (1, 2, 3...).
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
/// Membandingkan dua string (misal FilePath baris trek vs CurrentTrack.FilePath) untuk menandai trek aktif.
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



