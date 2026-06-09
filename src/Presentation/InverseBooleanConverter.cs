namespace WarframeRelicOverlay.Presentation;

using System;
using System.Globalization;
using System.Windows.Data;

/// <summary>
/// Converts a <see cref="bool"/> to its inverse: <c>true</c> becomes
/// <c>false</c> and vice versa.  Used to disable the font-size slider
/// when the "Auto" checkbox is checked.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is not true;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is not true;
    }
}
