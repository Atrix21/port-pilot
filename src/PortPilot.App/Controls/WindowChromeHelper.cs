using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PortPilot.App.Native;

namespace PortPilot.App.Controls;

/// <summary>
/// Integrates a WPF window that draws its own title bar with Windows 11: Snap Layouts on the maximize
/// button, rounded corners, theme-aware DWM border and correct insets when maximized.
/// </summary>
public sealed class WindowChromeHelper
{
    public static readonly DependencyProperty IsNonClientHoverProperty = DependencyProperty.RegisterAttached(
        "IsNonClientHover", typeof(bool), typeof(WindowChromeHelper), new FrameworkPropertyMetadata(false));

    public static bool GetIsNonClientHover(DependencyObject element) => (bool)element.GetValue(IsNonClientHoverProperty);

    public static void SetIsNonClientHover(DependencyObject element, bool value) => element.SetValue(IsNonClientHoverProperty, value);

    public static readonly DependencyProperty IsNonClientPressedProperty = DependencyProperty.RegisterAttached(
        "IsNonClientPressed", typeof(bool), typeof(WindowChromeHelper), new FrameworkPropertyMetadata(false));

    public static bool GetIsNonClientPressed(DependencyObject element) => (bool)element.GetValue(IsNonClientPressedProperty);

    public static void SetIsNonClientPressed(DependencyObject element, bool value) => element.SetValue(IsNonClientPressedProperty, value);

    private readonly Window _window;
    private readonly Button _maximizeButton;
    private HwndSource? _source;

    private WindowChromeHelper(Window window, Button maximizeButton)
    {
        _window = window;
        _maximizeButton = maximizeButton;
        _window.SourceInitialized += OnSourceInitialized;
        _window.StateChanged += (_, _) => UpdateMaximizedInset();
        _window.DpiChanged += (_, _) => UpdateMaximizedInset();
    }

    /// <summary>Raised whenever the inset needed to keep content on-screen while maximized changes.</summary>
    public event Action<Thickness>? MaximizedInsetChanged;

    public nint Handle => _source?.Handle ?? 0;

    public static WindowChromeHelper Attach(Window window, Button maximizeButton) => new(window, maximizeButton);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(_window)!;
        _source.AddHook(WndProc);
        Win32.SetDwmInt(_source.Handle, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_ROUND);
        UpdateMaximizedInset();
    }

    /// <summary>Matches the DWM frame to the active theme.</summary>
    public void ApplyTheme(bool dark, Color border)
    {
        if (_source is null)
        {
            return;
        }

        Win32.SetDwmInt(_source.Handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
        Win32.SetDwmInt(_source.Handle, Win32.DWMWA_BORDER_COLOR, border.R | (border.G << 8) | (border.B << 16));
    }

    private void UpdateMaximizedInset()
    {
        if (_source is null)
        {
            return;
        }

        var inset = new Thickness(0);
        if (_window.WindowState == WindowState.Maximized)
        {
            // A maximized window extends past the monitor edge by the resize frame; pad it back in.
            var dpi = Win32.GetDpiForWindow(_source.Handle);
            var px = Win32.GetSystemMetricsForDpi(Win32.SM_CXSIZEFRAME, dpi) + Win32.GetSystemMetricsForDpi(Win32.SM_CXPADDEDBORDER, dpi);
            var dip = px * 96.0 / dpi;
            inset = new Thickness(dip);
        }

        MaximizedInsetChanged?.Invoke(inset);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                if (IsOverMaximizeButton(lParam))
                {
                    handled = true;
                    SetIsNonClientHover(_maximizeButton, true);
                    return Win32.HTMAXBUTTON;
                }

                ClearHover();
                break;

            case Win32.WM_NCMOUSEMOVE:
                if (wParam != Win32.HTMAXBUTTON)
                {
                    ClearHover();
                }

                break;

            case Win32.WM_NCMOUSELEAVE:
            case Win32.WM_MOUSEMOVE:
                ClearHover();
                break;

            case Win32.WM_NCLBUTTONDOWN:
            case Win32.WM_NCLBUTTONDBLCLK:
                if (wParam == Win32.HTMAXBUTTON)
                {
                    SetIsNonClientPressed(_maximizeButton, true);
                    handled = true;
                }

                break;

            case Win32.WM_NCLBUTTONUP:
                if (wParam == Win32.HTMAXBUTTON)
                {
                    SetIsNonClientPressed(_maximizeButton, false);
                    handled = true;
                    _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }

                break;
        }

        return 0;
    }

    private void ClearHover()
    {
        if (GetIsNonClientHover(_maximizeButton))
        {
            SetIsNonClientHover(_maximizeButton, false);
        }

        if (GetIsNonClientPressed(_maximizeButton))
        {
            SetIsNonClientPressed(_maximizeButton, false);
        }
    }

    private bool IsOverMaximizeButton(nint lParam)
    {
        if (!_maximizeButton.IsVisible)
        {
            return false;
        }

        var x = (short)(lParam & 0xFFFF);
        var y = (short)((lParam >> 16) & 0xFFFF);
        try
        {
            var point = _maximizeButton.PointFromScreen(new Point(x, y));
            return point.X >= 0 && point.Y >= 0 && point.X < _maximizeButton.ActualWidth && point.Y < _maximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
