namespace WarframeRelicOverlay.Presentation.Converters;

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

/// <summary>
/// Converts an ARGB hex colour string (e.g. "#EE181410") to a
/// <see cref="SolidColorBrush"/> for use in XAML bindings.
/// Returns a transparent brush if the string cannot be parsed.
/// </summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class StringToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch
            {
                // Fall through to transparent.
            }
        }

        return Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush brush)
            return brush.Color.ToString();
        return "#00000000";
    }
}
