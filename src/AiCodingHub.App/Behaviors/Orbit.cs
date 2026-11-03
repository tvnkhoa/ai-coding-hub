using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace AiCodingHub.App.Behaviors;

/// <summary>
/// Drives dashes around a rounded <see cref="Rectangle"/>'s outline like a drift car on a track:
/// the "this project is working" orbit on a group card. Speed is continuous — it accelerates
/// out of a bend, runs down the straight and brakes smoothly back to cornering speed — so the
/// motion never stutters. Several rectangles stacked on one card share the same timeline
/// (built from the card's exact geometry, recomputed on resize):
/// <list type="bullet">
/// <item><see cref="LengthProperty"/> — dash length in px; setting it enables the behavior.</item>
/// <item><see cref="LagProperty"/> — px the dash trails the head (a longer, fainter rectangle with
/// <c>Lag = trail − head</c> is the comet's tail).</item>
/// <item><see cref="FlashProperty"/> — the head glows while it takes a bend.</item>
/// <item><see cref="MarkProperty"/> — this rectangle is the tyre mark: as the car leaves a bend a
/// dash covering that bend lights up and fades on the track behind it.</item>
/// </list>
/// Animates only while the shape is visible.
/// </summary>
public static class Orbit
{
    public static readonly DependencyProperty LengthProperty = DependencyProperty.RegisterAttached(
        "Length", typeof(double), typeof(Orbit), new PropertyMetadata(0.0, OnChanged));

    public static readonly DependencyProperty LagProperty = DependencyProperty.RegisterAttached(
        "Lag", typeof(double), typeof(Orbit), new PropertyMetadata(0.0, OnChanged));

    /// <summary>Average straight-line speed in px/s.</summary>
    public static readonly DependencyProperty SpeedProperty = DependencyProperty.RegisterAttached(
        "Speed", typeof(double), typeof(Orbit), new PropertyMetadata(220.0, OnChanged));

    public static readonly DependencyProperty FlashProperty = DependencyProperty.RegisterAttached(
        "Flash", typeof(bool), typeof(Orbit), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty MarkProperty = DependencyProperty.RegisterAttached(
        "Mark", typeof(bool), typeof(Orbit), new PropertyMetadata(false, OnChanged));

    public static double GetLength(DependencyObject d) => (double)d.GetValue(LengthProperty);
    public static void SetLength(DependencyObject d, double v) => d.SetValue(LengthProperty, v);
    public static double GetLag(DependencyObject d) => (double)d.GetValue(LagProperty);
    public static void SetLag(DependencyObject d, double v) => d.SetValue(LagProperty, v);
    public static double GetSpeed(DependencyObject d) => (double)d.GetValue(SpeedProperty);
    public static void SetSpeed(DependencyObject d, double v) => d.SetValue(SpeedProperty, v);
    public static bool GetFlash(DependencyObject d) => (bool)d.GetValue(FlashProperty);
    public static void SetFlash(DependencyObject d, bool v) => d.SetValue(FlashProperty, v);
    public static bool GetMark(DependencyObject d) => (bool)d.GetValue(MarkProperty);
    public static void SetMark(DependencyObject d, bool v) => d.SetValue(MarkProperty, v);

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(Orbit), new PropertyMetadata(false));

    /// <summary>Cornering speed as a fraction of the straight-line average.</summary>
    private const double CornerFactor = 0.3;

    /// <summary>How long a tyre mark stays visible after the bend (s).</summary>
    private const double MarkFade = 0.75;

