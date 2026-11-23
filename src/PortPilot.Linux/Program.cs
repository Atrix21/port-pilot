using Avalonia;
using PortPilot.Linux.Services;

namespace PortPilot.Linux;

internal static class Program
{
    internal static StartupOptions Options { get; private set; } = new();

    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Options = StartupOptions.Parse(args);

        // Hand off to a running copy before paying for any UI startup.
        Instance = Options.ScreenshotDirectory is not null ? null : Options.Elevated ? SingleInstance.Replace() : SingleInstance.TryAcquire();
        if (Instance is null && !Options.Demo && Options.ScreenshotDirectory is null)
        {
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { WmClass = "portpilot" });
}

internal sealed record StartupOptions
{
    public bool Minimized { get; init; }

    public bool Demo { get; init; }

    public bool Verbose { get; init; }

    public bool Elevated { get; init; }

    /// <summary>Renders the main views to PNGs in this folder, then exits. Used to check the UI and refresh docs.</summary>
    public string? ScreenshotDirectory { get; init; }

    public static StartupOptions Parse(string[] args) => new()
    {
        Minimized = args.Contains("--minimized"),
        Demo = args.Contains("--demo"),
        Verbose = args.Contains("--verbose"),
        Elevated = args.Contains("--elevated"),
        ScreenshotDirectory = Array.IndexOf(args, "--screenshot") is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null,
    };
}
