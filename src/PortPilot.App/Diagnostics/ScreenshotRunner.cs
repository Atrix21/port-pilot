using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PortPilot.App.Services;
using PortPilot.App.ViewModels;
using PortPilot.App.Views;
using PortPilot.Core.Settings;

namespace PortPilot.App.Diagnostics;

/// <summary>
/// Renders the window into PNGs at several sizes, themes and states (<c>--screenshot &lt;dir&gt;</c>).
/// Used for visual QA and for the README. Runs against <see cref="DemoPortScanner"/> so the output is stable.
/// </summary>
internal sealed class ScreenshotRunner(MainWindow window, MainViewModel viewModel, ThemeService theme, string directory)
{
    private const double Scale = 2;

    public async Task RunAsync()
    {
        Directory.CreateDirectory(directory);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 40;
        window.Top = 40;
        window.ShowActivated = false;
        Resize(1280, 800);
        window.Show();

        theme.Apply(ThemePreference.Dark, animate: false);
        await Settle(400);
        await Capture("discovering-dark");

        viewModel.Start();
        await WaitFor(() => viewModel.Rows.Any(r => r.IsClosed), TimeSpan.FromSeconds(10));
        await Settle(900);

        foreach (var dark in new[] { true, false })
        {
            var suffix = dark ? "dark" : "light";
            theme.Apply(dark ? ThemePreference.Dark : ThemePreference.Light, animate: false);
            viewModel.SelectedRow = null;
            Resize(1280, 800);
            await Settle(600);
            await Capture($"main-{suffix}");

            viewModel.SelectedRow = viewModel.Rows.First(r => r.Port == 5173);
            await Settle(700);
            await Capture($"inspector-{suffix}");

            viewModel.SelectedRow = viewModel.Rows.First(r => r.Port == 135);
            await Settle(700);
            await Capture($"inspector-restricted-{suffix}");

            viewModel.SelectedRow = viewModel.Rows.First(r => r.Port == 5432);
            Resize(1600, 1000);
            await Settle(700);
            await Capture($"wide-{suffix}");

            viewModel.SelectedRow = null;
            Resize(900, 620);
            await Settle(700);
            await Capture($"compact-{suffix}");

            Resize(1280, 800);
            viewModel.SearchText = "redis-x";
            await Settle(700);
            await Capture($"no-results-{suffix}");
            viewModel.SearchText = "";
            await Settle(300);

            viewModel.SearchText = "node";
            await Settle(700);
            await Capture($"search-{suffix}");
            viewModel.SearchText = "";

            viewModel.IsSettingsOpen = true;
            await Settle(700);
            await Capture($"settings-{suffix}");
            viewModel.IsSettingsOpen = false;

            viewModel.IsShortcutsOpen = true;
            await Settle(700);
            await Capture($"shortcuts-{suffix}");
            viewModel.IsShortcutsOpen = false;

            var row = viewModel.Rows.First(r => r.Port == 3000);
            viewModel.SelectedRow = row;
            viewModel.TerminateCommand.Execute(row);
            await Settle(700);
            await Capture($"terminate-{suffix}");
            viewModel.DismissCommand.Execute(null);
            await Settle(400);

            viewModel.ShowToast("Opened localhost:5173", ToastKind.Success);
            await Settle(700);
            await Capture($"toast-{suffix}");
            viewModel.SelectedRow = null;
            await Settle(3500);
        }
    }

    private void Resize(double width, double height)
    {
        window.Width = width;
        window.Height = height;
    }

    private static async Task Settle(int milliseconds)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }

    private Task Capture(string name)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(root.ActualWidth * Scale),
            (int)Math.Round(root.ActualHeight * Scale),
            96 * Scale,
            96 * Scale,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"{name}.png"));
        encoder.Save(stream);
        return Task.CompletedTask;
    }
}
