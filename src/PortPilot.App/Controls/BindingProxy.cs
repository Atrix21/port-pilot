using System.Windows;

namespace PortPilot.App.Controls;

/// <summary>
/// Exposes a DataContext to elements outside the visual tree, such as context menus.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>
/// Column widths shared by the table header and every row. The window narrows the table gracefully by
/// collapsing secondary columns rather than squeezing every column at once.
/// </summary>
public sealed class TableLayout : DependencyObject
{
    public static readonly DependencyProperty PortWidthProperty = Register(nameof(PortWidth), 112);
    public static readonly DependencyProperty PidWidthProperty = Register(nameof(PidWidth), 84);
    public static readonly DependencyProperty ProtocolWidthProperty = Register(nameof(ProtocolWidth), 108);
    public static readonly DependencyProperty AddressWidthProperty = Register(nameof(AddressWidth), 176);
    public static readonly DependencyProperty StatusWidthProperty = Register(nameof(StatusWidth), 136);

    public GridLength PortWidth
    {
        get => (GridLength)GetValue(PortWidthProperty);
        set => SetValue(PortWidthProperty, value);
    }

    public GridLength PidWidth
    {
        get => (GridLength)GetValue(PidWidthProperty);
        set => SetValue(PidWidthProperty, value);
    }

    public GridLength ProtocolWidth
    {
        get => (GridLength)GetValue(ProtocolWidthProperty);
        set => SetValue(ProtocolWidthProperty, value);
    }

    public GridLength AddressWidth
    {
        get => (GridLength)GetValue(AddressWidthProperty);
        set => SetValue(AddressWidthProperty, value);
    }

    public GridLength StatusWidth
    {
        get => (GridLength)GetValue(StatusWidthProperty);
        set => SetValue(StatusWidthProperty, value);
    }

    /// <summary>Adapts columns to the width available for the table.</summary>
    public void Fit(double width)
    {
        // Process name keeps at least ~220px; secondary columns give way in order of importance.
        AddressWidth = new GridLength(width < 860 ? 0 : 176);
        ProtocolWidth = new GridLength(width < 700 ? 0 : 108);
        PidWidth = new GridLength(width < 600 ? 0 : 84);
    }

    private static DependencyProperty Register(string name, double width) =>
        DependencyProperty.Register(name, typeof(GridLength), typeof(TableLayout), new PropertyMetadata(new GridLength(width)));
}
