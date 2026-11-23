using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PortPilot.App.Mvvm;
using PortPilot.App.Services;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;
using PortPilot.Core.Settings;

namespace PortPilot.App.ViewModels;

public enum EmptyState
{
    None,
    Discovering,
    NoPorts,
    NoResults,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan RemovalAnimation = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(3.2);
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly PortMonitor _monitor;
    private readonly IProcessTerminator _terminator;
    private readonly ISettingsStore _settingsStore;
    private readonly IShellService _shell;
    private readonly IThemeService _theme;
    private readonly IElevationService _elevation;
    private readonly INotificationService _notifications;
    private readonly IAppShell _app;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;

    private readonly PortChangeTracker _tracker;
    private readonly Dictionary<string, PortRowViewModel> _rowsByKey = [];
    private readonly RowsView _rowsView;

    private AppSettings _settings;
    private PortFilter _filter;
    private bool _scopeIncludesAllStates;
    private bool _hasSnapshot;
    private int _searchGeneration;
    private int _saveGeneration;

    private string _searchText = "";
    private PortRowViewModel? _selectedRow;
    private ConfirmDialogViewModel? _dialog;
    private bool _isSettingsOpen;
    private bool _isShortcutsOpen;
    private bool _isPaused;
    private EmptyState _emptyState = EmptyState.Discovering;
    private int _totalCount;
    private int _visibleCount;
    private int _processCount;
    private bool _hasRestrictedProcesses;
    private DateTimeOffset? _lastUpdated;
    private TimeSpan _lastScanDuration;
    private string? _scanError;
    private long _pulse;

    public MainViewModel(
        PortMonitor monitor,
        IProcessTerminator terminator,
        ISettingsStore settingsStore,
        AppSettings settings,
        IShellService shell,
        IThemeService theme,
        IStartupRegistration startup,
        IElevationService elevation,
        INotificationService notifications,
        IAppShell app,
        IUiDispatcher ui,
        ILogger<MainViewModel> log,
        string logDirectory,
        string settingsPath,
        PortChangeTracker? tracker = null,
        TimeProvider? clock = null)
    {
        _monitor = monitor;
        _terminator = terminator;
        _settingsStore = settingsStore;
        _shell = shell;
        _theme = theme;
        _elevation = elevation;
        _notifications = notifications;
        _app = app;
        _ui = ui;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _settings = settings;
        _tracker = tracker ?? new PortChangeTracker(_clock);

        _filter = new PortFilter("", settings.Protocol, settings.ListeningOnly, settings.DevPortsOnly);
        _scopeIncludesAllStates = !settings.ListeningOnly;
        _monitor.IncludeAllTcpStates = _scopeIncludesAllStates;

        _rowsView = new RowsView(Rows, row => _filter.Matches(row.Entry), new RowComparer(settings.SortColumn, settings.SortDescending));

        Inspector = new InspectorViewModel(terminator);
        Settings = new SettingsViewModel(() => _settings, UpdateSettings, startup, shell, logDirectory, settingsPath);

        RefreshCommand = new RelayCommand(Refresh);
        TogglePauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        ToggleShortcutsCommand = new RelayCommand(() => IsShortcutsOpen = !IsShortcutsOpen);
        DismissCommand = new RelayCommand(Dismiss);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        SortCommand = new RelayCommand<string>(column =>
        {
            if (Enum.TryParse<SortColumn>(column, out var parsed))
            {
                SortBy(parsed);
            }
        });
        SetProtocolCommand = new RelayCommand<string>(value =>
        {
            if (Enum.TryParse<ProtocolFilter>(value, out var parsed))
            {
                Protocol = parsed;
            }
        });
        ToggleListeningOnlyCommand = new RelayCommand(() => ListeningOnly = !ListeningOnly);
        ToggleDevPortsOnlyCommand = new RelayCommand(() => DevPortsOnly = !DevPortsOnly);
        CloseInspectorCommand = new RelayCommand(() => SelectedRow = null);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsElevated);
        QuitCommand = new RelayCommand(_app.Quit);

