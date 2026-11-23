using System.Collections.ObjectModel;
using System.Globalization;
using PortPilot.App.Mvvm;
using PortPilot.Core.Apps;
using PortPilot.Core.Diagnostics;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.App.ViewModels;

public enum AttentionKind
{
    LeftBehind,
    NotAnswering,
    Heavy,
}

/// <summary>Something a person should look at, said in a sentence.</summary>
public sealed class AttentionViewModel(string key, AttentionKind kind, string text, AppViewModel app) : ObservableObject
{
    public string Key { get; } = key;

    public AttentionKind Kind { get; } = kind;

    public string Text { get; } = text;

    public AppViewModel App { get; } = app;

    public string ActionText => Kind == AttentionKind.LeftBehind ? "Stop it" : "Inspect";
}

/// <summary>One app: a dev server with its workers, a database, a container. Updated in place every scan.</summary>
public sealed class AppViewModel(string key) : ObservableObject
{
    private AppInfo? _info;
    private HealthResult? _health;

    public string Key { get; } = key;

    public AppInfo Info => _info!;

    public string Name => _info?.Name ?? "";

    public string? Kind => _info?.Kind;

    public bool HasKind => !string.IsNullOrEmpty(Kind) && !Name.Contains(Kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>":5173 +2"</summary>
    public string PortsText
    {
        get
        {
            var ports = _info?.Endpoints.Select(e => e.Port).Distinct().ToList() ?? [];
            return ports.Count == 0 ? "" : $":{ports[0].ToString(CultureInfo.InvariantCulture)}{(ports.Count > 1 ? $" +{ports.Count - 1}" : "")}";
        }
    }

    public string AllPortsText => string.Join("  ", _info?.Endpoints.Select(e => $":{e.Port.ToString(CultureInfo.InvariantCulture)}").Distinct() ?? []);

    public string MemoryText => FormatMemory(_info?.Usage.MemoryBytes ?? 0);

    public string CpuText => _info is { } i && i.Usage.MemoryBytes > 0 ? $"{i.Usage.CpuPercent.ToString("0.#", CultureInfo.CurrentCulture)}%" : "—";

    public string UptimeText => _info?.Started is { } s ? InspectorViewModel.FormatDuration(DateTimeOffset.Now - s) : "";

    public string ProcessesText => _info is { ProcessIds.Count: > 1 } i ? $"{i.ProcessIds.Count} processes" : "";

    public string? AgentTagText => _info?.Agent is { } a ? AgentDetector.ShortNameFor(a.Name) + (a.IsRunning ? "" : " · ended") : null;

    public bool HasAgent => _info?.Agent is not null;

    public bool IsOrphaned => _info?.IsOrphaned == true;

    public bool IsSystem => _info?.IsSystem == true;

    public bool IsHttp => _info?.Primary is { } p && p.Binding.Protocol == TransportProtocol.Tcp && p.Binding.State == ConnectionState.Listening
        && p.Known?.Kind != KnownPortKind.DataStore && Kind is not ("Database" or "Broker" or "System");

    public string Url => $"{(_info?.Primary.Known?.PrefersHttps == true ? "https" : "http")}://localhost:{_info?.Primary.Port.ToString(CultureInfo.InvariantCulture)}/";

    public HealthResult? Health
    {
        get => _health;
        set
        {
            if (Set(ref _health, value))
            {
                OnPropertiesChanged(nameof(StatusText), nameof(IsHealthy), nameof(IsUnhealthy));
            }
        }
    }

    public bool IsHealthy => _health?.State is HealthState.Healthy or HealthState.Accepting;

    public bool IsUnhealthy => _health?.State is HealthState.Unreachable or HealthState.Erroring;

    /// <summary>Plain words: Running, Left behind, Not answering, Error 500.</summary>
    public string StatusText => IsOrphaned ? "Left behind"
        : _health?.State == HealthState.Unreachable ? "Not answering"
        : _health is { State: HealthState.Erroring, StatusCode: { } code } ? $"Error {code.ToString(CultureInfo.InvariantCulture)}"
        : "Running";

    private string _signature = "";

    /// <summary>Takes the latest scan; raises change notifications only when something shown actually changed.</summary>
    internal bool Update(AppInfo info)
    {
        _info = info;
        var signature = string.Join('|', info.Name, PortsText, info.Usage.MemoryBytes >> 20, info.Usage.CpuPercent, info.Agent?.Key, info.Agent?.IsRunning, info.IsOrphaned, UptimeText, info.ProcessIds.Count);
        if (signature == _signature)
        {
            return false;
        }

        _signature = signature;
        OnPropertyChanged(string.Empty);
        return true;
    }

    public static string FormatMemory(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024L * 1024 => $"{(bytes / 1024).ToString(CultureInfo.CurrentCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024)).ToString("0", CultureInfo.CurrentCulture)} MB",
        _ => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture)} GB",
    };
}

