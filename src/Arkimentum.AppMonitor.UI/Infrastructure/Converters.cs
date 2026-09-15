using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Arkimentum.AppMonitor.UI;

/// <summary>true =&gt; Visible, false =&gt; Collapsed. Pass ConverterParameter="invert" to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Non-empty string =&gt; Visible.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Inserts a thin space (U+2009 ≈ 0.2 em) between characters so a short caps wordmark gets the brand's
/// letter-spaced look. WPF's TextBlock has no letter-spacing property; keeping this in the view layer means the
/// string itself stays a plain word in the host app.
/// </summary>
public sealed class LetterSpacingConverter : IValueConverter
{
    private const char ThinSpace = '\u2009';

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length < 2) return value ?? string.Empty;
        return string.Join(ThinSpace, text.ToCharArray());
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
