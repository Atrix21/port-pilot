using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PortPilot.App.Controls;
using PortPilot.App.Diagnostics;
using PortPilot.App.Native;
using PortPilot.App.Services;
using PortPilot.App.ViewModels;
using PortPilot.App.Views;
using PortPilot.Core.Discovery;
using PortPilot.Core.Logging;
using PortPilot.Core.Processes;
using PortPilot.Core.Settings;

namespace PortPilot.App;

/// <summary>
/// Composition root and application lifetime: single instance, tray, logging and shutdown.
/// </summary>
public partial class App : Application, IAppShell
{
    private const string InstanceName = @"Local\PortPilot.SingleInstance";
    private const string ShowSignalName = @"Local\PortPilot.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private JsonFileLoggerFactory? _logs;
    private ILogger? _log;
    private PortMonitor? _monitor;
    private TrayIconService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _quitting;

    internal StartupOptions Options { get; private set; } = new();

    public bool IsMainWindowVisible => _window is { IsVisible: true, WindowState: not WindowState.Minimized };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = StartupOptions.Parse(e.Args);

        if (!Options.IsScreenshotRun && !AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        _logs = new JsonFileLoggerFactory(JsonFileLoggerFactory.DefaultDirectory, Options.Verbose ? LogLevel.Debug : LogLevel.Information);
        _log = _logs.CreateLogger<App>();
        _log.LogInformation("PortPilot {Version} starting (demo: {Demo}, args: {Args})", SettingsViewModel.VersionText, Options.Demo, string.Join(' ', e.Args));
        HookExceptionHandlers();

        var settingsPath = Options.IsScreenshotRun
            ? Path.Combine(Path.GetTempPath(), "PortPilot-screenshots", "settings.json")
            : JsonSettingsStore.DefaultPath;
        var store = new JsonSettingsStore(settingsPath, _logs.CreateLogger<JsonSettingsStore>());
        var settings = Options.IsScreenshotRun ? new AppSettings() : store.Load();

        var theme = new ThemeService(Resources);
        theme.Apply(settings.Theme, animate: false);

        IPortScanner scanner;
        IProcessTerminator terminator;
        if (Options.Demo || Options.IsScreenshotRun)
        {
            var demo = new DemoPortScanner { Staged = Options.IsScreenshotRun };
            scanner = demo;
            terminator = new DemoProcessTerminator(demo);
        }
        else
        {
            scanner = new PortScanner(
                new IpHelperPortTableReader(_logs.CreateLogger<IpHelperPortTableReader>()),
                new ProcessInfoProvider(logger: _logs.CreateLogger<ProcessInfoProvider>()),
                logger: _logs.CreateLogger<PortScanner>());
            terminator = new ProcessTerminator(_logs.CreateLogger<ProcessTerminator>());
        }

        var interval = Options.IsScreenshotRun ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        _monitor = new PortMonitor(scanner, interval, _logs.CreateLogger<PortMonitor>());

        var startup = new StartupRegistration(_logs.CreateLogger<StartupRegistration>());
        if (!Options.IsScreenshotRun)
        {
            startup.RepairIfEnabled();
        }

        _tray = new TrayIconService(_logs.CreateLogger<TrayIconService>());
        var dispatcher = new WpfUiDispatcher(Dispatcher);

        _viewModel = new MainViewModel(
            _monitor,
            terminator,
            store,
            settings,
            new ShellService(_logs.CreateLogger<ShellService>()),
            theme,
            startup,
            new ElevationService(_logs.CreateLogger<ElevationService>(), Quit),
            _tray,
            this,
            dispatcher,
            _logs.CreateLogger<MainViewModel>(),
            JsonFileLoggerFactory.DefaultDirectory,
            settingsPath,
            tracker: Options.IsScreenshotRun
                ? new PortChangeTracker { NewWindow = TimeSpan.FromMinutes(10), ClosedLinger = TimeSpan.FromMinutes(10) }
                : null);

        _window = new MainWindow(_viewModel, theme);
        theme.TransitionHost = _window;
        MainWindow = _window;

        _tray.Activated += (_, _) => ToggleMainWindow();
        _tray.NotificationClicked += (_, _) => ShowMainWindow();
        _tray.MenuOpening += BuildTrayMenu;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CountText))
            {
                _tray.SetTooltip($"PortPilot · {_viewModel.TotalCount} open endpoints");
            }
        };

        if (Options.IsScreenshotRun)
        {
            _ = new ScreenshotRunner(_window, _viewModel, theme, Options.ScreenshotDirectory!).RunAsync().ContinueWith(
                t =>
                {
                    if (t.Exception is not null)
                    {
                        _log.LogError(t.Exception, "Screenshot run failed");
                    }

                    Quit();
                },
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        _tray.Show();
        if (!(Options.StartMinimized || settings.StartMinimized))
        {
            _window.Show();
        }

        _viewModel.OnWindowVisibilityChanged(IsMainWindowVisible);
        _viewModel.Start();
    }

    // ═════════════ Single instance ═════════════

    private bool AcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var created);
            if (!created)
            {
                // An elevated restart waits for the previous instance to exit.
                var wait = Options.ElevatedRestart ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
                try
                {
                    created = _instanceMutex.WaitOne(wait);
                }
                catch (AbandonedMutexException)
                {
                    created = true;
                }
            }

            if (!created)
            {
                SignalExistingInstance();
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Another instance runs elevated; ask it to show itself.
            SignalExistingInstance();
            return false;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, _) => Dispatcher.InvokeAsync(ShowMainWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return true;
    }

    private static void SignalExistingInstance()
    {
        try
        {
            Win32.AllowSetForegroundWindow(-1);
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
        }
    }

    // ═════════════ Window & tray ═════════════

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        var wasHidden = !_window.IsVisible;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        if (wasHidden)
        {
            _viewModel?.OnWindowVisibilityChanged(true);
        }
    }

    private void ToggleMainWindow()
    {
        if (IsMainWindowVisible && _window!.IsActive)
        {
            HideMainWindow();
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void HideMainWindow()
    {
        _window?.Hide();
        _viewModel?.OnWindowVisibilityChanged(false);
    }

    private void BuildTrayMenu(ContextMenu menu)
    {
        if (_viewModel is null)
        {
            return;
        }

        menu.Items.Add(new MenuItem { Header = "PortPilot", IsEnabled = false, InputGestureText = $"{_viewModel.TotalCount} open" });

        var dev = _viewModel.Rows
            .Where(r => r is { IsDevPort: true, CanOpenInBrowser: true, IsSystem: false } && r.Known!.Kind == Core.Model.KnownPortKind.DevServer)
            .GroupBy(r => r.Port)
            .Select(g => g.First())
            .OrderBy(r => r.Port)
            .Take(8)
            .ToList();

        if (dev.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var row in dev)
            {
                var item = new MenuItem
                {
                    Header = row.UrlDisplay,
                    InputGestureText = row.ProcessName,
                    Command = _viewModel.OpenInBrowserCommand,
                    CommandParameter = row,
                };
                Ui.SetIcon(item, (System.Windows.Media.Geometry)FindResource("Icon.Globe"));
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("Show PortPilot", "Icon.Monitor", ShowMainWindow));
        menu.Items.Add(CreateItem(_viewModel.IsPaused ? "Resume monitoring" : "Pause monitoring", _viewModel.IsPaused ? "Icon.Play" : "Icon.Pause", () => _viewModel.IsPaused = !_viewModel.IsPaused));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("Quit PortPilot", "Icon.Power", Quit));
    }

    private MenuItem CreateItem(string header, string icon, Action action)
    {
        var item = new MenuItem { Header = header };
        Ui.SetIcon(item, (System.Windows.Media.Geometry)FindResource(icon));
        item.Click += (_, _) => action();
        return item;
    }

    // ═════════════ Shutdown ═════════════

    /// <summary>True when the app is exiting for real rather than closing to the tray.</summary>
    internal bool IsQuitting => _quitting;

    public void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        if (_window is { } window && PresentationSource.FromVisual(window) is not null)
        {
            window.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("PortPilot exiting");
        if (!Options.IsScreenshotRun)
        {
            _viewModel?.SaveSettingsNow();
        }

        _viewModel?.Dispose();
        _monitor?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _tray?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _instanceMutex.Dispose();
        }

        _logs?.Dispose();
        base.OnExit(e);
    }

    private void HookExceptionHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true;
            _viewModel?.ShowToast("Something went wrong", ToastKind.Error, e.Exception.Message);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _log?.LogCritical(e.ExceptionObject as Exception, "Fatal exception");
            _logs?.Dispose();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}

internal sealed record StartupOptions
{
    public bool StartMinimized { get; init; }

    public bool Demo { get; init; }

    public bool Verbose { get; init; }

    public bool ElevatedRestart { get; init; }

    public string? ScreenshotDirectory { get; init; }

    public bool IsScreenshotRun => ScreenshotDirectory is not null;

    public static StartupOptions Parse(string[] args)
    {
        var options = new StartupOptions();
        for (var i = 0; i < args.Length; i++)
        {
            options = args[i].ToLowerInvariant() switch
            {
                "--minimized" or "/minimized" => options with { StartMinimized = true },
                "--demo" => options with { Demo = true },
                "--verbose" => options with { Verbose = true },
                "--elevated-restart" => options with { ElevatedRestart = true },
                "--screenshot" when i + 1 < args.Length => options with { ScreenshotDirectory = Path.GetFullPath(args[++i]) },
                _ => options,
            };
        }

        return options;
    }
}