        OpenInBrowserCommand = new RelayCommand<PortRowViewModel>(OpenInBrowser, r => r.CanOpenInBrowser);
        InspectCommand = new RelayCommand<PortRowViewModel>(r => SelectedRow = r);
        RevealCommand = new RelayCommand<PortRowViewModel>(Reveal, r => r.HasExecutablePath);
        CopyPortCommand = new RelayCommand<PortRowViewModel>(r => Copy(r.PortText, $"Copied port {r.PortText}"));
        CopyPidCommand = new RelayCommand<PortRowViewModel>(r => Copy(r.PidText, $"Copied PID {r.PidText}"));
        CopyUrlCommand = new RelayCommand<PortRowViewModel>(r => Copy(r.Url, "Copied URL"), r => r.IsTcp);
        CopyEndpointCommand = new RelayCommand<PortRowViewModel>(r => Copy(r.EndpointText, $"Copied {r.EndpointText}"));
        CopyPathCommand = new RelayCommand<PortRowViewModel>(r => Copy(r.ExecutablePath!, "Copied executable path"), r => r.HasExecutablePath);
        CopyCommandLineCommand = new RelayCommand<PortRowViewModel>(r => Copy(r.CommandLine!, "Copied command line"), r => r.CommandLine is not null);
        CopyDetailsCommand = new RelayCommand<PortRowViewModel>(r => Copy(r.ClipboardSummary, "Copied details"));
        TerminateCommand = new AsyncRelayCommand<PortRowViewModel>(TerminateAsync, r => !r.IsClosed && !r.IsSystem, OnCommandError);

