using System.Globalization;
using PortPilot.App.Mvvm;
using PortPilot.App.Services;
using System.Windows.Input;
using PortPilot.Core.Diagnostics;
using PortPilot.Core.Processes;

namespace PortPilot.App.ViewModels;

/// <summary>
/// Details for the selected row. Expensive lookups (version resources, termination rights) run in the
/// background and are discarded if the selection changes before they finish.
/// </summary>
public sealed class InspectorViewModel : ObservableObject
{
    private readonly IProcessTerminator _terminator;
    private HealthResult? _health;
    private bool _isCheckingHealth;
    private LogTail? _logs;
    private int _healthGeneration;

    public InspectorViewModel(IProcessTerminator terminator)
    {
        _terminator = terminator;
        RefreshHealthCommand = new RelayCommand(() => _ = LoadHealthAsync(_row, ++_healthGeneration));
        RefreshLogsCommand = new RelayCommand(() => _ = LoadLogsAsync(_row, _loadGeneration));
    }

    public ICommand RefreshHealthCommand { get; }

    public ICommand RefreshLogsCommand { get; }

    // ── Health ──
    public bool CanCheckHealth => _row is { IsTcp: true, IsListening: true, IsClosed: false };

    public bool IsCheckingHealth
    {
        get => _isCheckingHealth;
        private set => Set(ref _isCheckingHealth, value);
    }

    public string HealthText => _isCheckingHealth && _health is null ? "Checking…" : _health?.Summary ?? "—";

    public bool IsHealthy => _health?.State is HealthState.Healthy or HealthState.Accepting;

    public bool IsUnhealthy => _health?.State is HealthState.Erroring or HealthState.Unreachable;

    // ── Output ──
    public bool HasLogs => _logs is { IsAvailable: true, Lines.Count: > 0 };

    public string LogText => _logs is { IsAvailable: true } l ? string.Join(Environment.NewLine, l.Lines) : "";

    public string? LogSource => _logs?.Source;

    public string? LogUnavailable => _logs is { IsAvailable: false } l ? l.Unavailable : _logs is { Lines.Count: 0 } ? "Nothing written yet." : null;

    private PortRowViewModel? _row;
    private IReadOnlyList<PortRowViewModel> _siblings = [];
    private ExecutableMetadata _metadata = ExecutableMetadata.Empty;
    private TerminationAccess? _access;
    private int _loadGeneration;
    private DateTimeOffset _now = DateTimeOffset.Now;

    public PortRowViewModel? Row
    {
        get => _row;
        private set => Set(ref _row, value);
    }

    /// <summary>Other endpoints owned by the same process.</summary>
    public IReadOnlyList<PortRowViewModel> Siblings
    {
        get => _siblings;
        private set
        {
            if (Set(ref _siblings, value))
            {
                OnPropertyChanged(nameof(HasSiblings));
            }
        }
    }

    public bool HasSiblings => _siblings.Count > 0;

    public string? Description => _metadata.Description;

    public string? Company => _metadata.Company;

    public string? Version => _metadata.Version;

    public bool HasPublisher => Company is not null || Version is not null;

    public string PublisherText => string.Join(" · ", new[] { Company, Version is null ? null : $"v{Version}" }.Where(s => s is not null));

    public TerminationAccess? Access
    {
        get => _access;
        private set
        {
            if (Set(ref _access, value))
            {
                OnPropertiesChanged(nameof(CanTerminate), nameof(RequiresElevation), nameof(IsProtected), nameof(TerminateHint));
            }
        }
    }

    public bool CanTerminate => _access is TerminationAccess.Allowed or TerminationAccess.RequiresElevation;

    public bool RequiresElevation => _access == TerminationAccess.RequiresElevation || _row?.IsRestricted == true;

    public bool IsProtected => _access == TerminationAccess.Protected;

    public string TerminateHint => _access switch
    {
        TerminationAccess.RequiresElevation => $"Requires {PlatformText.AdminPrivileges}",
        TerminationAccess.Protected => $"{PlatformText.Os} protects this process",
        TerminationAccess.NotRunning => "The process has exited",
        _ => "Ends the process immediately",
    };

    public string StartedText => _row?.StartTime is { } start
        ? FormatStart(start.ToLocalTime())
        : "Unknown";

