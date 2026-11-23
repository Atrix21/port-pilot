using Avalonia.Controls;

namespace PortPilot.Desktop.Views;

public sealed record Shortcut(string Description, params string[] Keys);

public sealed record ShortcutGroup(string Title, IReadOnlyList<Shortcut> Items);

public sealed partial class ShortcutsView : UserControl
{
    public ShortcutsView()
    {
        InitializeComponent();

        LeftColumn.ItemsSource = new[]
        {
            new ShortcutGroup("Find", [
                new Shortcut("Search (or just type)", "Ctrl", "F"),
                new Shortcut("Clear search / close", "Esc"),
                new Shortcut("All protocols", "Ctrl", "1"),
                new Shortcut("TCP only", "Ctrl", "2"),
                new Shortcut("UDP only", "Ctrl", "3"),
                new Shortcut("Listening only", "Ctrl", "L"),
                new Shortcut("Dev ports only", "Ctrl", "D"),
            ]),
            new ShortcutGroup("Monitoring", [
                new Shortcut("Refresh now", "F5"),
                new Shortcut("Pause or resume", "Ctrl", "P"),
            ]),
        };

        RightColumn.ItemsSource = new[]
        {
            new ShortcutGroup("Selected port", [
                new Shortcut("Open in browser", "Ctrl", "Enter"),
                new Shortcut("Copy details", "Ctrl", "C"),
                new Shortcut("Copy URL", "Ctrl", "Shift", "C"),
                new Shortcut("Show in file manager", "Ctrl", "E"),
                new Shortcut("Terminate process", "Del"),
            ]),
            new ShortcutGroup("App", [
                new Shortcut("Settings", "Ctrl", ","),
                new Shortcut("Switch theme", "Ctrl", "T"),
                new Shortcut("This overview", "Ctrl", "/"),
                new Shortcut("Quit PortPilot", "Ctrl", "Q"),
            ]),
        };
    }
}
