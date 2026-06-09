namespace WarframeRelicOverlay.Presentation;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// Converts a <see cref="bool"/> to <see cref="Visibility"/> with
/// inverted logic: <c>true</c> maps to <see cref="Visibility.Collapsed"/>
/// and <c>false</c> maps to <see cref="Visibility.Visible"/>.
///
/// <para>
/// Used in the panel tab bar so the History content area is visible when
/// the Settings tab is <b>not</b> active and hidden when it is.
/// </para>
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Collapsed;
    }
}
