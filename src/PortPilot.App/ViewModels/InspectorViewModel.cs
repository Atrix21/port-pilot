using System.Globalization;
using PortPilot.App.Mvvm;
using PortPilot.Core.Processes;

namespace PortPilot.App.ViewModels;

/// <summary>
/// Details for the selected row. Expensive lookups (version resources, termination rights) run in the
/// background and are discarded if the selection changes before they finish.
/// </summary>
public sealed class InspectorViewModel(IProcessTerminator terminator) : ObservableObject
{
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
        TerminationAccess.RequiresElevation => "Requires administrator privileges",
        TerminationAccess.Protected => "Windows protects this process",
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
        Row = row;
        Refresh(allRows, DateTimeOffset.Now);

        if (row is null || !changedProcess)
        {
            return;
        }

        _metadata = ExecutableMetadata.Empty;
        Access = null;
        OnPropertiesChanged(nameof(Description), nameof(Company), nameof(Version), nameof(HasPublisher), nameof(PublisherText));
        _ = LoadAsync(row, ++_loadGeneration);
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
        var (metadata, access) = await Task.Run(() => (ExecutableMetadata.Read(row.ExecutablePath), terminator.ProbeAccess(row.Pid)));
        if (generation != _loadGeneration)
        {
            return;
        }

        _metadata = metadata;
        Access = access;
        OnPropertiesChanged(nameof(Description), nameof(Company), nameof(Version), nameof(HasPublisher), nameof(PublisherText));
    }

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
