using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AiCodingHub.App.Behaviors;

/// <summary>
/// The "this project is working" animation on a group card: a tiny top-down car (body + four
/// wheels) laps the card's rounded border like a drift car. It accelerates out of each bend,
/// runs the straight, brakes smoothly to cornering speed, and in the bend the body yaws past
/// the track direction (the rear steps out) before counter-steering straight again. A faint
/// trail rides behind it and brightens while the car is sideways.
/// <para>
/// Usage: a transparent <see cref="Canvas"/> laid over the card with
/// <c>b:RaceTrack.Radius="9"</c>; the behavior builds the car and trail itself and runs only
/// while the canvas is visible. Poses are computed every frame from the card's geometry, so the
/// car, its yaw and the trail always agree.
/// </para>
/// </summary>
public static class RaceTrack
{
    /// <summary>Corner radius of the card (px); setting it enables the behavior.</summary>
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RaceTrack), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double v) => d.SetValue(RadiusProperty, v);

    private const double Inset = 0.75;          // the track runs along the card's border line
    private const double StraightSpeed = 210;   // average px/s on a straight
    private const double CornerFactor = 0.32;   // cornering speed as a fraction of it
    private const double MaxYaw = 46;           // degrees the body swings past the track in a bend
    private const double PreSwing = 0.4;        // share of MaxYaw already out when braking ends
    private const double YawDecay = 0.2;        // s: how fast the swing dies after the bend
    private const double FishtailPeriod = 0.55; // s: one counter-steer wobble out of the bend
    private const double TrailLength = 92;      // px: a light-cycle style neon trail

    private static readonly Dictionary<Canvas, Track> Active = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _rendering;

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Track), typeof(RaceTrack), new PropertyMetadata(null));

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Canvas canvas || canvas.GetValue(StateProperty) is Track)
        {
            return;
        }

        var track = new Track(canvas);
        canvas.SetValue(StateProperty, track);
        canvas.IsHitTestVisible = false;
        canvas.SizeChanged += (_, _) => track.Rebuild();
        canvas.IsVisibleChanged += (_, _) => Update(canvas, track);
        canvas.Loaded += (_, _) => Update(canvas, track);
        canvas.Unloaded += (_, _) => Remove(canvas);
    }

    private static void Update(Canvas canvas, Track track)
    {
        if (canvas.IsVisible && canvas.IsLoaded)
        {
            track.Rebuild();
            Active[canvas] = track;
            if (!_rendering)
            {
                CompositionTarget.Rendering += OnRendering;
                _rendering = true;
            }
        }
        else
        {
            Remove(canvas);
        }
    }

    private static void Remove(Canvas canvas)
    {
        Active.Remove(canvas);
        if (Active.Count == 0 && _rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed.TotalSeconds;
        foreach (var track in Active.Values)
        {
            track.Tick(now);
        }
    }

    /// <summary>One lap segment: a straight or a quarter bend.</summary>
    private sealed record Segment(bool Corner, double Length, double Duration, double StartTime, double StartDistance,
        Point From, double Heading, Point Center, double StartAngle);

    private sealed class Track
    {
        private readonly Canvas _canvas;
        private readonly Path _trail;
        private readonly Canvas _car;
        // Yaw pivots on the front axle, so the rear is what swings out.
        private readonly RotateTransform _rotate = new() { CenterX = 3.5, CenterY = 0 };
        private readonly TranslateTransform _translate = new();
        private readonly List<Segment> _segments = new();
        private double _lap, _perimeter, _cornerSpeed, _start = double.NaN;

        public Track(Canvas canvas)
        {
            _canvas = canvas;

            _trail = new Path { StrokeThickness = 1.8, StrokeDashCap = PenLineCap.Round, Opacity = 0.6 };
            _trail.SetResourceReference(Shape.StrokeProperty, "Hud.Race");
            _trail.Effect = _trailGlow;

            // Top-down car pointing along +X, centred on the origin: body, cabin, four wheels.
            _car = new Canvas { Width = 0, Height = 0 };
            AddRect(_car, -5.5, -3, 11, 6, 2, "Hud.Race");
            AddRect(_car, -1.2, -2, 3.2, 4, 1, "Hud.Race.Cabin", opacity: 0.8);   // windscreen / cabin
            foreach (var (x, y) in new[] { (2.2, -4.1), (2.2, 2.7), (-4.6, -4.1), (-4.6, 2.7) })
            {
                AddRect(_car, x, y, 2.6, 1.4, 0.6, "Hud.Foreground");
            }
            var group = new TransformGroup();
            group.Children.Add(_rotate);
            group.Children.Add(_translate);
            _car.RenderTransform = group;

            _car.Effect = _carGlow;
            canvas.Children.Add(_trail);
            canvas.Children.Add(_car);
        }

        // Neon glow (Tron light-cycle look); tinted from the theme's race colour on each rebuild.
        private readonly System.Windows.Media.Effects.DropShadowEffect _trailGlow = new() { ShadowDepth = 0, BlurRadius = 7, Opacity = 0.9 };
        private readonly System.Windows.Media.Effects.DropShadowEffect _carGlow = new() { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.85 };

        private static void AddRect(Canvas parent, double x, double y, double w, double h, double r, string brush, double opacity = 1)
        {
            var rect = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Opacity = opacity };
            rect.SetResourceReference(Shape.FillProperty, brush);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            parent.Children.Add(rect);
        }

        /// <summary>Recomputes the lap from the card size (the track starts at the top-right bend).</summary>
        public void Rebuild()
        {
            if (_canvas.TryFindResource("Hud.Race") is SolidColorBrush race)
            {
                _trailGlow.Color = race.Color;
                _carGlow.Color = race.Color;
            }

            _segments.Clear();
            var w = _canvas.ActualWidth - 2 * Inset;
            var h = _canvas.ActualHeight - 2 * Inset;
            if (w <= 4 || h <= 4)
            {
                _lap = 0;
                return;
            }

            var r = Math.Min(GetRadius(_canvas), Math.Min(w, h) / 2);
            double x0 = Inset, y0 = Inset, x1 = Inset + w, y1 = Inset + h;
            var arc = Math.PI * r / 2;
            var top = w - 2 * r;
            var side = h - 2 * r;
            _cornerSpeed = StraightSpeed * CornerFactor;

            // Clockwise from the top-right bend. Heading in degrees (0 = +X, 90 = +Y / down).
            var time = 0.0;
            var dist = 0.0;
            void Bend(Point center, double startAngle)
            {
                var d = arc / _cornerSpeed;
                _segments.Add(new Segment(true, arc, d, time, dist, default, 0, center, startAngle));
                time += d; dist += arc;
            }
            void Straight(double length, Point from, double heading)
            {
                if (length <= 0.01) return;
                var d = length / StraightSpeed;
                _segments.Add(new Segment(false, length, d, time, dist, from, heading, default, 0));
                time += d; dist += length;
            }

            Bend(new Point(x1 - r, y0 + r), -90);
            Straight(side, new Point(x1, y0 + r), 90);
            Bend(new Point(x1 - r, y1 - r), 0);
            Straight(top, new Point(x1 - r, y1), 180);
            Bend(new Point(x0 + r, y1 - r), 90);
            Straight(side, new Point(x0, y1 - r), 270);
            Bend(new Point(x0 + r, y0 + r), 180);
            Straight(top, new Point(x0 + r, y0), 0);
            _lap = time;
            _perimeter = dist;

            // Trail geometry: the same outline, drawn from the same starting point.
            var fig = new PathFigure { StartPoint = new Point(x1 - r, y0), IsClosed = true };
            var size = new Size(r, r);
            fig.Segments.Add(new ArcSegment(new Point(x1, y0 + r), size, 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(x1, y1 - r), true));
            fig.Segments.Add(new ArcSegment(new Point(x1 - r, y1), size, 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(x0 + r, y1), true));
            fig.Segments.Add(new ArcSegment(new Point(x0, y1 - r), size, 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(x0, y0 + r), true));
            fig.Segments.Add(new ArcSegment(new Point(x0 + r, y0), size, 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(x1 - r, y0), true));
            var geometry = new PathGeometry(new[] { fig });
            geometry.Freeze();
            _trail.Data = geometry;
            var units = _perimeter / _trail.StrokeThickness;
            var dash = TrailLength / _trail.StrokeThickness;
            _trail.StrokeDashArray = new DoubleCollection { dash, Math.Max(0.1, units - dash) };
        }

        public void Tick(double now)
        {
            if (_lap <= 0)
            {
                return;
            }
            if (double.IsNaN(_start))
            {
                _start = now;
            }

            var t = (now - _start) % _lap;
            var seg = _segments[^1];
            foreach (var s in _segments)
            {
                if (t < s.StartTime + s.Duration) { seg = s; break; }
            }

            var u = Math.Clamp((t - seg.StartTime) / seg.Duration, 0, 1);
            double along, x, y, heading, yaw;
            if (seg.Corner)
            {
                along = seg.Length * u;
                var angle = seg.StartAngle + 90 * u;
                var rad = angle * Math.PI / 180;
                var radius = seg.Length / (Math.PI / 2);
                x = seg.Center.X + radius * Math.Cos(rad);
                y = seg.Center.Y + radius * Math.Sin(rad);
                heading = angle + 90;
                // The rear steps out through the bend, building on the swing begun while braking.
                yaw = MaxYaw * (PreSwing + (1 - PreSwing) * Smooth(u));
            }
            else
            {
                // Hermite ease: leaves and enters at cornering speed, averages StraightSpeed.
                var k = _cornerSpeed * seg.Duration / seg.Length;
                along = seg.Length * Hermite(u, k);
                var rad = seg.Heading * Math.PI / 180;
                x = seg.From.X + along * Math.Cos(rad);
                y = seg.From.Y + along * Math.Sin(rad);
                heading = seg.Heading;
                // Out of the bend: a damped fishtail (one wobble past straight, then settle)…
                var since = t - seg.StartTime;
                var recover = MaxYaw * Math.Exp(-since / YawDecay) * Math.Cos(2 * Math.PI * since / FishtailPeriod);
                // …and on the brakes before the next bend the rear starts stepping out again.
                var brake = u > 0.7 ? MaxYaw * PreSwing * Smooth((u - 0.7) / 0.3) : 0;
                yaw = Math.Abs(recover) > brake ? recover : brake;
            }

            _translate.X = x;
            _translate.Y = y;
            _rotate.Angle = heading + yaw;

            var s0 = seg.StartDistance + along;
            // The trail's leading edge sits just behind the car.
            _trail.StrokeDashOffset = (TrailLength + 5 - s0) / _trail.StrokeThickness;
            // Always lit like a light-cycle trail; brighter while the car is sideways.
            _trail.Opacity = 0.55 + 0.45 * Math.Clamp(Math.Abs(yaw) / MaxYaw, 0, 1);
        }

        private static double Smooth(double u) => u * u * (3 - 2 * u);

        /// <summary>Progress 0→1 over a straight with end slopes k (normalised cornering speed).</summary>
        private static double Hermite(double u, double k)
        {
            var u2 = u * u;
            var u3 = u2 * u;
            return (u3 - 2 * u2 + u) * k + (-2 * u3 + 3 * u2) + (u3 - u2) * k;
        }
    }
}
