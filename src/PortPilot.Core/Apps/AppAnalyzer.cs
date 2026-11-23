using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Apps;

/// <summary>
/// One thing a person started: a dev server with its watcher and workers, a database, a container.
/// Several listening processes and ports can belong to one app.
/// </summary>
public sealed record AppInfo
{
    /// <summary>Stable across scans: the root process and its start time.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>Framework or category: Vite, Django, Database, Docker, …</summary>
    public string? Kind { get; init; }

    public required int RootProcessId { get; init; }

    public required IReadOnlyList<int> ProcessIds { get; init; }

    /// <summary>Every endpoint of the app, the main one first.</summary>
    public required IReadOnlyList<PortEntry> Endpoints { get; init; }

    public PortEntry Primary => Endpoints[0];

    public ProjectInfo? Project => Endpoints.Select(e => e.Process.Project).FirstOrDefault(p => p is not null);

    public AgentSession? Agent => Endpoints.Select(e => e.Process.Agent).FirstOrDefault(a => a is not null);

    public bool IsOrphaned => Endpoints.Any(e => e.Process.IsOrphaned);

    public bool IsDetached => Endpoints.All(e => e.Process.IsDetached);

    public bool IsTooling { get; init; }

    /// <summary>Services whose owner can't be seen without root/admin, folded into one app.</summary>
    public bool IsSystem { get; init; }

    public DateTimeOffset? Started { get; init; }

    /// <summary>Summed over the whole process family, including children that hold no port.</summary>
    public ResourceUsage Usage { get; init; }

    public string? CommandLine { get; init; }

    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// Groups endpoints into apps and totals their CPU and memory. A listening process's app is found by
/// climbing its parents until a shell, terminal, agent, editor or init: so `npm run dev` → node → esbuild
/// is one app. The process table is read at most every few seconds, and only while someone asks.
/// </summary>
public sealed class AppAnalyzer
{
    private static readonly TimeSpan TableMaxAge = TimeSpan.FromSeconds(4);

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "zsh", "fish", "dash", "ksh", "tcsh", "csh", "nu", "elvish", "xonsh", "pwsh", "powershell", "cmd",
    };