/// <summary>A project (or "Other") with its apps and their combined CPU and memory.</summary>
public sealed class AppGroupViewModel(string name) : ObservableObject
{
    private string _summary = "";
    private string _branches = "";

    public string Name { get; } = name;

    public ObservableCollection<AppViewModel> Apps { get; } = [];

    public string Branches
    {
        get => _branches;
        set
        {
            if (Set(ref _branches, value))
            {
                OnPropertyChanged(nameof(HasBranches));
            }
        }
    }

    public bool HasBranches => _branches.Length > 0;

    /// <summary>"3 apps · 420 MB · 4% CPU"</summary>
    public string Summary
    {
        get => _summary;
        set => Set(ref _summary, value);
    }
}

/// <summary>
/// The view for people: apps instead of ports, grouped by project, with plain-language status and a short
/// list of things that need attention. Rebuilt from each scan's apps and reconciled in place.
/// </summary>
public sealed class OverviewViewModel : ObservableObject
{
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, AppViewModel> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppGroupViewModel> _groups = new(StringComparer.Ordinal);
    private DateTimeOffset _lastHealth = DateTimeOffset.MinValue;
    private bool _checking;
    private string _totals = "";

    public ObservableCollection<AppGroupViewModel> Groups { get; } = [];

    public ObservableCollection<AttentionViewModel> Attention { get; } = [];

    public bool HasAttention => Attention.Count > 0;

    public bool IsEmpty => Groups.Count == 0;

    /// <summary>"7 apps · 1.2 GB · 9% CPU"</summary>
    public string Totals
    {
        get => _totals;
        private set => Set(ref _totals, value);
    }

    public bool ShowTooling { get; set; }

    /// <summary>Off in demo mode, where the ports aren't real.</summary>
    public bool CheckHealth { get; set; } = true;

    /// <summary>Applies a scan. <paramref name="checkHealth"/> runs health checks in the background, at most every 15 s.</summary>
    public void Update(IReadOnlyList<AppInfo> apps, Action<Action> post)
    {
        var visible = apps.Where(a => ShowTooling || !a.IsTooling).ToList();

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in visible)
        {
            live.Add(info.Key);
            if (!_apps.TryGetValue(info.Key, out var app))
            {
                _apps[info.Key] = app = new AppViewModel(info.Key);
            }

            app.Update(info);
        }

        foreach (var gone in _apps.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _apps.Remove(gone);
        }