    /// <summary>How far a tyre mark reaches into the straights on each side of a bend (px).</summary>
    private const double MarkOverhang = 10;

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
            // A theme switch changes the stroke brush: re-tint the glow.
            DependencyPropertyDescriptor.FromProperty(Shape.StrokeProperty, typeof(Rectangle))
                .AddValueChanged(r, (_, _) => Restart(r));
        }

        Restart(r);
    }

    private readonly record struct Segment(double Length, bool Corner);

    private static void Stop(Rectangle r)
    {
        r.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        r.BeginAnimation(UIElement.OpacityProperty, null);
        if (r.Effect is DropShadowEffect glow)
        {
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        }
    }

    private static void Restart(Rectangle r)
    {
        var length = GetLength(r);
        var t = r.StrokeThickness;
        var w = r.ActualWidth - t;
        var h = r.ActualHeight - t;
        if (length <= 0 || t <= 0 || w <= 0 || h <= 0 || !r.IsVisible)
        {
            Stop(r);
            return;
        }

        // The rounded-rectangle outline runs clockwise from the top edge. The lap is timed from the
        // top-right bend so it ends on the long top straight: the last tyre mark fades out before
        // the loop restarts instead of being cut off.
        var radius = Math.Min(r.RadiusX, Math.Min(w, h) / 2);
        var arc = Math.PI * radius / 2;
        var top = w - 2 * radius;
        var side = h - 2 * radius;
        var segments = new[]
        {
            new Segment(arc, true), new Segment(side, false),
            new Segment(arc, true), new Segment(top, false),
            new Segment(arc, true), new Segment(side, false),
            new Segment(arc, true), new Segment(top, false),
        };
        var perimeter = 2 * top + 2 * side + 4 * arc;
        var units = perimeter / t;                       // dash lengths are in stroke-thickness units

        var mark = GetMark(r);
        var dashPx = mark ? arc + 2 * MarkOverhang : length;
        var dash = Math.Min(dashPx / t, units * 0.9);
        r.StrokeDashArray = new DoubleCollection { dash, Math.Max(0.1, units - dash) };

        var speed = Math.Max(20, GetSpeed(r));
        var cornerSpeed = speed * CornerFactor;
        var lag = GetLag(r);

        var offset = new DoubleAnimationUsingKeyFrames();
        var opacity = mark ? new DoubleAnimationUsingKeyFrames() : null;
        var glowFrames = GetFlash(r) ? new DoubleAnimationUsingKeyFrames() : null;

        double HeadOffset(double s) => (lag - s) / t;
        double MarkOffset(double cornerStart) => -(cornerStart - MarkOverhang) / t;

        var time = TimeSpan.Zero;
        var travelled = top; // distance of the top-right bend from the outline's start
        if (!mark)
        {
            offset.KeyFrames.Add(new LinearDoubleKeyFrame(HeadOffset(travelled), KeyTime.FromTimeSpan(time)));
        }
        opacity?.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(time)));
        glowFrames?.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(time)));

        var lastMarkEnd = TimeSpan.Zero;
        foreach (var seg in segments)
        {
            if (seg.Length <= 0.01)
            {
                continue;
            }

            var start = time;
            var startDistance = travelled;
            TimeSpan duration;
            if (seg.Corner)
            {
                duration = TimeSpan.FromSeconds(seg.Length / cornerSpeed);
                time += duration;
                travelled += seg.Length;
                if (!mark)
                {
                    offset.KeyFrames.Add(new LinearDoubleKeyFrame(HeadOffset(travelled), KeyTime.FromTimeSpan(time)));
                }

                glowFrames?.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(start + duration / 2)));
                glowFrames?.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(time + TimeSpan.FromSeconds(0.12))));

                if (mark)
                {
                    // Park the mark on this bend, light it as the car exits, let it fade behind.
                    offset.KeyFrames.Add(new DiscreteDoubleKeyFrame(MarkOffset(startDistance), KeyTime.FromTimeSpan(start)));
                    opacity!.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(start)));
                    opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.65, KeyTime.FromTimeSpan(time)));
                    lastMarkEnd = time + TimeSpan.FromSeconds(MarkFade);
                    opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(lastMarkEnd)));
                }
            }
            else
            {
                // Average speed `speed`, entering and leaving at cornering speed: a cubic ease whose
                // end slopes equal the cornering speed keeps the velocity continuous (no stutter).
                duration = TimeSpan.FromSeconds(seg.Length / speed);
                time += duration;
                travelled += seg.Length;
                var k = Math.Clamp(cornerSpeed * duration.TotalSeconds / seg.Length, 0, 3);
                const double a = 1.0 / 3;
                var spline = new KeySpline(a, Math.Clamp(a * k, 0, 1), 1 - a, Math.Clamp(1 - a * k, 0, 1));
                spline.Freeze();
                if (!mark)
                {
                    offset.KeyFrames.Add(new SplineDoubleKeyFrame(HeadOffset(travelled), KeyTime.FromTimeSpan(time), spline));
                }
            }
        }

        // Keyframes past the loop end (a fading mark or glow from the last bend) would be cut off:
        // make the loop long enough, and start the mark parked off-screen.
        var loop = time;
        offset.Duration = new Duration(loop);
        offset.RepeatBehavior = RepeatBehavior.Forever;
        if (mark && offset.KeyFrames.Count > 0)
        {
            offset.KeyFrames.Insert(0, new DiscreteDoubleKeyFrame(((DiscreteDoubleKeyFrame)offset.KeyFrames[^1]).Value, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        }
        r.BeginAnimation(Shape.StrokeDashOffsetProperty, offset);

        if (opacity is not null)
        {
            Trim(opacity, loop);
            opacity.Duration = new Duration(loop);
            opacity.RepeatBehavior = RepeatBehavior.Forever;
            r.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        if (glowFrames is not null)
        {
            Trim(glowFrames, loop);
            if (r.Effect is not DropShadowEffect glow)
            {
                glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0 };
                r.Effect = glow;
            }
            glow.Color = (r.Stroke as SolidColorBrush)?.Color ?? Colors.White;
            glowFrames.Duration = new Duration(loop);
            glowFrames.RepeatBehavior = RepeatBehavior.Forever;
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, glowFrames);
        }
    }

    /// <summary>Clamps keyframes to the loop: values past the end wrap no further than its last instant.</summary>
    private static void Trim(DoubleAnimationUsingKeyFrames frames, TimeSpan loop)
    {
        for (var i = frames.KeyFrames.Count - 1; i >= 0; i--)
        {
            if (frames.KeyFrames[i].KeyTime.TimeSpan > loop)
            {
                frames.KeyFrames[i].KeyTime = KeyTime.FromTimeSpan(loop);
            }
        }
    }
}
