using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text;
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
/// Plays a scripted tour of the app and saves timed frames (<c>--record &lt;dir&gt;</c>). The folder ends up
/// with numbered PNGs and an ffmpeg concat list (<c>frames.txt</c>) that carries each frame's real duration,
/// so the resulting animation keeps the app's actual timing. Runs against <see cref="DemoPortScanner"/>.
/// </summary>
internal sealed partial class RecordingRunner(MainWindow window, MainViewModel viewModel, ThemeService theme, string directory)
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 20);
    private nint _hwnd;

    private readonly List<(string File, TimeSpan At)> _frames = [];
    private readonly List<Task> _writes = [];
    private readonly Stopwatch _clock = new();

    public async Task RunAsync()
    {
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, "f*.png"))
        {
            File.Delete(old);
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 40;
        window.Top = 40;
        window.Width = 1120;
        window.Height = 700;
        window.ShowActivated = false;
        theme.Apply(ThemePreference.Dark, animate: false);
        window.Show();
        await Wait(300);

        // Capture on a background thread from the window's composited surface, so recording never
        // slows the UI it is recording. PrintWindow only ever reads this window, not the screen.
        _hwnd = new WindowInteropHelper(window).Handle;
        using var stop = new CancellationTokenSource();
        _clock.Start();
        var capture = Task.Run(async () =>
        {
            using var ticker = new PeriodicTimer(FrameInterval);
            do
            {
                CaptureFrame();
            }
            while (await ticker.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
        });

        // 1. Loading, then the first scan, a new dev server appearing and a closed one fading.
        await Wait(500);
        viewModel.Start();
        await Wait(3200);

        // 2. Search by typing.
        foreach (var c in "vite")
        {
            viewModel.SearchText += c;
            await Wait(150);
        }

        await Wait(900);

        // 3. Inspect.
        viewModel.SelectedRow = viewModel.Rows.First(r => r.Port == 5173 && !r.IsIPv6);
        await Wait(2600);

        // 4. Back to everything, then terminate the Next.js server.
        viewModel.SelectedRow = null;
        viewModel.SearchText = "";
        await Wait(1100);
        var next = viewModel.Rows.First(r => r.Port == 3000);
        viewModel.SelectedRow = next;
        await Wait(1300);
        viewModel.TerminateCommand.Execute(next);
        await Wait(1800);
        viewModel.Dialog?.ConfirmCommand.Execute(null);
        await Wait(2600);

        // 5. Light theme, with the cross-fade.
        viewModel.SelectedRow = null;
        await Wait(700);
        viewModel.ToggleThemeCommand.Execute(null);
        await Wait(2600);

        await stop.CancelAsync();
        try
        {
            await capture;
        }
        catch (OperationCanceledException)
        {
        }

        Task[] writes;
        lock (_frames)
        {
            writes = [.. _writes];
        }

        await Task.WhenAll(writes);
        WriteConcatList();
    }

    private unsafe void CaptureFrame()
    {
        if (!GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var info = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var memoryDc = CreateCompatibleDC(0);
        var dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, 0, 0);
        var previous = SelectObject(memoryDc, dib);
        try
        {
            if (!PrintWindow(_hwnd, memoryDc, PW_RENDERFULLCONTENT))
            {
                return;
            }

            var stride = width * 4;
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bits, stride * height, stride);
            bitmap.Freeze();

            string file;
            lock (_frames)
            {
                file = $"f{_frames.Count:D4}.png";
                _frames.Add((file, _clock.Elapsed));
            }

            var path = Path.Combine(directory, file);
            var write = Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(path);
                encoder.Save(stream);
            });
            lock (_frames)
            {
                _writes.Add(write);
            }
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(dib);
            DeleteDC(memoryDc);
        }
    }

    private const uint PW_RENDERFULLCONTENT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);
    private void WriteConcatList()
    {
        var list = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < _frames.Count; i++)
        {
            var duration = i + 1 < _frames.Count ? _frames[i + 1].At - _frames[i].At : FrameInterval;
            list.Append(CultureInfo.InvariantCulture, $"file {_frames[i].File}\nduration {duration.TotalSeconds:0.000}\n");
        }

        // ffmpeg's concat demuxer ignores the last duration unless the final file is repeated.
        list.Append(CultureInfo.InvariantCulture, $"file {_frames[^1].File}\n");
        File.WriteAllText(Path.Combine(directory, "frames.txt"), list.ToString());
    }

    private static async Task Wait(int milliseconds) => await Task.Delay(milliseconds);
}
