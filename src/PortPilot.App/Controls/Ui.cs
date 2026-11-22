using System.Windows;
using System.Windows.Media;

namespace PortPilot.App.Controls;

/// <summary>
/// Attached properties consumed by PortPilot's control templates.
/// </summary>
public static class Ui
{
    /// <summary>Icon geometry shown by button, menu item and chip templates.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached(
        "IconSize", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetIconSize(DependencyObject element) => (double)element.GetValue(IconSizeProperty);

    public static void SetIconSize(DependencyObject element, double value) => element.SetValue(IconSizeProperty, value);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(7)));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    /// <summary>Secondary text, e.g. the keyboard hint shown at the trailing edge of a menu item.</summary>
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetHint(DependencyObject element) => (string?)element.GetValue(HintProperty);

    public static void SetHint(DependencyObject element, string? value) => element.SetValue(HintProperty, value);

    /// <summary>Marks a control as destructive so templates can switch to the negative palette.</summary>
    public static readonly DependencyProperty IsDestructiveProperty = DependencyProperty.RegisterAttached(
        "IsDestructive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetIsDestructive(DependencyObject element) => (bool)element.GetValue(IsDestructiveProperty);

    public static void SetIsDestructive(DependencyObject element, bool value) => element.SetValue(IsDestructiveProperty, value);
}
