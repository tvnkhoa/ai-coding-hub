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
    private const double StraightSpeed = 190;   // average px/s on a straight
    private const double CornerFactor = 0.12;   // cornering speed as a fraction of it: slow, readable bends (~0.6 s each)
    private const double MaxYaw = 46;           // degrees the body swings past the track in a bend
    private const double PreSwing = 0.4;        // share of MaxYaw already out when braking ends
    private const double YawDecay = 0.28;       // s: how fast the swing dies after the bend
    private const double FishtailPeriod = 0.75; // s: one counter-steer wobble out of the bend
    private const double TrailSeconds = 0.55;   // the trail is where the rear wheels were this long ago
    private const int TrailSegments = 48;       // pieces the trail is drawn in (fading with age)
    private const int TrailBands = 12;          // opacity steps: each band is one polyline
    private const int AlphaLevels = 32;         // quantised trail opacities (one cached pen each)
    private const double TrailThickness = 1.8;
    private const double GlowThickness = 5.5;   // the neon halo: a wide faint stroke under the line
    private const double GlowAlpha = 0.28;

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
        private readonly TrailHost _trail = new();
        private readonly List<(double Time, Point Rear)> _history = new();
        private readonly Canvas _car;
        // Heading turns the car about its centre; yaw then pivots it on the front axle, so the
        // rear is what swings out in a bend.
        private readonly RotateTransform _yaw = new() { CenterX = 3.5, CenterY = 0 };
        private readonly RotateTransform _heading = new();
        private readonly TranslateTransform _translate = new();
        private readonly List<Segment> _segments = new();
        private double _lap, _perimeter, _cornerSpeed, _start = double.NaN;

        public Track(Canvas canvas)
        {
            _canvas = canvas;

            // The trail traces the rear wheels' real path, so when the tail swings out in a bend the
            // trail swings out with it into an arc (a light-cycle wall drawn by the car). It is one
            // visual redrawn per frame; its glow is a wide faint stroke, not a per-frame blur.

            // Top-down car pointing along +X, centred on the origin: body, cabin, four wheels.
            // The glow blur sits on the inner body and the outer canvas is bitmap-cached, so the
            // blur is rendered once and each frame only moves and turns the cached bitmap.
            var body = new Canvas { Width = 0, Height = 0, Effect = _carGlow };
            AddRect(body, -5.5, -3, 11, 6, 2, "Hud.Race");
            AddRect(body, -1.2, -2, 3.2, 4, 1, "Hud.Race.Cabin", opacity: 0.8);   // windscreen / cabin
            foreach (var (x, y) in new[] { (2.2, -4.1), (2.2, 2.7), (-4.6, -4.1), (-4.6, 2.7) })
            {
                AddRect(body, x, y, 2.6, 1.4, 0.6, "Hud.Foreground");
            }
            _car = new Canvas { Width = 0, Height = 0, CacheMode = new BitmapCache { RenderAtScale = 2 } };
            _car.Children.Add(body);
            var group = new TransformGroup();
            group.Children.Add(_yaw);
            group.Children.Add(_heading);
            group.Children.Add(_translate);
            _car.RenderTransform = group;

            _trail.SetResourceReference(TrailHost.BrushProperty, "Hud.Race");
            _trail.BrushChanged += Retint;
            canvas.Children.Add(_trail);
            canvas.Children.Add(_car);
        }

        // Neon glow (Tron light-cycle look); tinted from the theme's race colour on each rebuild.
        private readonly System.Windows.Media.Effects.DropShadowEffect _carGlow = new() { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.85 };
        private Color _color;
        private Pen[] _corePens = Array.Empty<Pen>();
        private Pen[] _glowPens = Array.Empty<Pen>();
        private readonly Point[] _points = new Point[TrailSegments + 1];
        private readonly double[] _alphas = new double[TrailSegments];

        private static void AddRect(Canvas parent, double x, double y, double w, double h, double r, string brush, double opacity = 1)
        {
            var rect = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Opacity = opacity };
            rect.SetResourceReference(Shape.FillProperty, brush);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            parent.Children.Add(rect);
        }

        /// <summary>Tints the glow and the trail pens from the theme's race colour.</summary>
        private void Retint()
        {
            if (_trail.Brush is not SolidColorBrush race || (_corePens.Length > 0 && race.Color == _color))
            {
                return;
            }
            _color = race.Color;
            _carGlow.Color = race.Color;
            _corePens = MakePens(race.Color, TrailThickness, 1, PenLineCap.Round);
            _glowPens = MakePens(race.Color, GlowThickness, GlowAlpha, PenLineCap.Flat);
        }

        /// <summary>Recomputes the lap from the card size (the track starts at the top-right bend).</summary>
        public void Rebuild()
        {
            Retint();
            _segments.Clear();
            _history.Clear();
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
            _heading.Angle = heading;
            _yaw.Angle = yaw;

            // Rear axle in canvas space: (-4.5, 0) yawed about the front axle, then turned to the
            // heading and moved onto the track.
            var yr = yaw * Math.PI / 180;
            var lx = 3.5 + (-4.5 - 3.5) * Math.Cos(yr);
            var ly = (-4.5 - 3.5) * Math.Sin(yr);
            var hr = heading * Math.PI / 180;
            var rear = new Point(x + lx * Math.Cos(hr) - ly * Math.Sin(hr), y + lx * Math.Sin(hr) + ly * Math.Cos(hr));
            DrawTrail(now, rear, Math.Clamp(Math.Abs(yaw) / MaxYaw, 0, 1));
        }

        /// <summary>Draws the rear wheels' recent path, fading with age; brighter while sideways.</summary>
        private void DrawTrail(double now, Point rear, double sideways)
        {
            _history.Add((now, rear));
            var cutoff = now - TrailSeconds;
            var drop = 0;
            while (drop < _history.Count - 1 && _history[drop].Time < cutoff) drop++;
            if (drop > 0) _history.RemoveRange(0, drop);

            using var dc = _trail.Visual.RenderOpen();
            var points = _history.Count;
            var pieces = Math.Min(TrailSegments, points - 1);
            if (pieces <= 0 || _corePens.Length == 0)
            {
                return;
            }

            // At most TrailSegments pieces, spread evenly over the history; piece i fades with the
            // age of its newer end (0 at the car, 1 at the tip). Pieces are drawn in TrailBands
            // runs, one polyline per run at the run's mean opacity, so joins stay smooth and the
            // draw count stays small. The wide faint glow pass goes under the core line.
            var boost = 0.75 + 0.25 * sideways;
            for (var i = 0; i <= pieces; i++)
            {
                var h = _history[(int)((long)i * (points - 1) / pieces)];
                _points[i] = h.Rear;
                if (i > 0)
                {
                    _alphas[i - 1] = Math.Clamp(1 - (now - h.Time) / TrailSeconds, 0, 1) * boost;
                }
            }

            var perBand = (pieces + TrailBands - 1) / TrailBands;
            for (var pass = 0; pass < 2; pass++)
            {
                var pens = pass == 0 ? _glowPens : _corePens;
                for (var start = 0; start < pieces; start += perBand)
                {
                    var end = Math.Min(pieces, start + perBand);
                    var alpha = 0.0;
                    for (var k = start; k < end; k++) alpha += _alphas[k];
                    var level = (int)Math.Round(alpha / (end - start) * (AlphaLevels - 1));
                    if (level <= 0) continue;

                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(_points[start], false, false);
                        for (var k = start + 1; k <= end; k++) g.LineTo(_points[k], true, true);
                    }
                    geometry.Freeze();
                    dc.DrawGeometry(null, pens[level], geometry);
                }
            }
        }

        /// <summary>One frozen pen per quantised opacity level: no per-frame brush allocations.</summary>
        private static Pen[] MakePens(Color color, double thickness, double scale, PenLineCap cap)
        {
            var pens = new Pen[AlphaLevels];
            for (var i = 0; i < AlphaLevels; i++)
            {
                var brush = new SolidColorBrush(color) { Opacity = scale * i / (AlphaLevels - 1) };
                brush.Freeze();
                var pen = new Pen(brush, thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
                pen.Freeze();
                pens[i] = pen;
            }
            return pens;
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

    /// <summary>Hosts the trail's single <see cref="DrawingVisual"/>; redrawing it causes no layout.</summary>
    private sealed class TrailHost : FrameworkElement
    {
        /// <summary>The theme's race brush (a resource reference), so a theme switch re-tints at once.</summary>
        public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
            nameof(Brush), typeof(Brush), typeof(TrailHost),
            new PropertyMetadata(null, (d, _) => ((TrailHost)d).BrushChanged?.Invoke()));

        public Brush? Brush => (Brush?)GetValue(BrushProperty);

        public event Action? BrushChanged;

        public DrawingVisual Visual { get; } = new();

        public TrailHost()
        {
            IsHitTestVisible = false;
            AddVisualChild(Visual);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => Visual;
    }
}
