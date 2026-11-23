using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Resolves process details from procfs with a PID-reuse-aware cache. A cached entry is re-validated
/// with a single read of <c>/proc/[pid]/stat</c>, which is world-readable even for other users'
/// processes, so restricted entries are validated the same way as full ones.
/// </summary>
public sealed class LinuxProcessInfoProvider : IProcessInfoProvider
{
    private static readonly TimeSpan LineagePruneInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _log;
    private readonly LineageStore? _store;
    private bool _lineageDirty;

    private readonly Dictionary<int, ProcessDetails> _cache = [];
    private readonly Lock _gate = new();

    // Which agent session started each process, remembered after the session ends so its leftovers
    // can still be attributed once they have been re-parented to init.
    private readonly Dictionary<int, (long StartTicks, AgentSession Agent)> _lineage = [];
    private DateTimeOffset _lastLineagePrune = DateTimeOffset.UtcNow;

    /// <param name="lineagePath">
    /// Where to share agent attribution with other PortPilot processes, so a one-off CLI run can still
    /// recognise what a long-running copy saw an agent start. Null keeps it in memory only.
    /// </param>
    public LinuxProcessInfoProvider(ILogger<LinuxProcessInfoProvider>? logger = null, string? lineagePath = null)
    {
        _log = logger ?? NullLogger<LinuxProcessInfoProvider>.Instance;
        if (lineagePath is not null)
        {
            _store = new LineageStore(lineagePath);
            foreach (var (pid, entry) in _store.Load())
            {
                if (IsSameProcess(pid, entry.StartTicks))
                {
                    _lineage[pid] = entry;
                }
            }
        }
    }

