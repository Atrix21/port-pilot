using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PortPilot.App.Controls;

/// <summary>
/// Animates an element in and out instead of snapping its visibility. Fades opacity, and optionally
/// lifts (<see cref="OffsetProperty"/>) and scales (<see cref="ScaleProperty"/>) the element as it appears.
/// The element is collapsed once hidden so it costs nothing to render.
/// </summary>
public static class Reveal
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly Duration InDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration OutDuration = TimeSpan.FromMilliseconds(120);

    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool?), typeof(Reveal), new PropertyMetadata(null, OnIsShownChanged));

    // Nullable so the first binding value – even false – always runs the change handler.
    public static bool? GetIsShown(DependencyObject element) => (bool?)element.GetValue(IsShownProperty);

    public static void SetIsShown(DependencyObject element, bool? value) => element.SetValue(IsShownProperty, value);

    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(Reveal), new PropertyMetadata(0d));

    public static double GetOffset(DependencyObject element) => (double)element.GetValue(OffsetProperty);

    public static void SetOffset(DependencyObject element, double value) => element.SetValue(OffsetProperty, value);

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(Reveal), new PropertyMetadata(1d));

    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);

    public static void SetScale(DependencyObject element, double value) => element.SetValue(ScaleProperty, value);

    /// <summary>Optional child that receives the lift/scale motion (e.g. a dialog card) while the root only fades.</summary>
    public static readonly DependencyProperty MotionTargetProperty = DependencyProperty.RegisterAttached(
        "MotionTarget", typeof(UIElement), typeof(Reveal), new PropertyMetadata(null));

    public static UIElement? GetMotionTarget(DependencyObject element) => (UIElement?)element.GetValue(MotionTargetProperty);

    public static void SetMotionTarget(DependencyObject element, UIElement? value) => element.SetValue(MotionTargetProperty, value);

    private static void OnIsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var show = e.NewValue as bool? == true;
        if (!element.IsLoaded)
        {
            // Initial state: no animation.
            element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            element.Opacity = show ? 1 : 0;
            return;
        }

        var target = GetMotionTarget(element) ?? element;
        var transform = EnsureTransform(target);
        var offset = GetOffset(element);
        var scale = GetScale(element);

        if (show)
        {
            element.Visibility = Visibility.Visible;
            element.IsHitTestVisible = true;
            Animate(element, UIElement.OpacityProperty, 0, 1, InDuration);
            Animate(transform.Children[1], TranslateTransform.YProperty, offset, 0, InDuration);
            Animate(transform.Children[0], ScaleTransform.ScaleXProperty, scale, 1, InDuration);
            Animate(transform.Children[0], ScaleTransform.ScaleYProperty, scale, 1, InDuration);
        }
        else
        {
            element.IsHitTestVisible = false;
            var fade = Animate(element, UIElement.OpacityProperty, element.Opacity, 0, OutDuration);
            Animate(transform.Children[1], TranslateTransform.YProperty, 0, offset / 2, OutDuration);
            Animate(transform.Children[0], ScaleTransform.ScaleXProperty, 1, 1 - (1 - scale) / 2, OutDuration);
            Animate(transform.Children[0], ScaleTransform.ScaleYProperty, 1, 1 - (1 - scale) / 2, OutDuration);
            fade.Completed += (_, _) =>
            {
                if (GetIsShown(element) != true)
                {
                    element.Visibility = Visibility.Collapsed;
                }
            };
        }
    }

    private static TransformGroup EnsureTransform(UIElement target)
    {
        if (target.RenderTransform is TransformGroup { Children.Count: 2 } existing && existing.Children[0] is ScaleTransform && existing.Children[1] is TranslateTransform)
        {
            return existing;
        }

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(new TranslateTransform());
        target.RenderTransform = group;
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        return group;
    }

    private static DoubleAnimation Animate(DependencyObject target, DependencyProperty property, double from, double to, Duration duration)
    {
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = EaseOut };
        ((IAnimatable)target).BeginAnimation(property, animation);
        return animation;
    }
}
