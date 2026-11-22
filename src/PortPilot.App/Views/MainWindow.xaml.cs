using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PortPilot.App.Controls;
using PortPilot.App.Native;
using PortPilot.App.Services;
using PortPilot.App.ViewModels;
using PortPilot.Core.Settings;

namespace PortPilot.App.Views;

/// <summary>
/// The main window. Everything here is presentation: chrome integration, motion and focus management.
/// Behaviour lives in <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window, IThemeTransitionHost
{
    private const double InspectorWidth = 372;

    private static readonly IEasingFunction EaseOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    private readonly MainViewModel _viewModel;
    private readonly ThemeService _theme;
    private readonly WindowChromeHelper _chrome;
    private readonly TableLayout _layout;

    public MainWindow(MainViewModel viewModel, ThemeService theme)
    {
        _viewModel = viewModel;
        _theme = theme;
        InitializeComponent();
        DataContext = viewModel;
        _layout = (TableLayout)FindResource("Layout");

        _chrome = WindowChromeHelper.Attach(this, MaximizeButton);
        _chrome.MaximizedInsetChanged += inset => Root.Margin = inset;
        _theme.ThemeChanged += (_, _) => ApplyChromeTheme();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewTextInput += OnWindowPreviewTextInput;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        InputBindings.Add(new KeyBinding(_viewModel.DismissCommand, Key.Escape, ModifierKeys.None));
        RestorePlacement(viewModel.CurrentSettings.Window);
    }

    private static App? Host => Application.Current as App;

    // ═════════════ Chrome ═════════════

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyChromeTheme();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SettingsHook);
    }

    private nint SettingsHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WM_SETTINGCHANGE && lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            _theme.OnSystemThemeChanged();
        }

        return 0;
    }

    private void ApplyChromeTheme()
    {
        var border = TryFindResource("WindowBorderColor") is Color color ? color : Colors.Gray;
        _chrome.ApplyTheme(_theme.IsDark, border);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        AutomationPropertiesHelper.SetName(MaximizeButton, WindowState == WindowState.Maximized ? "Restore" : "Maximize");

        if (WindowState == WindowState.Minimized && _viewModel.CurrentSettings.MinimizeToTray)
        {
            Host?.HideMainWindow();
            WindowState = WindowState.Normal;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        var host = Host;
        if (host is not null && !host.IsQuitting)
        {
            if (_viewModel.CurrentSettings.CloseToTray)
            {
                e.Cancel = true;
                host.HideMainWindow();
                _viewModel.OnClosedToTray();
                return;
            }

            Dispatcher.InvokeAsync(host.Quit);
        }

        base.OnClosing(e);
    }

    // ═════════════ Placement ═════════════

    private void RestorePlacement(WindowPlacementSettings? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }

        // Only restore if the window would be meaningfully visible on the current monitor layout.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        var visible = Rect.Intersect(screen, bounds);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 120)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        if (Host?.Options.IsScreenshotRun == true)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || double.IsInfinity(bounds.Width))
        {
            return;
        }

        _viewModel.UpdateSettings(s => s with
        {
            Window = new WindowPlacementSettings(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized),
        });
    }

    // ═════════════ Motion ═════════════

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildSkeleton();
        if (_viewModel.IsInspectorOpen)
        {
            InspectorHost.Width = InspectorWidth;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsInspectorOpen):
                AnimateInspector(_viewModel.IsInspectorOpen);
                break;
            case nameof(MainViewModel.SelectedRow) when _viewModel.SelectedRow is not null:
                PulseInspectorContent();
                if (Table.IsKeyboardFocusWithin)
                {
                    // Keep keyboard focus on the selected row so no stale focus ring is left behind.
                    FocusRow(_viewModel.SelectedRow);
                }
                else
                {
                    Table.ScrollIntoView(_viewModel.SelectedRow);
                }
                break;
            case nameof(MainViewModel.SelectedRow) when Table.IsKeyboardFocusWithin:
                // Nothing selected: park focus on the list itself rather than on the last row.
                Table.Focus();
                break;
            case nameof(MainViewModel.Pulse):
                PulseLiveIndicator();
                break;
            case nameof(MainViewModel.EmptyState) when _viewModel.EmptyState != EmptyState.Discovering:
                SkeletonRows.BeginAnimation(OpacityProperty, null);
                break;
            case nameof(MainViewModel.IsDialogOpen) when _viewModel.IsDialogOpen:
                Dispatcher.InvokeAsync(() => DialogCancel.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                break;
            case nameof(MainViewModel.IsDialogOpen):
            case nameof(MainViewModel.IsSettingsOpen) when !_viewModel.IsSettingsOpen:
            case nameof(MainViewModel.IsShortcutsOpen) when !_viewModel.IsShortcutsOpen:
                RestoreTableFocus();
                break;
            case nameof(MainViewModel.ListeningOnly):
                PageTitle.Text = _viewModel.ListeningOnly ? "Listening ports" : "All endpoints";
                break;
        }
    }

    private void AnimateInspector(bool open)
    {
        var width = new DoubleAnimation(open ? InspectorWidth : 0, open ? TimeSpan.FromMilliseconds(260) : TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = open ? EaseOut : EaseInOut,
        };
        InspectorHost.BeginAnimation(WidthProperty, width);

        var fade = new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 240 : 140));
        Inspector.BeginAnimation(OpacityProperty, fade);
    }

    private void PulseInspectorContent()
    {
        if (InspectorHost.ActualWidth < InspectorWidth - 1)
        {
            return;
        }

        // A short settle when switching between rows so the change of subject reads clearly.
        var transform = Inspector.RenderTransform as TranslateTransform ?? new TranslateTransform();
        Inspector.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseOut });
        Inspector.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(200)));
    }

    private void PulseLiveIndicator()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _viewModel.IsPaused)
        {
            return;
        }

        if (LiveButton.Template.FindName("LiveRipple", LiveButton) is not System.Windows.Shapes.Ellipse ripple)
        {
            return;
        }

        // Template transforms are frozen; give the ripple its own animatable one.
        if (ripple.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            scale = new ScaleTransform();
            ripple.RenderTransform = scale;
        }

        // A heartbeat on every scan. Capped at 30 fps: it is tiny, and this keeps idle CPU near zero.
        var duration = TimeSpan.FromMilliseconds(700);
        var fade = new DoubleAnimation(0.55, 0, duration);
        Timeline.SetDesiredFrameRate(fade, 30);
        ripple.BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation(1, 2.6, duration) { EasingFunction = EaseOut };
        Timeline.SetDesiredFrameRate(grow, 30);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void BuildSkeleton()
    {
        double[] processWidths = [148, 112, 172, 96, 136, 120, 156];
        for (var i = 0; i < processWidths.Length; i++)
        {
            var row = new Grid { Height = 50, Opacity = 1 - i * 0.11 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });

            row.Children.Add(Bar(38, 0));
            var process = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            process.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = (Brush)FindResource("Skeleton") });
            var lines = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(Bar(processWidths[i] * 0.55, null));
            var detail = Bar(processWidths[i], null);
            detail.Height = 7;
            detail.Margin = new Thickness(0, 7, 0, 0);
            lines.Children.Add(detail);
            process.Children.Add(lines);
            Grid.SetColumn(process, 1);
            row.Children.Add(process);
            row.Children.Add(Bar(44, 2));
            row.Children.Add(Bar(64, 3));
            SkeletonRows.Children.Add(row);
        }

        var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseInOut };
        if (_viewModel.EmptyState == EmptyState.Discovering)
        {
            SkeletonRows.BeginAnimation(OpacityProperty, pulse);
        }

        Border Bar(double width, int? column)
        {
            var bar = new Border { Width = width, Style = (Style)FindResource("SkeletonBar"), VerticalAlignment = VerticalAlignment.Center };
            if (column is { } c)
            {
                Grid.SetColumn(bar, c);
            }

            return bar;
        }
    }

    /// <summary>Cross-fades from a snapshot of the old theme to the new one.</summary>
    public void RunThemeTransition(Action swap)
    {
        if (!IsVisible || Root.ActualWidth < 1)
        {
            swap();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(Root);
        bitmap.Freeze();

        ThemeSnapshot.Source = bitmap;
        ThemeSnapshot.Opacity = 1;
        ThemeSnapshot.Visibility = Visibility.Visible;

        swap();

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = EaseInOut };
        fade.Completed += (_, _) =>
        {
            ThemeSnapshot.Visibility = Visibility.Collapsed;
            ThemeSnapshot.Source = null;
        };
        ThemeSnapshot.BeginAnimation(OpacityProperty, fade);
    }

    // ═════════════ Layout ═════════════

    private void OnTableSizeChanged(object sender, SizeChangedEventArgs e) => _layout.Fit(e.NewSize.Width - 56);

    // ═════════════ Keyboard ═════════════

    private bool IsOverlayOpen => _viewModel.IsDialogOpen || _viewModel.IsSettingsOpen || _viewModel.IsShortcutsOpen;

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsOverlayOpen)
        {
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearch();
            e.Handled = true;
        }
    }

    private void OnWindowPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Type anywhere to search.
        if (IsOverlayOpen || Keyboard.FocusedElement is TextBox || string.IsNullOrEmpty(e.Text) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        var c = e.Text[0];
        if (!char.IsLetterOrDigit(c) && c is not ':' and not '/' and not '.')
        {
            return;
        }

        FocusSearch();
        if (c != '/')
        {
            _viewModel.SearchText += e.Text;
            SearchInput.CaretIndex = SearchInput.Text.Length;
        }

        e.Handled = true;
    }

    private void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter && Table.Items.Count > 0)
        {
            var target = _viewModel.SelectedRow ?? Table.Items[0];
            _viewModel.SelectedRow = (PortRowViewModel)target;
            FocusRow(target);
            e.Handled = true;
        }
    }

    private void OnTableKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.OriginalSource as FrameworkElement)?.DataContext is PortRowViewModel row)
        {
            _viewModel.SelectedRow = row;
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Table.SelectedIndex <= 0)
        {
            FocusSearch();
            e.Handled = true;
        }
    }

    private void FocusRow(object item)
    {
        Table.ScrollIntoView(item);
        Table.UpdateLayout();
        (Table.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
    }

    private void RestoreTableFocus()
    {
        if (IsOverlayOpen)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (_viewModel.SelectedRow is { } row)
            {
                FocusRow(row);
            }
            else
            {
                Table.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag)
        {
            case "Settings":
                _viewModel.IsSettingsOpen = false;
                break;
            case "Shortcuts":
                _viewModel.IsShortcutsOpen = false;
                break;
        }
    }
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
