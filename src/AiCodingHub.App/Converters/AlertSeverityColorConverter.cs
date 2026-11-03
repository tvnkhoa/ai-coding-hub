using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AiCodingHub.Core.Alerts;

namespace AiCodingHub.App.Converters;

/// <summary>Maps an <see cref="AlertSeverity"/> to the banner background brush.</summary>
public sealed class AlertSeverityColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = Freeze("#FF2D6CDF");
    private static readonly SolidColorBrush Warning = Freeze("#FFB4820A");
    private static readonly SolidColorBrush High = Freeze("#FFC2570F");
    private static readonly SolidColorBrush Critical = Freeze("#FFC02A2F");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AlertSeverity severity
            ? severity switch
            {
                AlertSeverity.Critical => Critical,
                AlertSeverity.High => High,
                AlertSeverity.Warning => Warning,
                _ => Info,
            }
            : Info;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
