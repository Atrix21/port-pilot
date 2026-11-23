using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PortPilot.App.ViewModels;
using PortPilot.Core.Settings;

namespace PortPilot.Linux.Views;

/// <summary>Drives the window through its main states and renders each to a PNG.</summary>
internal static class ScreenshotRunner
{
    public static async Task RunAsync(MainWindow window, MainViewModel vm, string directory)
    {
        Directory.CreateDirectory(directory);
        await Task.Delay(2500);

        foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
        {
            var suffix = theme.ToString().ToLowerInvariant();
            vm.UpdateSettings(s => s with { Theme = theme });
            vm.SelectedRow = null;
            await Task.Delay(600);
            Save(window, Path.Combine(directory, $"main-{suffix}.png"));

            vm.SelectedRow = vm.RowsView.FirstOrDefault(r => r.HasExecutablePath) ?? vm.RowsView.FirstOrDefault();
            await Task.Delay(700);
            Save(window, Path.Combine(directory, $"inspector-{suffix}.png"));

            vm.IsSettingsOpen = true;
            await Task.Delay(600);
            Save(window, Path.Combine(directory, $"settings-{suffix}.png"));
            vm.IsSettingsOpen = false;
            await Task.Delay(300);
        }
    }

    private static void Save(MainWindow window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
        using var file = File.Create(path);
#pragma warning disable CS0618 // The replacement overload's options type differs between Avalonia 12 minors.
        bitmap.Save(file);
#pragma warning restore CS0618
    }
}
