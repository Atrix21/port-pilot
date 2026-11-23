using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using PortPilot.App.Services;
using PortPilot.Core.Settings;

namespace PortPilot.Linux.Services;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Schedule(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(action, delay);
}

/// <summary>Opens URLs and folders with the desktop's handlers, and talks to the clipboard.</summary>
public sealed class LinuxShellService(Func<TopLevel?> topLevel, ILogger<LinuxShellService> log) : IShellService
{
    public bool OpenUrl(string url) => Run("xdg-open", url);

    public bool RevealInExplorer(string path)
    {
        // Ask the file manager to select the file (works with Nautilus, Dolphin, Nemo, Caja, Thunar);
        // fall back to opening the containing folder.
        var uri = new Uri(path).AbsoluteUri;
        if (File.Exists(path) && Run("dbus-send", "--session", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:"))
        {
            return true;
        }

        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return folder is not null && Run("xdg-open", folder);
    }

    public bool CopyText(string text)
    {
        if (topLevel()?.Clipboard is not { } clipboard)
        {
            return false;
        }

        _ = clipboard.SetTextAsync(text);
        return true;
    }

    private bool Run(string program, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            // Both helpers return quickly; don't leave zombies behind.
            _ = process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not run {Program}", program);
            return false;
        }
    }
}

public sealed class ThemeService : IThemeService
{
    private ThemePreference _preference = ThemePreference.System;

    public ThemeService()
    {
        if (Application.Current?.PlatformSettings is { } settings)
        {
            settings.ColorValuesChanged += (_, _) =>
            {
                if (_preference == ThemePreference.System)
                {
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                }
            };
        }
    }

    public bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public event EventHandler? ThemeChanged;

    public void Apply(ThemePreference preference, bool animate)
    {
        _preference = preference;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = preference switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Start on login through an XDG autostart entry.</summary>
public sealed class AutostartRegistration(ILogger<AutostartRegistration> log) : IStartupRegistration
{
    private static string EntryPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart",
        "portpilot.desktop");

    public bool IsEnabled => File.Exists(EntryPath);

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(EntryPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            File.WriteAllText(EntryPath, $"""
                [Desktop Entry]
                Type=Application
                Name=PortPilot
                Comment=See what's listening on your ports
                Exec="{Environment.ProcessPath}" --minimized
                Icon=portpilot
                Terminal=false
                X-GNOME-Autostart-enabled=true

                """);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not update autostart entry");
        }
    }
}

/// <summary>
/// Restarts PortPilot as root through polkit, so it can see and stop every user's processes.
/// </summary>
public sealed class ElevationService(ILogger<ElevationService> log) : IElevationService
{
    public bool IsElevated { get; } = Environment.UserName == "root" || GetEuid() == 0;

    public bool TryRestartElevated()
    {
        try
        {
            // pkexec scrubs the environment; pass through what a GUI needs to reach the display.
            var info = new ProcessStartInfo("pkexec") { UseShellExecute = false };
            info.ArgumentList.Add("env");
            foreach (var name in new[] { "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "XDG_CURRENT_DESKTOP" })
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                {
                    info.ArgumentList.Add($"{name}={value}");
                }
            }

            info.ArgumentList.Add(Environment.ProcessPath!);
            info.ArgumentList.Add("--elevated");
            var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            // On success the elevated copy asks this one to quit (see SingleInstance). If the user cancels
            // the polkit prompt, pkexec exits with 126 and nothing changes.
            _ = process.WaitForExitAsync().ContinueWith(t => log.LogInformation("pkexec exited with {Code}", process.ExitCode), TaskScheduler.Default);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not restart elevated");
            return false;
        }
    }

    private static uint GetEuid()
    {
        try
        {
            // The owner of /proc/self is the effective user.
            var status = File.ReadAllText("/proc/self/status");
            var line = status.Split('\n').FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            return line is null ? uint.MaxValue : uint.Parse(line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return uint.MaxValue;
        }
    }
}

/// <summary>Desktop notifications through <c>notify-send</c> (libnotify), which every major desktop provides.</summary>
public sealed class NotificationService(ILogger<NotificationService> log) : INotificationService
{
    public void Notify(string title, string message)
    {
        try
        {
            var info = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            foreach (var arg in new[] { "--app-name=PortPilot", "--icon=portpilot", title, message })
            {
                info.ArgumentList.Add(arg);
            }

            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "notify-send is not available");
        }
    }
}

internal static class Lifetime
{
    public static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
}