        _monitor.SnapshotAvailable += OnSnapshotAvailable;
        _monitor.ScanFailed += OnScanFailed;
    }

    // ═════════════ Collections ═════════════

    public ObservableCollection<PortRowViewModel> Rows { get; } = [];

    /// <summary>The rows that pass the current filter, in display order.</summary>
    public RowsView RowsView => _rowsView;

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public InspectorViewModel Inspector { get; }

    public SettingsViewModel Settings { get; }

    // ═════════════ Filtering & sorting ═════════════

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
            {
                OnPropertyChanged(nameof(HasSearchText));
                var generation = ++_searchGeneration;
                _ui.Schedule(SearchDebounce, () =>
                {
                    if (generation == _searchGeneration)
                    {
                        ApplyFilter(_filter with { Query = _searchText });
                    }
                });
            }
        }
    }

    public bool HasSearchText => _searchText.Length > 0;

    public ProtocolFilter Protocol
    {
        get => _filter.Protocol;
        set
        {
            if (_filter.Protocol != value)
            {
                ApplyFilter(_filter with { Protocol = value });
                UpdateSettings(s => s with { Protocol = value });
            }
        }
    }

    public bool ListeningOnly
    {
        get => _filter.ListeningOnly;
        set
        {
            if (_filter.ListeningOnly == value)
            {
                return;
            }

            ApplyFilter(_filter with { ListeningOnly = value });
            UpdateSettings(s => s with { ListeningOnly = value });

            // Showing every connection needs a different OS table; rebaseline so nothing flashes as "new".
            _scopeIncludesAllStates = !value;
            _tracker.Reset();
            _monitor.IncludeAllTcpStates = _scopeIncludesAllStates;
        }
    }

    public bool DevPortsOnly
    {
        get => _filter.DevPortsOnly;
        set
        {
            if (_filter.DevPortsOnly != value)
            {
                ApplyFilter(_filter with { DevPortsOnly = value });
                UpdateSettings(s => s with { DevPortsOnly = value });
            }
        }
    }

    public bool HasActiveFilters => _filter.HasQuery || _filter.Protocol != ProtocolFilter.All || _filter.DevPortsOnly;

    public SortColumn SortColumn => _settings.SortColumn;

    public bool SortDescending => _settings.SortDescending;

    // ═════════════ Selection ═════════════

    public PortRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(IsInspectorOpen));
                Inspector.Show(value, Rows);
            }
        }
    }

    public bool IsInspectorOpen => _selectedRow is not null;

    // ═════════════ Overlays ═════════════

    public ConfirmDialogViewModel? Dialog
    {
        get => _dialog;
        private set
        {
            if (Set(ref _dialog, value))
            {
                OnPropertyChanged(nameof(IsDialogOpen));
            }
        }
    }

    public bool IsDialogOpen => _dialog is not null;

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (Set(ref _isSettingsOpen, value))
            {
                if (value)
                {
                    IsShortcutsOpen = false;
                    Settings.Refresh();
                }
            }
        }
    }

    public bool IsShortcutsOpen
    {
        get => _isShortcutsOpen;
        set
        {
            if (Set(ref _isShortcutsOpen, value) && value)
            {
                IsSettingsOpen = false;
            }
        }
    }

    // ═════════════ Status ═════════════

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (Set(ref _isPaused, value))
            {
                _monitor.IsPaused = value;
                OnPropertiesChanged(nameof(IsLive), nameof(LiveText), nameof(StatusText));
            }
        }
    }

    public bool IsLive => !_isPaused;

    public string LiveText => _isPaused ? "Paused" : "Live";

    public string IntervalText => $"{_settings.RefreshIntervalSeconds}s";

    public EmptyState EmptyState
    {
        get => _emptyState;
        private set => Set(ref _emptyState, value);
    }

    public string NoResultsTitle => _filter.HasQuery ? $"No matches for “{_filter.Query.Trim()}”" : "Nothing matches these filters";

    public string NoResultsMessage => _filter.HasQuery
        ? "Try a process name, a PID, or a port. Prefix with “:” to match ports only."
        : "Loosen the filters to see more of what's running.";

    public int TotalCount
    {
        get => _totalCount;
        private set => Set(ref _totalCount, value);
    }

    public int VisibleCount
    {
        get => _visibleCount;
        private set => Set(ref _visibleCount, value);
    }

    public int ProcessCount
    {
        get => _processCount;
        private set => Set(ref _processCount, value);
    }

    public string CountText => $"{VisibleCount} of {TotalCount} shown";

    /// <summary>True when filters hide some endpoints, so the filter bar shows how many remain.</summary>
    public bool IsFiltered => _hasSnapshot && VisibleCount != TotalCount;

    public string HeaderSubtitle => !_hasSnapshot ? "Scanning…"
        : $"{TotalCount} {(ListeningOnly ? "open" : "active")} {(TotalCount == 1 ? "endpoint" : "endpoints")} across {ProcessCount} {(ProcessCount == 1 ? "process" : "processes")}";

    public string StatusText => _scanError is not null ? _scanError
        : _lastUpdated is not { } updated ? "Discovering listening ports…"
        : _isPaused ? $"Paused · last scan {updated.ToLocalTime():T}"
        : $"Refreshing every {IntervalText} · last scan took {FormatScanDuration(_lastScanDuration)}";

    public bool HasScanError => _scanError is not null;

    public bool IsElevated => _elevation.IsElevated;

    public bool HasRestrictedProcesses
    {
        get => _hasRestrictedProcesses;
        private set => Set(ref _hasRestrictedProcesses, value);
    }

    /// <summary>Increments when a scan detects a change; the view pulses the live indicator in response.</summary>
    public long Pulse
    {
        get => _pulse;
        private set => Set(ref _pulse, value);
    }

    public bool IsDarkTheme => _theme.IsDark;

    // ═════════════ Commands ═════════════

    public ICommand RefreshCommand { get; }

    public ICommand TogglePauseCommand { get; }

    public ICommand ToggleThemeCommand { get; }

    public ICommand ToggleSettingsCommand { get; }

    public ICommand ToggleShortcutsCommand { get; }

    public ICommand DismissCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand ClearFiltersCommand { get; }

    public ICommand SortCommand { get; }

    public ICommand SetProtocolCommand { get; }

    public ICommand ToggleListeningOnlyCommand { get; }

    public ICommand ToggleDevPortsOnlyCommand { get; }

    public ICommand CloseInspectorCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public ICommand QuitCommand { get; }

    public ICommand OpenInBrowserCommand { get; }

    public ICommand InspectCommand { get; }

    public ICommand RevealCommand { get; }

    public ICommand CopyPortCommand { get; }

    public ICommand CopyPidCommand { get; }

    public ICommand CopyUrlCommand { get; }

    public ICommand CopyEndpointCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand CopyCommandLineCommand { get; }

    public ICommand CopyDetailsCommand { get; }

    public ICommand TerminateCommand { get; }

    // ═════════════ Lifecycle ═════════════

    public void Start() => _monitor.Start();

    /// <summary>Called by the shell when the window is shown or hidden so background scans can relax.</summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        // While hidden, scan only as often as notifications need.
        _monitor.Interval = TimeSpan.FromSeconds(visible
            ? _settings.RefreshIntervalSeconds
            : Math.Max(_settings.RefreshIntervalSeconds, 5));
        if (visible)
        {
            _monitor.RefreshNow();
        }
    }

    public AppSettings CurrentSettings => _settings;

    /// <summary>Tells the user, once, that closing the window kept PortPilot running in the tray.</summary>
    public void OnClosedToTray()
    {
        if (_settings.HasShownTrayHint)
        {
            return;
        }

        UpdateSettings(s => s with { HasShownTrayHint = true });
        _notifications.Notify("PortPilot is still running", "It keeps watching your ports from the notification area. Quit from its menu or press Ctrl+Q.");
    }

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var previous = _settings;
        _settings = change(_settings).Normalize();
        if (previous == _settings)
        {
            return;
        }

        if (previous.Theme != _settings.Theme)
        {
            _theme.Apply(_settings.Theme, animate: true);
            OnPropertyChanged(nameof(IsDarkTheme));
        }

        if (previous.RefreshIntervalSeconds != _settings.RefreshIntervalSeconds)
        {
            _monitor.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
            OnPropertiesChanged(nameof(IntervalText), nameof(StatusText));
        }

        ScheduleSave();
    }

    public void SaveSettingsNow() => _settingsStore.Save(_settings);

    private void ScheduleSave()
    {
        var generation = ++_saveGeneration;
        _ui.Schedule(SettingsSaveDelay, () =>
        {
            if (generation == _saveGeneration)
            {
                var snapshot = _settings;
                Task.Run(() => _settingsStore.Save(snapshot));
            }
        });
    }

    // ═════════════ Snapshot reconciliation ═════════════

    private void OnSnapshotAvailable(object? sender, SnapshotEventArgs e) => _ui.Post(() => ApplySnapshot(e));

    private void OnScanFailed(object? sender, Exception e) => _ui.Post(() =>
    {
        _scanError = "Couldn't read the port table – retrying";
        OnPropertiesChanged(nameof(StatusText), nameof(HasScanError));
    });

    private void ApplySnapshot(SnapshotEventArgs e)
    {
        // A scan started before the scope changed; the next one will match.
        if (e.IncludesAllTcpStates != _scopeIncludesAllStates)
        {
            return;
        }

        var changes = _tracker.Apply(e.Snapshot);
        var now = _clock.GetUtcNow();
        var newlyOpened = new List<PortRowViewModel>();

        foreach (var tracked in changes.Added)
        {
            if (_rowsByKey.TryGetValue(tracked.Key, out var existing))
            {
                // Re-baselining after a scope change: keep the row, refresh its data.
                existing.IsRemoving = false;
                existing.IsClosed = false;
                existing.IsNew = false;
                UpdateRow(existing, tracked.Entry);
                continue;
            }

            var row = new PortRowViewModel(tracked.Entry, IsNoteworthy(tracked.Entry) && tracked.IsNew(now, _tracker.NewWindow));
            _rowsByKey.Add(row.Key, row);
            Rows.Add(row);
            if (row.IsNew)
            {
                newlyOpened.Add(row);
            }
        }

        foreach (var tracked in changes.Reopened)
        {
            if (_rowsByKey.TryGetValue(tracked.Key, out var row))
            {
                row.IsRemoving = false;
                row.IsClosed = false;
                row.IsNew = IsNoteworthy(tracked.Entry);
                UpdateRow(row, tracked.Entry);
            }
            else
            {
                // Quiet endpoints are dropped as soon as they close, so a reopen re-adds them.
                row = new PortRowViewModel(tracked.Entry, isNew: false);
                _rowsByKey.Add(row.Key, row);
                Rows.Add(row);
            }
        }

        foreach (var tracked in changes.Updated)
        {
            if (_rowsByKey.TryGetValue(tracked.Key, out var row))
            {
                UpdateRow(row, tracked.Entry);
            }
        }

        foreach (var tracked in changes.Closed)
        {
            if (!_rowsByKey.TryGetValue(tracked.Key, out var row))
            {
                continue;
            }

            if (IsNoteworthy(tracked.Entry) || ReferenceEquals(row, _selectedRow))
            {
                row.IsClosed = true;
            }
            else
            {
                RemoveRow(row);
            }
        }

        foreach (var tracked in changes.Expired)
        {
            if (_rowsByKey.TryGetValue(tracked.Key, out var row))
            {
                BeginRemove(row);
            }
        }

        if (changes.IsInitial)
        {
            // Drop rows the new baseline no longer contains, without lingering.
            var live = _tracker.Ports.Select(p => p.Key).ToHashSet();
            foreach (var stale in _rowsByKey.Values.Where(r => !live.Contains(r.Key)).ToList())
            {
                RemoveRow(stale);
            }
        }

        foreach (var tracked in _tracker.Ports)
        {
            if (_rowsByKey.TryGetValue(tracked.Key, out var row))
            {
                row.IsNew = IsNoteworthy(tracked.Entry) && tracked.IsNew(now, _tracker.NewWindow);
            }
        }

        _hasSnapshot = true;
        _lastUpdated = e.Snapshot.Timestamp;
        _lastScanDuration = e.Snapshot.Duration;
        _scanError = null;
        if (!changes.IsInitial && changes.Added.Concat(changes.Reopened).Concat(changes.Closed).Any(t => IsNoteworthy(t.Entry)))
        {
            Pulse++;
        }

        UpdateCounts();
        Inspector.Refresh(Rows, DateTimeOffset.Now);
        NotifyAboutNewDevPorts(newlyOpened);
        CommandRequery.Invalidate();
    }

    /// <summary>
    /// Whether an endpoint appearing or disappearing deserves attention. Browsers and system services
    /// open short-lived UDP sockets on ephemeral ports all the time; those update silently.
    /// </summary>
    internal static bool IsNoteworthy(PortEntry entry) =>
        entry.Binding.Protocol == TransportProtocol.Tcp || entry.Port < EphemeralPortStart;

    private const int EphemeralPortStart = 49152;

    private void UpdateRow(PortRowViewModel row, PortEntry entry)
    {
        var comparer = _rowsView.Comparer;
        var oldSortKey = (row.ProcessName, row.State);
        if (!row.Update(entry))
        {
            return;
        }

        // Keep sorted order correct if the change affects the active sort column.
        if (comparer.Column is SortColumn.Process or SortColumn.Status && oldSortKey != (row.ProcessName, row.State))
        {
            var selected = ReferenceEquals(row, _selectedRow);
            var index = Rows.IndexOf(row);
            Rows.RemoveAt(index);
            Rows.Add(row);
            if (selected)
            {
                SelectedRow = row;
            }
        }
    }

    private void BeginRemove(PortRowViewModel row)
    {
        if (row.IsRemoving)
        {
            return;
        }

        row.IsRemoving = true;
        _ui.Schedule(RemovalAnimation, () =>
        {
            if (row.IsRemoving)
            {
                RemoveRow(row);
                UpdateCounts();
            }
        });
    }

    private void RemoveRow(PortRowViewModel row)
    {
        if (ReferenceEquals(_selectedRow, row))
        {
            SelectedRow = null;
        }

        _rowsByKey.Remove(row.Key);
        Rows.Remove(row);
    }

    private void UpdateCounts()
    {
        var open = Rows.Where(r => !r.IsClosed).ToList();
        TotalCount = open.Count;
        ProcessCount = open.Select(r => r.Pid).Distinct().Count();
        VisibleCount = _rowsView.Cast<PortRowViewModel>().Count(r => !r.IsClosed);
        HasRestrictedProcesses = !IsElevated && open.Any(r => r.IsRestricted);

        EmptyState = !_hasSnapshot ? EmptyState.Discovering
            : Rows.Count == 0 ? EmptyState.NoPorts
            : _rowsView.IsEmpty ? EmptyState.NoResults
            : EmptyState.None;

        // With no filters, an empty view means nothing is listening.
        if (EmptyState == EmptyState.NoResults && !HasActiveFilters && _rowsView.IsEmpty && TotalCount == 0)
        {
            EmptyState = EmptyState.NoPorts;
        }

        OnPropertiesChanged(nameof(CountText), nameof(IsFiltered), nameof(HeaderSubtitle), nameof(StatusText), nameof(HasScanError), nameof(NoResultsTitle), nameof(NoResultsMessage), nameof(HasActiveFilters));
    }

    private void ApplyFilter(PortFilter filter)
    {
        if (filter == _filter)
        {
            return;
        }

        _filter = filter;
        _rowsView.Refresh();

        // Keep the inspector only if its row is still visible.
        if (_selectedRow is not null && !_rowsView.Contains(_selectedRow))
        {
            SelectedRow = null;
        }

        OnPropertiesChanged(nameof(Protocol), nameof(ListeningOnly), nameof(DevPortsOnly), nameof(HasActiveFilters));
        UpdateCounts();
    }

    private void ClearFilters()
    {
        _searchText = "";
        OnPropertiesChanged(nameof(SearchText), nameof(HasSearchText));
        ApplyFilter(_filter with { Query = "", Protocol = ProtocolFilter.All, DevPortsOnly = false });
        UpdateSettings(s => s with { Protocol = ProtocolFilter.All, DevPortsOnly = false });
    }

    private void SortBy(SortColumn column)
    {
        var descending = _settings.SortColumn == column && !_settings.SortDescending;
        UpdateSettings(s => s with { SortColumn = column, SortDescending = descending });
        _rowsView.Comparer = new RowComparer(column, descending);
        OnPropertiesChanged(nameof(SortColumn), nameof(SortDescending));
    }

    private void NotifyAboutNewDevPorts(List<PortRowViewModel> opened)
    {
        if (!_settings.NotifyOnNewDevPorts || _app.IsMainWindowVisible)
        {
            return;
        }

        var dev = opened.Where(r => r.Known?.Kind == KnownPortKind.DevServer && r.CanOpenInBrowser).ToList();
        if (dev.Count == 1)
        {
            var row = dev[0];
            _notifications.Notify($"localhost:{row.PortText} is live", $"{row.ProcessName} started listening{(row.KnownName is { } name ? $" · {name}" : "")}");
        }
        else if (dev.Count > 1)
        {
            _notifications.Notify($"{dev.Count} dev servers started", string.Join(", ", dev.Select(r => $":{r.PortText}")));
        }
    }

    // ═════════════ Actions ═════════════

    private void Refresh()
    {
        _monitor.RefreshNow();
    }

    private void ToggleTheme()
    {
        UpdateSettings(s => s with { Theme = _theme.IsDark ? ThemePreference.Light : ThemePreference.Dark });
        Settings.Refresh();
    }

    private void Dismiss()
    {
        if (Dialog is not null)
        {
            Dialog.CancelCommand.Execute(null);
        }
        else if (IsSettingsOpen)
        {
            IsSettingsOpen = false;
        }
        else if (IsShortcutsOpen)
        {
            IsShortcutsOpen = false;
        }
        else if (HasSearchText)
        {
            SearchText = "";
        }
        else if (SelectedRow is not null)
        {
            SelectedRow = null;
        }
    }

    private void OpenInBrowser(PortRowViewModel row)
    {
        if (_shell.OpenUrl(row.Url))
        {
            ShowToast($"Opened {row.UrlDisplay}", ToastKind.Success);
        }
        else
        {
            ShowToast("Couldn't open the browser", ToastKind.Error);
        }
    }

    private void Reveal(PortRowViewModel row)
    {
        if (row.ExecutablePath is { } path && !_shell.RevealInExplorer(path))
        {
            ShowToast($"Couldn't open {PlatformText.FileManager}", ToastKind.Error);
        }
    }

    private void Copy(string text, string message)
    {
        var copied = _shell.CopyText(text);
        ShowToast(copied ? message : "Clipboard is busy – try again", copied ? ToastKind.Success : ToastKind.Error);
    }

    private void RestartAsAdmin()
    {
        SaveSettingsNow();
        if (!_elevation.TryRestartElevated())
        {
            ShowToast($"{PlatformText.AdminTitle} restart was cancelled", ToastKind.Info);
        }
    }

    private async Task TerminateAsync(PortRowViewModel row)
    {
        var siblings = Rows.Where(r => r.Pid == row.Pid && !r.IsClosed && r.IsListening).Select(r => r.Port).Distinct().OrderBy(p => p).ToList();
        var ports = siblings.Count switch
        {
            0 => "",
            1 => $"It is listening on port {siblings[0].ToString(CultureInfo.InvariantCulture)}.",
            _ => $"It is listening on ports {string.Join(", ", siblings.Take(6).Select(p => p.ToString(CultureInfo.InvariantCulture)))}{(siblings.Count > 6 ? "…" : "")}.",
        };

        var dialog = new ConfirmDialogViewModel
        {
            Title = $"Terminate {row.ProcessName}?",
            Message = $"{ports} Any unsaved work in this process will be lost.".Trim(),
            Detail = $"{row.ImageName} · PID {row.PidText}",
            Note = row.IsRestricted || Inspector.Row == row && Inspector.RequiresElevation
                ? $"{PlatformText.Os} may require {PlatformText.AdminPrivileges} for this process."
                : null,
            ConfirmText = "Terminate",
            IsDestructive = true,
        };

        Dialog = dialog;
        var confirmed = await dialog.Result;
        Dialog = null;
        if (!confirmed)
        {
            return;
        }

        _log.LogInformation("User requested termination of {Process} ({Pid})", row.ImageName, row.Pid);
        var result = await _terminator.TerminateAsync(row.Pid, row.StartTime);

        switch (result.Outcome)
        {
            case TerminationOutcome.Terminated:
                ShowToast($"Terminated {row.ProcessName}", ToastKind.Success, $"PID {row.PidText}");
                break;
            case TerminationOutcome.AlreadyExited:
                ShowToast($"{row.ProcessName} had already exited", ToastKind.Info);
                break;
            case TerminationOutcome.ProcessChanged:
                ShowToast("PID now belongs to another process – nothing was terminated", ToastKind.Warning);
                break;
            case TerminationOutcome.AccessDenied:
                ShowToast(IsElevated ? $"{PlatformText.Os} denied access to this process" : $"{PlatformText.AdminTitle} privileges required", ToastKind.Error,
                    IsElevated ? null : $"Restart PortPilot as {PlatformText.Admin} to manage this process.");
                break;
            case TerminationOutcome.Protected:
                ShowToast($"{PlatformText.Os} protects this process", ToastKind.Warning, result.Detail);
                break;
            case TerminationOutcome.TimedOut:
                ShowToast($"{row.ProcessName} is still shutting down", ToastKind.Info);
                break;
            default:
                ShowToast("Couldn't terminate the process", ToastKind.Error, result.Detail);
                break;
        }

        _monitor.RefreshNow();
    }

    private void OnCommandError(Exception ex)
    {
        _log.LogError(ex, "Command failed");
        ShowToast("Something went wrong", ToastKind.Error, ex.Message);
    }

    public void ShowToast(string message, ToastKind kind, string? detail = null)
    {
        var toast = new ToastViewModel(message, kind, detail);
        Toasts.Add(toast);
        while (Toasts.Count(t => !t.IsLeaving) > 3)
        {
            DismissToast(Toasts.First(t => !t.IsLeaving));
        }

        _ui.Schedule(ToastLifetime, () => DismissToast(toast));
    }

    private void DismissToast(ToastViewModel toast)
    {
        if (toast.IsLeaving)
        {
            return;
        }

        toast.IsLeaving = true;
        _ui.Schedule(TimeSpan.FromMilliseconds(220), () => Toasts.Remove(toast));
    }

    private static string FormatScanDuration(TimeSpan duration) =>
        duration.TotalMilliseconds < 1 ? "<1 ms" : $"{duration.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture)} ms";

    public void Dispose()
    {
        _monitor.SnapshotAvailable -= OnSnapshotAvailable;
        _monitor.ScanFailed -= OnScanFailed;
    }
}
