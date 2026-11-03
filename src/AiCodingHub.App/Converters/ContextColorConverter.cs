using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AiCodingHub.App.Converters;

/// <summary>
/// Maps a context-usage percentage to the theme's severity brush (ok → warn → high → critical)
/// at the default thresholds (80 / 90 / 95). Used by the detail widgets; the sessions dashboard
/// uses per-row levels computed from the configured Alerts thresholds instead.
/// </summary>
public sealed class ContextColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var pct = value is double d ? d : 0;
        var key = pct switch
        {
            >= 95 => "Hud.Status.Critical",
            >= 90 => "Hud.Status.High",
            >= 80 => "Hud.Status.Warn",
            _ => "Hud.Status.Ok",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