        // Projects first (alphabetical), then Other; within a group, attention first, then the heaviest.
        var grouped = visible
            .GroupBy(a => a.Project?.Name ?? "")
            .OrderBy(g => g.Key.Length == 0)
            .ThenByDescending(g => g.Any(a => a.IsOrphaned))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var liveGroups = grouped.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Groups.Where(g => !liveGroups.Contains(GroupKey(g))).ToList())
        {
            Groups.Remove(stale);
            _groups.Remove(GroupKey(stale));
        }

        for (var i = 0; i < grouped.Count; i++)
        {
            var key = grouped[i].Key;
            if (!_groups.TryGetValue(key, out var group))
            {
                _groups[key] = group = new AppGroupViewModel(key.Length == 0 ? "Other" : key);
                Groups.Insert(Math.Min(i, Groups.Count), group);
            }

            Move(Groups, group, i);
            var members = grouped[i]
                .OrderByDescending(a => a.IsOrphaned)
                .ThenBy(a => a.IsSystem)
                .ThenByDescending(a => a.Usage.MemoryBytes)
                .Select(a => _apps[a.Key])
                .ToList();
            Reconcile(group.Apps, members);
            group.Branches = string.Join(", ", grouped[i].Select(a => a.Project?.Branch).Where(b => b is not null).Distinct());
            group.Summary = Summary(grouped[i].ToList());
        }

        Totals = Summary(visible);
        UpdateAttention();
        OnPropertiesChanged(nameof(IsEmpty), nameof(HasAttention));

        if (CheckHealth && !_checking && DateTimeOffset.UtcNow - _lastHealth >= HealthInterval)
        {
            _ = CheckHealthAsync(_apps.Values.Where(a => a.IsHttp && !a.IsSystem).ToList(), post);
        }
    }

    /// <summary>Forget health so the next scan checks again (e.g. after a restart).</summary>
    public void RecheckSoon() => _lastHealth = DateTimeOffset.MinValue;

    private async Task CheckHealthAsync(List<AppViewModel> apps, Action<Action> post)
    {
        _checking = true;
        _lastHealth = DateTimeOffset.UtcNow;
        try
        {
            var results = await Task.Run(() => Task.WhenAll(apps.Select(a => HealthProbe.CheckAsync(a.Info.Primary)))).ConfigureAwait(false);
            post(() =>
            {
                for (var i = 0; i < apps.Count; i++)
                {
                    apps[i].Health = results[i];
                }

                UpdateAttention();
            });
        }
        finally
        {
            _checking = false;
        }
    }

    private void UpdateAttention()
    {
        var items = new List<AttentionViewModel>();
        foreach (var app in _apps.Values)
        {
            var where = app.Info.Project is { } p ? $"{p.Name}: " : "";
            if (app.IsOrphaned)
            {
                var agent = app.Info.Agent?.Name ?? "an agent";
                items.Add(new($"{app.Key}:left", AttentionKind.LeftBehind, $"{where}{app.Name} on {app.PortsText} was left running by a {agent} session that has ended.", app));
            }
            else if (app.Health?.State == HealthState.Unreachable)
            {
                items.Add(new($"{app.Key}:down", AttentionKind.NotAnswering, $"{where}{app.Name} is running but not answering on {app.PortsText}.", app));
            }

            var usage = app.Info.Usage;
            var heavyMemory = usage.MemoryBytes > Math.Max(2L * 1024 * 1024 * 1024, ResourceSampler.TotalMemoryBytes / 4);
            if (usage.CpuPercent >= 150 || heavyMemory)
            {
                var what = usage.CpuPercent >= 150
                    ? $"is using {(usage.CpuPercent / 100).ToString("0.#", CultureInfo.CurrentCulture)} CPU cores"
                    : $"is using {AppViewModel.FormatMemory(usage.MemoryBytes)} of memory";
                items.Add(new($"{app.Key}:heavy", AttentionKind.Heavy, $"{where}{app.Name} {what}.", app));
            }
        }

        var ordered = items.OrderBy(i => i.Kind).ThenBy(i => i.Text, StringComparer.Ordinal).ToList();
        if (!ordered.Select(i => i.Key + i.Text).SequenceEqual(Attention.Select(i => i.Key + i.Text)))
        {
            Attention.Clear();
            foreach (var item in ordered)
            {
                Attention.Add(item);
            }

            OnPropertyChanged(nameof(HasAttention));
        }
    }

    private static string Summary(IReadOnlyCollection<AppInfo> apps)
    {
        var memory = apps.Sum(a => a.Usage.MemoryBytes);
        var cpu = apps.Sum(a => a.Usage.CpuPercent);
        var count = apps.Count(a => !a.IsSystem);
        var parts = new List<string> { $"{count.ToString(CultureInfo.CurrentCulture)} {(count == 1 ? "app" : "apps")}" };
        if (memory > 0)
        {
            parts.Add(AppViewModel.FormatMemory(memory));
            parts.Add($"{cpu.ToString("0.#", CultureInfo.CurrentCulture)}% CPU");
        }

        return string.Join(" · ", parts);
    }

    private string GroupKey(AppGroupViewModel group) => _groups.FirstOrDefault(kv => ReferenceEquals(kv.Value, group)).Key ?? group.Name;

    private static void Move<T>(ObservableCollection<T> list, T item, int index)
    {
        var current = list.IndexOf(item);
        if (current >= 0 && current != index && index < list.Count)
        {
            list.Move(current, index);
        }
    }

    /// <summary>Makes <paramref name="target"/> equal <paramref name="desired"/> with minimal moves, so rows don't flicker.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        var wanted = desired.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var current = target.IndexOf(desired[i]);
            if (current < 0)
            {
                target.Insert(i, desired[i]);
            }
            else if (current != i)
            {
                target.Move(current, i);
            }
        }
    }
}