    public string UptimeText => _row?.StartTime is { } start ? FormatDuration(_now - start) : "—";

    public void Show(PortRowViewModel? row, IEnumerable<PortRowViewModel> allRows)
    {
        var changedProcess = row?.Pid != _row?.Pid || row?.StartTime != _row?.StartTime;
        var changedRow = !ReferenceEquals(row, _row);
        Row = row;
        Refresh(allRows, DateTimeOffset.Now);

        // Health is per port, so it reloads whenever the selected row changes.
        if (changedRow && !changedProcess)
        {
            _health = null;
            _ = LoadHealthAsync(row, ++_healthGeneration);
        }

        if (row is null || !changedProcess)
        {
            return;
        }

        _metadata = ExecutableMetadata.Empty;
        Access = null;
        _health = null;
        _logs = null;
        OnHealthChanged();
        OnLogsChanged();
        OnPropertiesChanged(nameof(Description), nameof(Company), nameof(Version), nameof(HasPublisher), nameof(PublisherText));
        _ = LoadAsync(row, ++_loadGeneration);
        _ = LoadHealthAsync(row, ++_healthGeneration);
        _ = LoadLogsAsync(row, _loadGeneration);
    }

    /// <summary>Recomputes time-dependent and sibling information after a refresh.</summary>
    public void Refresh(IEnumerable<PortRowViewModel> allRows, DateTimeOffset now)
    {
        _now = now;
        if (_row is null)
        {
            Siblings = [];
            return;
        }

        var row = _row;
        var siblings = allRows
            .Where(r => r.Pid == row.Pid && !ReferenceEquals(r, row) && !r.IsClosed && r.IsListening)
            .OrderBy(r => r.Port)
            .ThenBy(r => r.ProtocolText, StringComparer.Ordinal)
            .ToList();
        if (!siblings.SequenceEqual(_siblings))
        {
            Siblings = siblings;
        }

        OnPropertiesChanged(nameof(UptimeText), nameof(StartedText), nameof(RequiresElevation));
    }

    private async Task LoadAsync(PortRowViewModel row, int generation)
    {
        var (metadata, access) = await Task.Run(() => (ExecutableMetadata.Read(row.ExecutablePath), _terminator.ProbeAccess(row.Pid)));
        if (generation != _loadGeneration)
        {
            return;
        }

        _metadata = metadata;
        Access = access;
        OnPropertiesChanged(nameof(Description), nameof(Company), nameof(Version), nameof(HasPublisher), nameof(PublisherText));
    }

    private async Task LoadHealthAsync(PortRowViewModel? row, int generation)
    {
        if (row is null || !CanCheckHealth)
        {
            OnHealthChanged();
            return;
        }

        IsCheckingHealth = true;
        OnHealthChanged();
        var result = await Task.Run(() => HealthProbe.CheckAsync(row.Entry));
        if (generation != _healthGeneration)
        {
            return;
        }

        _health = result;
        IsCheckingHealth = false;
        OnHealthChanged();
    }

    private async Task LoadLogsAsync(PortRowViewModel? row, int generation)
    {
        if (row is null)
        {
            return;
        }

        var logs = await Task.Run(() => ProcessLogs.Read(row.Pid, 60));
        if (generation != _loadGeneration)
        {
            return;
        }

        _logs = logs;
        OnLogsChanged();
    }

    private void OnHealthChanged() =>
        OnPropertiesChanged(nameof(CanCheckHealth), nameof(HealthText), nameof(IsHealthy), nameof(IsUnhealthy), nameof(IsCheckingHealth));

    private void OnLogsChanged() =>
        OnPropertiesChanged(nameof(HasLogs), nameof(LogText), nameof(LogSource), nameof(LogUnavailable));

    private string FormatStart(DateTimeOffset start)
    {
        var time = start.ToString("t", CultureInfo.CurrentCulture);
        var today = _now.ToLocalTime().Date;
        return start.Date == today ? $"Today at {time}"
            : start.Date == today.AddDays(-1) ? $"Yesterday at {time}"
            : $"{start.ToString("MMM d", CultureInfo.CurrentCulture)} at {time}";
    }

    internal static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalDays}d {span.Hours}h";
    }
}
