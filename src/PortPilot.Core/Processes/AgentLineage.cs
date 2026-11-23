using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Remembers which agent session started which process, so a process can still be attributed after
/// its session ends (when Linux re-parents it to init, or Windows leaves a dead parent PID behind).
/// Optionally shared between PortPilot processes through a small file, so a one-off CLI run, a Claude
/// Code hook and the background watcher all see the same history. Not thread-safe; callers serialise.
/// </summary>
public sealed class AgentLineage
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<int, (long StartTicks, AgentSession Agent)> _entries = [];
    private readonly LineageStore? _store;
    private readonly Func<int, DateTimeOffset?> _startTimeOf;
    private DateTimeOffset _lastPrune = DateTimeOffset.UtcNow;
    private bool _dirty;

    /// <param name="startTimeOf">The start time of a live process, or null when the PID is not running.</param>
    /// <param name="path">The shared file, or null to keep the history in memory only.</param>
    public AgentLineage(Func<int, DateTimeOffset?> startTimeOf, string? path)
    {
        _startTimeOf = startTimeOf;
        if (path is not null)
        {
            _store = new LineageStore(path);
            Merge();
        }
    }

    /// <summary>Per user: on Linux in tmpfs ($XDG_RUNTIME_DIR, cleared at logout); on Windows in local app data.</summary>
    public static string DefaultPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortPilot", "lineage")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath(), "portpilot-lineage");

    public int Count => _entries.Count;

    /// <summary>Records an attribution, e.g. from a Claude Code session-end hook.</summary>
    public void Record(int pid, DateTimeOffset start, AgentSession agent)
    {
        if (!_entries.TryGetValue(pid, out var existing) || existing.StartTicks != start.UtcTicks)
        {
            _entries[pid] = (start.UtcTicks, agent with { IsRunning = true });
            _dirty = true;
        }
    }

    /// <summary>
    /// Records what a freshly inspected process tells us, or fills in its agent from history. Also marks
    /// the agent as ended when it is no longer running.
    /// </summary>
    public ProcessDetails Apply(ProcessDetails details, Dictionary<string, bool>? aliveMemo = null)
    {
        if (details.StartTime is not { } start || details.ProcessId <= 0)
        {
            return details;
        }

        var ticks = start.UtcTicks;
        if (details.Agent is { } agent)
        {
            Record(details.ProcessId, start, agent);
            return details;
        }

        if (_entries.TryGetValue(details.ProcessId, out var known) && known.StartTicks == ticks)
        {
            return details with { Agent = known.Agent with { IsRunning = IsAlive(known.Agent, aliveMemo) } };
        }

        // A child of an attributed process (a server forked by a script) inherits its agent.
        if (details.ParentProcessId is { } parent && _entries.TryGetValue(parent, out var parentEntry))
        {
            Record(details.ProcessId, start, parentEntry.Agent);
            return details with { Agent = parentEntry.Agent with { IsRunning = IsAlive(parentEntry.Agent, aliveMemo) } };
        }

        return details;
    }

    /// <summary>Marks an agent as ended once it has exited. Cheap: one check per distinct agent per scan.</summary>
    public ProcessDetails Refresh(ProcessDetails details, Dictionary<string, bool> aliveMemo) =>
        details.Agent is { IsRunning: true } agent && !IsAlive(agent, aliveMemo)
            ? details with { Agent = agent with { IsRunning = false } }
            : details;

    public bool IsAlive(AgentSession agent, Dictionary<string, bool>? memo = null)
    {
        if (memo is not null && memo.TryGetValue(agent.Key, out var cached))
        {
            return cached;
        }

        var alive = _startTimeOf(agent.ProcessId) is { } start && (agent.StartTime is null || start.UtcTicks == agent.StartTime.Value.UtcTicks);
        if (memo is not null)
        {
            memo[agent.Key] = alive;
        }

        return alive;
    }

    /// <summary>Drops entries for processes that have exited, then publishes changes to the shared file.</summary>
    public void Flush()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPrune >= PruneInterval)
        {
            _lastPrune = now;
            foreach (var (pid, entry) in _entries.ToList())
            {
                if (!IsSameProcess(pid, entry.StartTicks))
                {
                    _entries.Remove(pid);
                    _dirty = true;
                }
            }
        }

        if (_dirty && _store is not null)
        {
            _dirty = false;
            Merge();
            _store.Save(_entries);
        }
    }

    /// <summary>Takes in what other PortPilot processes recorded, skipping processes that have since exited.</summary>
    private void Merge()
    {
        foreach (var (pid, entry) in _store!.Load())
        {
            if (!_entries.ContainsKey(pid) && IsSameProcess(pid, entry.StartTicks))
            {
                _entries[pid] = entry;
            }
        }
    }

    private bool IsSameProcess(int pid, long startTicks) => _startTimeOf(pid) is { } start && start.UtcTicks == startTicks;
}
