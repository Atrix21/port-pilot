using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortPilot.App.ViewModels;
using PortPilot.Linux.Controls;

namespace PortPilot.Linux.Views;

public sealed partial class MainWindow : Window
{
    private static readonly string[] HeaderTitles = ["Port", "Process", "PID", "Protocol", "Address", "Status"];

    private readonly MainViewModel _vm;
    private int _refreshTurns;

    // Parameterless constructor for the XAML previewer.
    public MainWindow() : this(null!)
    {
    }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        if (vm is null)
        {
            return;
        }

        RestorePlacement();
        BuildHeaders();
        BuildSkeleton();
        BuildContextMenu();

        vm.PropertyChanged += OnViewModelChanged;
        RefreshButton.Click += (_, _) => RefreshGlyph.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse($"rotate({++_refreshTurns * 360}deg)");
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
        Opened += (_, _) => SearchInput.Focus();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SortColumn) or nameof(MainViewModel.SortDescending):
                UpdateHeaders();
                break;
            case nameof(MainViewModel.IsInspectorOpen):
                InspectorHost.Width = _vm.IsInspectorOpen ? 372 : 0;
                break;
            case nameof(MainViewModel.SelectedRow) when _vm.SelectedRow is { } row:
                Table.ScrollIntoView(row);
                break;
            case nameof(MainViewModel.Pulse):
                // Restart the ripple on the live indicator.
                LiveRipple.Classes.Remove("active");
                Dispatcher.UIThread.Post(() => LiveRipple.Classes.Add("active"), DispatcherPriority.Background);
                break;
            case nameof(MainViewModel.IsDialogOpen) when _vm.IsDialogOpen:
                Dispatcher.UIThread.Post(() => DialogCancel.Focus(), DispatcherPriority.Background);
                break;
        }
    }

    // ═════════════ Keyboard ═════════════

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape:
                _vm.DismissCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F when ctrl:
                SearchInput.Focus();
                SearchInput.SelectAll();
                e.Handled = true;
                break;
            case Key.C when ctrl && !(SearchInput.IsFocused && SearchInput.SelectionEnd != SearchInput.SelectionStart) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (_vm.SelectedRow is { } row)
                {
                    _vm.CopyDetailsCommand.Execute(row);
                    e.Handled = true;
                }

                break;
            case Key.Down or Key.Up when SearchInput.IsFocused && !_vm.IsDialogOpen:
                // Arrow keys in the search box move through the results.
                MoveSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Enter when SearchInput.IsFocused && !ctrl && _vm.RowsView.Count > 0:
                _vm.SelectedRow ??= _vm.RowsView[0];
                Table.ContainerFromItem(_vm.SelectedRow)?.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Typing anywhere starts a search.</summary>
    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (SearchInput.IsFocused || _vm.IsDialogOpen || _vm.IsSettingsOpen || string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        SearchInput.Focus();
        SearchInput.Text += e.Text;
        SearchInput.CaretIndex = SearchInput.Text?.Length ?? 0;
        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        var rows = _vm.RowsView;
        if (rows.Count == 0)
        {
            return;
        }

        var index = _vm.SelectedRow is { } current ? rows.IndexOf(current) : -1;
        _vm.SelectedRow = rows[Math.Clamp(index + delta, 0, rows.Count - 1)];
    }

    // ═════════════ Row actions ═════════════

    private static PortRowViewModel? RowOf(object? sender) => (sender as StyledElement)?.DataContext as PortRowViewModel;

    private void OnRowOpen(object? sender, RoutedEventArgs e) => Execute(_vm.OpenInBrowserCommand, RowOf(sender));

    private void OnRowCopy(object? sender, RoutedEventArgs e) => Execute(_vm.CopyDetailsCommand, RowOf(sender));

    private void OnRowTerminate(object? sender, RoutedEventArgs e) => Execute(_vm.TerminateCommand, RowOf(sender));

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => Execute(_vm.OpenInBrowserCommand, RowOf(sender));

    private static void Execute(System.Windows.Input.ICommand command, PortRowViewModel? row)
    {
        if (row is not null && command.CanExecute(row))
        {
            command.Execute(row);
        }
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            if (_vm.SelectedRow is not { } row)
            {
                e.Cancel = true;
                return;
            }

            menu.Items.Clear();
            Add(menu, $"Open {row.UrlDisplay}", "Icon.External", _vm.OpenInBrowserCommand, row, "Ctrl+Enter");
            Add(menu, "Inspect process", "Icon.Info", _vm.InspectCommand, row, "Enter");
            menu.Items.Add(new Separator());
            Add(menu, "Copy URL", "Icon.Globe", _vm.CopyUrlCommand, row, "Ctrl+Shift+C");
            Add(menu, "Copy port", "Icon.Hash", _vm.CopyPortCommand, row);
            Add(menu, "Copy PID", "Icon.Cpu", _vm.CopyPidCommand, row);
            Add(menu, "Copy command line", "Icon.Terminal", _vm.CopyCommandLineCommand, row);
            Add(menu, "Copy details", "Icon.Copy", _vm.CopyDetailsCommand, row, "Ctrl+C");
            menu.Items.Add(new Separator());
            Add(menu, "Show in file manager", "Icon.Folder", _vm.RevealCommand, row, "Ctrl+E");
            menu.Items.Add(new Separator());
            Add(menu, "Terminate process…", "Icon.Power", _vm.TerminateCommand, row, "Del");
        };
        Table.ContextMenu = menu;

        // Right-click selects the row under the pointer before the menu opens.
        Table.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Table).Properties.IsRightButtonPressed && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: PortRowViewModel row })
            {
                _vm.SelectedRow = row;
            }
        }, RoutingStrategies.Tunnel);
    }

    private void Add(ContextMenu menu, string header, string icon, System.Windows.Input.ICommand command, PortRowViewModel row, string? gesture = null)
    {
        menu.Items.Add(new MenuItem
        {
            Header = header,
            Icon = new Icon { Data = this.FindResource(icon) as Geometry, Size = 15 },
            Command = command,
            CommandParameter = row,
            InputGesture = gesture is null ? null : KeyGesture.Parse(gesture),
            IsEnabled = command.CanExecute(row),
        });
    }

    // ═════════════ Layout pieces ═════════════

    private void BuildHeaders()
    {
        var buttons = HeaderRow.Children.OfType<Button>().ToList();
        for (var i = 0; i < buttons.Count; i++)
        {
            buttons[i].Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = HeaderTitles[i], VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                    new Icon { Size = 12, Classes = { "arrow" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                },
            };
        }

        UpdateHeaders();
    }

    private void UpdateHeaders()
    {
        foreach (var button in HeaderRow.Children.OfType<Button>())
        {
            var sorted = (string?)button.Tag == _vm.SortColumn.ToString();
            button.Classes.Set("sorted", sorted);
            if (button.Content is StackPanel { Children: [_, Icon arrow] })
            {
                arrow.Data = this.FindResource(_vm.SortDescending ? "Icon.ArrowDown" : "Icon.ArrowUp") as Geometry;
            }
        }
    }

    private void BuildSkeleton()
    {
        double[] widths = [0.5, 0.8, 0.65, 0.9, 0.55, 0.75];
        foreach (var w in widths)
        {
            var row = new Grid { Height = 50, ColumnDefinitions = new ColumnDefinitions("110,*,88,116,190,132") };
            row.Children.Add(new Border { Classes = { "skeleton" }, Width = 46 * w + 20, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var process = new Border { Classes = { "skeleton" }, Width = 220 * w, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(process, 1);
            row.Children.Add(process);
            var pid = new Border { Classes = { "skeleton" }, Width = 44, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(pid, 2);
            row.Children.Add(pid);
            var address = new Border { Classes = { "skeleton" }, Width = 110 * w, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(address, 4);
            row.Children.Add(address);
            SkeletonRows.Children.Add(row);
        }
    }

    private void RestorePlacement()
    {
        if (_vm.CurrentSettings.Window is not { } placement || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }

        Width = placement.Width;
        Height = placement.Height;
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnProjectClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as StyledElement)?.DataContext is ProjectGroupViewModel project)
        {
            _vm.ShowProjectCommand.Execute(project);
        }
    }

    private void OnProjectCleanUp(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as StyledElement)?.DataContext is ProjectGroupViewModel project && _vm.CleanUpOrphansCommand.CanExecute(project))
        {
            _vm.CleanUpOrphansCommand.Execute(project);
        }
    }

    private void OnSettingsScrimPressed(object? sender, PointerPressedEventArgs e) => _vm.IsSettingsOpen = false;

    private void OnShortcutsScrimPressed(object? sender, PointerPressedEventArgs e) => _vm.IsShortcutsOpen = false;
}