    /// <summary>The shared lineage file: per user, on tmpfs, gone at logout.</summary>
    public static string DefaultLineagePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath(),
        "portpilot-lineage");

    public IReadOnlyDictionary<int, ProcessDetails> Resolve(IReadOnlyCollection<int> processIds)
    {
        lock (_gate)
        {
            var wanted = processIds as ISet<int> ?? processIds.ToHashSet();

            foreach (var stale in _cache.Keys.Where(pid => !wanted.Contains(pid)).ToList())
            {
                _cache.Remove(stale);
            }

            var result = new Dictionary<int, ProcessDetails>(wanted.Count);
            var resolved = 0;
            Dictionary<string, bool>? agentAlive = null;
            foreach (var pid in wanted)
            {
                if (!_cache.TryGetValue(pid, out var details) || !IsStillValid(details))
                {
                    details = Remember(Inspect(pid));
                    resolved++;
                }

                // An agent that was running at the last scan may have exited since.
                if (details.Agent is { IsRunning: true } agent && !AliveCached(agent, ref agentAlive))
                {
                    details = details with { Agent = agent with { IsRunning = false } };
                }

                _cache[pid] = details;
                result[pid] = details;
            }

            PruneLineage();
            if (_lineageDirty && _store is not null)
            {
                _lineageDirty = false;

                // Merge with what other PortPilot processes recorded, then publish.
                foreach (var (pid, entry) in _store.Load())
                {
                    if (!_lineage.ContainsKey(pid) && IsSameProcess(pid, entry.StartTicks))
                    {
                        _lineage[pid] = entry;
                    }
                }

                _store.Save(_lineage);
            }

            if (resolved > 0)
            {
                _log.LogDebug("Resolved {Count} new processes", resolved);
            }

            return result;
        }
    }

    /// <summary>Records which agent started a process, or fills it in from the record if the agent has gone.</summary>
    private ProcessDetails Remember(ProcessDetails details)
    {
        if (details.StartTime is not { } start || details.ProcessId <= 0)
        {
            return details;
        }

        var ticks = start.UtcTicks;
        if (details.Agent is { } agent)
        {
            if (!_lineage.TryGetValue(details.ProcessId, out var existing) || existing.StartTicks != ticks)
            {
                _lineage[details.ProcessId] = (ticks, agent);
                _lineageDirty = true;
            }

            return details;
        }

        if (_lineage.TryGetValue(details.ProcessId, out var known) && known.StartTicks == ticks)
        {
            return details with { Agent = known.Agent with { IsRunning = IsAlive(known.Agent) } };
        }

        // A child of a process we already attributed (e.g. a server forked by a script) inherits it.
        if (details.ParentProcessId is { } parent && _lineage.TryGetValue(parent, out var parentAgent))
        {
            _lineage[details.ProcessId] = (ticks, parentAgent.Agent);
            _lineageDirty = true;
            return details with { Agent = parentAgent.Agent with { IsRunning = IsAlive(parentAgent.Agent) } };
        }

        return details;
    }

    private void PruneLineage()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastLineagePrune < LineagePruneInterval)
        {
            return;
        }

        _lastLineagePrune = now;
        foreach (var (pid, entry) in _lineage.ToList())
        {
            if (!IsSameProcess(pid, entry.StartTicks))
            {
                _lineage.Remove(pid);
                _lineageDirty = true;
            }
        }
    }

    /// <summary>The PID still belongs to the process that started at <paramref name="startTicks"/>.</summary>
    private static bool IsSameProcess(int pid, long startTicks) =>
        ProcFs.TryReadStat(pid, out var stat) && !stat.IsGone && ProcFs.ToStartTime(stat.StartTicks).UtcTicks == startTicks;

    private static bool AliveCached(AgentSession agent, ref Dictionary<string, bool>? memo)
    {
        memo ??= [];
        if (!memo.TryGetValue(agent.Key, out var alive))
        {
            memo[agent.Key] = alive = IsAlive(agent);
        }

        return alive;
    }

    private static bool IsAlive(AgentSession agent) =>
        ProcFs.TryReadStat(agent.ProcessId, out var stat) && !stat.IsGone && ProcFs.ToStartTime(stat.StartTicks) == agent.StartTime;

    private static bool IsStillValid(ProcessDetails details)
    {
        if (details.Access == ProcessAccessLevel.System || details.ProcessId <= 0)
        {
            return true;
        }

        var alive = ProcFs.TryReadStat(details.ProcessId, out var stat) && !stat.IsGone;
        return details.Access == ProcessAccessLevel.Exited
            ? !alive
            : alive && ProcFs.ToStartTime(stat.StartTicks) == details.StartTime;
    }

    /// <summary>Reads fresh details for one process, bypassing the cache.</summary>
    public static ProcessDetails Inspect(int pid)
    {
        if (pid <= 0 || !ProcFs.TryReadStat(pid, out var stat, out var comm) || stat.IsGone)
        {
            return ProcessDetails.Unknown(pid);
        }

        var startTime = ProcFs.ToStartTime(stat.StartTicks);

        // Kernel threads have no executable or command line and descend from kthreadd (pid 2).
        if (pid == 2 || stat.ParentId == 2)
        {
            return new ProcessDetails
            {
                ProcessId = pid,
                ImageName = comm ?? $"PID {pid}",
                StartTime = startTime,
                ParentProcessId = stat.ParentId,
                Access = ProcessAccessLevel.System,
            };
        }

        var path = ProcFs.ReadLink($"/proc/{pid}/exe");
        if (path is not null && path.EndsWith(" (deleted)", StringComparison.Ordinal))
        {
            path = path[..^10];
        }

        var commandLine = ReadCommandLine(pid);
        var cwd = path is null ? null : ProcFs.ReadLink($"/proc/{pid}/cwd");

        return new ProcessDetails
        {
            ProcessId = pid,
            ImageName = ImageName(path, commandLine, comm) ?? $"PID {pid}",
            ExecutablePath = path,
            CommandLine = commandLine,
            StartTime = startTime,
            ParentProcessId = stat.ParentId,
            Access = path is null ? ProcessAccessLevel.Restricted : ProcessAccessLevel.Full,
            WorkingDirectory = cwd,
            Project = ProjectResolver.Resolve(cwd),
            Agent = AgentDetector.FindAncestor(pid, stat.ParentId),
        };
    }

    /// <summary>
    /// Picks the most recognisable name. comm is what the process calls itself but the kernel truncates it
    /// to 15 characters, so a longer name that starts with it wins: argv[0] first (<c>python3</c> rather than
    /// <c>python3.12</c>), then the executable. Versioned or hashed executables that share nothing with
    /// comm fall back to comm.
    /// </summary>
    private static string? ImageName(string? path, string? commandLine, string? comm)
    {
        if (string.IsNullOrEmpty(comm))
        {
            return path is null ? null : Path.GetFileName(path);
        }

        if (!string.IsNullOrEmpty(commandLine))
        {
            var argv0 = commandLine.AsSpan();
            var space = argv0.IndexOf(' ');
            var name = Path.GetFileName(space > 0 ? argv0[..space] : argv0);
            if (name.StartsWith(comm, StringComparison.Ordinal))
            {
                return name.ToString();
            }
        }

        var file = path is null ? null : Path.GetFileName(path);
        return file is not null && file.StartsWith(comm, StringComparison.Ordinal) ? file : comm;
    }

    private static string? ReadCommandLine(int pid)
    {
        var data = ProcFs.ReadSmall($"/proc/{pid}/cmdline");
        data = data.TrimEnd((byte)0);
        if (data.IsEmpty)
        {
            return null;
        }

        // Arguments are NUL-separated.
        var text = Encoding.UTF8.GetString(data);
        return text.Replace('\0', ' ');
    }
}
