using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using PortPilot.App.Diagnostics;
using PortPilot.App.Services;
using PortPilot.App.ViewModels;
using PortPilot.Core;
using PortPilot.Core.Discovery;
using PortPilot.Core.Logging;
using PortPilot.Core.Processes;
using PortPilot.Core.Settings;
using PortPilot.Linux.Services;
using PortPilot.Linux.Views;

namespace PortPilot.Linux;

/// <summary>
/// Composition root and application lifetime: tray, logging, window and shutdown.
/// </summary>
public sealed partial class App : Application, IAppShell
{
    private JsonFileLoggerFactory? _logs;
    private ILogger? _log;
    private PortMonitor? _monitor;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _pauseItem;
    private bool _hasTray;
    private bool _quitting;

    public bool IsMainWindowVisible => _window is { IsVisible: true, WindowState: not WindowState.Minimized };

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var options = Program.Options;

        _logs = new JsonFileLoggerFactory(JsonFileLoggerFactory.DefaultDirectory, options.Verbose ? LogLevel.Debug : LogLevel.Information);
        _log = _logs.CreateLogger<App>();
        _log.LogInformation("PortPilot {Version} starting on Linux (demo: {Demo})", SettingsViewModel.VersionText, options.Demo);
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };

        var store = new JsonSettingsStore(JsonSettingsStore.DefaultPath, _logs.CreateLogger<JsonSettingsStore>());
        var settings = options.ScreenshotDirectory is null ? store.Load() : new AppSettings();

        var theme = new ThemeService();
        theme.Apply(settings.Theme, animate: false);

        IPortScanner scanner;
        IProcessTerminator terminator;
        if (options.Demo)
        {
            var demo = new DemoPortScanner();
            scanner = demo;
            terminator = new DemoProcessTerminator(demo);
        }
        else
        {
            scanner = PlatformServices.CreateScanner(_logs);
            terminator = PlatformServices.CreateProcessTerminator(_logs);
        }

        _monitor = new PortMonitor(scanner, TimeSpan.FromSeconds(settings.RefreshIntervalSeconds), _logs.CreateLogger<PortMonitor>());
        _hasTray = TraySupport.IsAvailable();

        _viewModel = new MainViewModel(
            _monitor,
            terminator,
            store,
            settings,
            new LinuxShellService(() => _window, _logs.CreateLogger<LinuxShellService>()),
            theme,
            new AutostartRegistration(_logs.CreateLogger<AutostartRegistration>()),
            new ElevationService(_logs.CreateLogger<ElevationService>()),
            new NotificationService(_logs.CreateLogger<NotificationService>()),
            this,
            new AvaloniaUiDispatcher(),
            _logs.CreateLogger<MainViewModel>(),
            JsonFileLoggerFactory.DefaultDirectory,
            JsonSettingsStore.DefaultPath);

        _window = new MainWindow(_viewModel);
        _window.Closing += OnWindowClosing;
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && _window.WindowState == WindowState.Minimized
                && _viewModel.CurrentSettings.MinimizeToTray && _hasTray)
            {
                _window.Hide();
            }

            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            {
                _viewModel.OnWindowVisibilityChanged(IsMainWindowVisible);
            }
        };

        if (_hasTray)
        {
            CreateTray();
        }

        if (Program.Instance is { } instance)
        {
            instance.MessageReceived += message => Dispatcher.UIThread.Post(() =>
            {
                if (message == SingleInstance.Quit)
                {
                    Quit();
                }
                else
                {
                    ShowMainWindow();
                }
            });
        }

        // Without a tray there is nowhere to hide, so always show the window.
        if (!_hasTray || !(options.Minimized || settings.StartMinimized))
        {
            _window.Show();
        }

        _viewModel.OnWindowVisibilityChanged(IsMainWindowVisible);
        _viewModel.Start();
        if (options.ScreenshotDirectory is { } directory)
        {
            _ = ScreenshotRunner.RunAsync(_window, _viewModel, directory).ContinueWith(
                t =>
                {
                    if (t.Exception is not null)
                    {
                        _log.LogError(t.Exception, "Screenshot run failed");
                        Console.Error.WriteLine(t.Exception);
                    }

                    Quit();
                },
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray()
    {
        var show = new NativeMenuItem("Show PortPilot");
        show.Click += (_, _) => ShowMainWindow();
        _pauseItem = new NativeMenuItem("Pause live updates");
        _pauseItem.Click += (_, _) =>
        {
            _viewModel!.IsPaused = !_viewModel.IsPaused;
            _pauseItem.Header = _viewModel.IsPaused ? "Resume live updates" : "Pause live updates";
        };
        var quit = new NativeMenuItem("Quit PortPilot");
        quit.Click += (_, _) => Quit();

        using var icon = AssetLoader.Open(new Uri("avares://portpilot-desktop/Assets/PortPilot.png"));
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(new Bitmap(icon)),
            ToolTipText = "PortPilot",
            Menu = new NativeMenu { Items = { show, _pauseItem, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ToggleMainWindow();
        TrayIcon.SetIcons(this, [_tray]);

        _viewModel!.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CountText))
            {
                _tray.ToolTipText = $"PortPilot · {_viewModel.TotalCount} open endpoints";
            }
        };
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        if (_hasTray && _viewModel!.CurrentSettings.CloseToTray)
        {
            e.Cancel = true;
            SavePlacement();
            _window!.Hide();
            _viewModel.OnClosedToTray();
            return;
        }

        e.Cancel = true;
        Quit();
    }

    private void ToggleMainWindow()
    {
        if (IsMainWindowVisible && _window!.IsActive)
        {
            _window.Hide();
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _log?.LogInformation("PortPilot shutting down");
        SavePlacement();
        _viewModel?.SaveSettingsNow();
        _viewModel?.Dispose();
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }

        var monitor = _monitor;
        _ = Task.Run(async () =>
        {
            if (monitor is not null)
            {
                await monitor.DisposeAsync().ConfigureAwait(false);
            }

            _logs?.Dispose();
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()), TaskScheduler.Default);
    }

    private void SavePlacement()
    {
        if (_window is null || _viewModel is null || !_window.IsVisible)
        {
            return;
        }

        var maximized = _window.WindowState == WindowState.Maximized;
        var bounds = _window.Bounds;
        var position = _window.Position;
        _viewModel.UpdateSettings(s => s with
        {
            Window = maximized && s.Window is { } previous
                ? previous with { IsMaximized = true }
                : new WindowPlacementSettings(position.X, position.Y, bounds.Width, bounds.Height, maximized),
        });
    }
}

/// <summary>Whether the desktop shows tray icons (StatusNotifierItem), e.g. KDE, or GNOME with the AppIndicator extension.</summary>
internal static class TraySupport
{
    public static bool IsAvailable()
    {
        try
        {
            var info = new ProcessStartInfo("dbus-send")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher" })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output.Contains("boolean true", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
