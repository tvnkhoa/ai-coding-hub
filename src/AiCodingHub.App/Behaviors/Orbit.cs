using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AiCodingHub.App.Behaviors;

/// <summary>
/// Runs a single dash around a rounded <see cref="Rectangle"/>'s outline at a constant speed:
/// the "this project is working" orbit on a group card. The dash pattern is sized to the
/// shape's exact perimeter (recomputed on resize), so exactly one dash circles the border
/// whatever the card's height. Animates only while the shape is visible.
/// <code>&lt;Rectangle RadiusX="9" RadiusY="9" Stroke="…" b:Orbit.Length="36" /&gt;</code>
/// <see cref="LagProperty"/> shifts a dash backwards along the path, so a longer, fainter
/// rectangle with <c>Lag = trail − head</c> becomes the comet's tail.
/// </summary>
public static class Orbit
{
    /// <summary>Dash length in pixels; setting it enables the orbit.</summary>
    public static readonly DependencyProperty LengthProperty = DependencyProperty.RegisterAttached(
        "Length", typeof(double), typeof(Orbit), new PropertyMetadata(0.0, OnChanged));

    /// <summary>Pixels the dash trails behind the head (for a tail drawn as a longer dash).</summary>
    public static readonly DependencyProperty LagProperty = DependencyProperty.RegisterAttached(
        "Lag", typeof(double), typeof(Orbit), new PropertyMetadata(0.0, OnChanged));

    /// <summary>Travel speed in pixels per second.</summary>
    public static readonly DependencyProperty SpeedProperty = DependencyProperty.RegisterAttached(
        "Speed", typeof(double), typeof(Orbit), new PropertyMetadata(170.0, OnChanged));

    public static double GetLength(DependencyObject d) => (double)d.GetValue(LengthProperty);
    public static void SetLength(DependencyObject d, double v) => d.SetValue(LengthProperty, v);
    public static double GetLag(DependencyObject d) => (double)d.GetValue(LagProperty);
    public static void SetLag(DependencyObject d, double v) => d.SetValue(LagProperty, v);
    public static double GetSpeed(DependencyObject d) => (double)d.GetValue(SpeedProperty);
    public static void SetSpeed(DependencyObject d, double v) => d.SetValue(SpeedProperty, v);

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(Orbit), new PropertyMetadata(false));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Rectangle r)
        {
            return;
        }

        if (!(bool)r.GetValue(HookedProperty))
        {
            r.SetValue(HookedProperty, true);
            r.SizeChanged += (_, _) => Restart(r);
            r.IsVisibleChanged += (_, _) => Restart(r);
            r.Loaded += (_, _) => Restart(r);
        }

        Restart(r);
    }

    private static void Restart(Rectangle r)
    {
        var length = GetLength(r);
        var t = r.StrokeThickness;
        var w = r.ActualWidth - t;
        var h = r.ActualHeight - t;
        if (length <= 0 || t <= 0 || w <= 0 || h <= 0 || !r.IsVisible)
        {
            r.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            return;
        }

        var radius = Math.Min(r.RadiusX, Math.Min(w, h) / 2);
        var perimeter = 2 * (w - 2 * radius) + 2 * (h - 2 * radius) + 2 * Math.PI * radius;
        var units = perimeter / t;               // dash lengths are in stroke-thickness units
        var dash = Math.Min(length / t, units * 0.9);
        r.StrokeDashArray = new DoubleCollection { dash, Math.Max(0.1, units - dash) };

        var lag = GetLag(r) / t;
        var speed = Math.Max(10, GetSpeed(r));
        var animation = new DoubleAnimation(lag, lag - units, new Duration(TimeSpan.FromSeconds(perimeter / speed)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        r.BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
    }
}
