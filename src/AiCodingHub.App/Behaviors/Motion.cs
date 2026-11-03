using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AiCodingHub.App.Behaviors;

/// <summary>
/// Small motion helpers for XAML. <c>Motion.ScaleX</c> eases an element's horizontal scale to the
/// bound value (0–1) instead of jumping, which turns any fill <c>Border</c> into a smoothly
/// animated progress bar: <c>&lt;Border b:Motion.ScaleX="{Binding Fraction}"/&gt;</c>.
/// </summary>
public static class Motion
{
    private static readonly Duration BarDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static readonly DependencyProperty ScaleXProperty = DependencyProperty.RegisterAttached(
        "ScaleX", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnScaleXChanged));

    public static double GetScaleX(DependencyObject d) => (double)d.GetValue(ScaleXProperty);

    public static void SetScaleX(DependencyObject d, double value) => d.SetValue(ScaleXProperty, value);

    private static void OnScaleXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        var target = Math.Clamp((double)e.NewValue, 0, 1);
        if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(target, 1);
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0, 0.5);
            return; // first value: no animation from zero on load
        }

        var animation = new DoubleAnimation(target, BarDuration) { EasingFunction = Ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static IEasingFunction Freeze(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