    private static readonly HashSet<string> Boundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost", "wsl", "wslhost",
        // terminals and multiplexers
        "gnome-terminal-server", "konsole", "tilix", "alacritty", "kitty", "wezterm-gui", "xterm", "urxvt", "foot", "terminator", "iterm2", "terminal",
        "windowsterminal", "openconsole", "tmux", "tmux: server", "screen", "zellij", "ghostty", "warp", "hyper", "tabby",
        // session, init and services
        "systemd", "init", "launchd", "login", "sshd", "sshd-session", "explorer", "services", "svchost", "wininit", "winlogon", "runsv", "supervisord", "s6-supervise", "containerd-shim", "containerd-shim-runc-v2",
    };

    private readonly ResourceSampler _sampler = new();
    private readonly Dictionary<int, ProcessDetails> _roots = [];
    private Dictionary<int, (int Parent, string Name)> _table = [];
    private Dictionary<int, List<int>> _children = [];
    private DateTimeOffset _tableRead = DateTimeOffset.MinValue;

    public IReadOnlyList<AppInfo> Analyze(IReadOnlyList<PortEntry> entries)
    {
        RefreshTable();

        // Endpoint processes → their app root. Sockets whose owner can't be seen (other users' services)
        // become one "System services" app rather than a line each.
        const int Unknown = -1;
        var byRoot = new Dictionary<int, List<PortEntry>>();
        foreach (var entry in entries)
        {
            var root = entry.ProcessId <= 0 ? Unknown : RootOf(entry.ProcessId);
            if (!byRoot.TryGetValue(root, out var list))
            {
                byRoot[root] = list = [];
            }

            list.Add(entry);
        }

        // One sample for every process in every family.
        var families = byRoot.Keys.ToDictionary(root => root, root => root > 0 ? Family(root) : new List<int>());
        var usage = _sampler.Sample(families.Values.SelectMany(f => f));
        PruneRoots(byRoot.Keys);

        var apps = new List<AppInfo>(byRoot.Count);
        foreach (var (root, endpoints) in byRoot)
        {
            var ordered = endpoints
                .OrderByDescending(e => e.Binding.Protocol == TransportProtocol.Tcp && e.Binding.IsListening)
                .ThenByDescending(e => e.Known?.Kind == KnownPortKind.DevServer)
                .ThenBy(e => e.Binding.IsIPv6)
                .ThenBy(e => e.Port)
                .ToList();
            var primary = ordered[0];
            var rootDetails = root > 0 && root != primary.ProcessId ? RootDetails(root) : primary.Process;
            var (name, kind) = root == Unknown
                ? (ordered.Count == 1 && primary.Known is { } k ? k.Name : "System services", "System")
                : AppNamer.Name(primary.Process, rootDetails, primary.Known);
            var isAgent = ordered.All(e => AgentDetector.Identify(e.Process.ImageName, () => e.Process.CommandLine) is not null);
            var family = families[root];
            var total = family.Aggregate(default(ResourceUsage), (sum, pid) => usage.TryGetValue(pid, out var u) ? sum + u : sum);

            apps.Add(new AppInfo
            {
                Key = root > 0 ? $"{root}@{rootDetails?.StartTime?.UtcTicks ?? primary.Process.StartTime?.UtcTicks}" : "system",
                Name = name,
                Kind = kind,
                RootProcessId = root > 0 ? root : 0,
                ProcessIds = family,
                Endpoints = ordered,
                IsTooling = isAgent || ordered.All(e => ToolingDetector.IsTooling(e.Process)),
                IsSystem = root == Unknown,
                Started = rootDetails?.StartTime ?? primary.Process.StartTime,
                Usage = total,
                CommandLine = rootDetails?.CommandLine ?? primary.Process.CommandLine,
                WorkingDirectory = rootDetails?.WorkingDirectory ?? primary.Process.WorkingDirectory,
            });
        }

        return apps;
    }

    private void RefreshTable()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _tableRead < TableMaxAge)
        {
            return;
        }

        _tableRead = now;
        _table = ProcessTree.Snapshot();
        _children = [];
        foreach (var (pid, (parent, _)) in _table)
        {
            if (!_children.TryGetValue(parent, out var kids))
            {
                _children[parent] = kids = [];
            }

            kids.Add(pid);
        }
    }

    private int RootOf(int pid)
    {
        var current = pid;
        for (var depth = 0; depth < 16 && _table.TryGetValue(current, out var me); depth++)
        {
            var parent = me.Parent;
            if (parent <= 1 || !_table.TryGetValue(parent, out var up) || IsBoundary(up.Name))
            {
                break;
            }

            // A shell is part of the app when a tool started it (npm runs scripts through `sh -c`), and the
            // edge of the app when it is the user's own shell (started by a terminal, an agent, an editor).
            if (IsShell(up.Name) && (up.Parent <= 1 || !_table.TryGetValue(up.Parent, out var above) || IsBoundary(above.Name) || IsShell(above.Name)))
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private static bool IsShell(string name) => Shells.Contains(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);

    private static bool IsBoundary(string name)
    {
        var n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return Boundaries.Contains(n)
            || AgentDetector.Agents.Any(a => a.Executables.Contains(n, StringComparer.OrdinalIgnoreCase))
            || ToolingDetector.IsTooling(new ProcessDetails { ProcessId = 0, ImageName = n });
    }

    private List<int> Family(int root)
    {
        var family = new List<int> { root };
        for (var i = 0; i < family.Count && family.Count < 512; i++)
        {
            if (_children.TryGetValue(family[i], out var kids))
            {
                family.AddRange(kids);
            }
        }

        return family;
    }

    /// <summary>The root's own details (its command line names `npm run dev` apps), cached per PID and start time.</summary>
    private ProcessDetails? RootDetails(int root)
    {
        var start = ProcessTree.StartTimeOf(root);
        if (_roots.TryGetValue(root, out var cached) && cached.StartTime == start)
        {
            return cached;
        }

        var details = PlatformServices.Inspect(root);
        _roots[root] = details;
        return details;
    }

    private void PruneRoots(IEnumerable<int> live)
    {
        var keep = live.ToHashSet();
        foreach (var stale in _roots.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _roots.Remove(stale);
        }
    }
}
