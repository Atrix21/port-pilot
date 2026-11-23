using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Resolves process details on macOS through libproc, with the same PID-reuse-aware cache and agent
/// history as the other platforms. Re-validating a cached entry costs one <c>proc_pidinfo</c> call.
/// </summary>
public sealed class MacProcessInfoProvider : IProcessInfoProvider
{
    private const int MaxDepth = 32;

    private readonly ILogger _log;
    private readonly Dictionary<int, ProcessDetails> _cache = [];
    private readonly Lock _gate = new();
    private readonly AgentLineage _lineage;

    public MacProcessInfoProvider(ILogger<MacProcessInfoProvider>? logger = null, string? lineagePath = null)
    {
        _log = logger ?? NullLogger<MacProcessInfoProvider>.Instance;
        _lineage = new AgentLineage(MacProc.StartTimeOf, lineagePath);
    }

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
            var alive = new Dictionary<string, bool>(StringComparer.Ordinal);
            var resolved = 0;
            foreach (var pid in wanted)
            {
                if (!_cache.TryGetValue(pid, out var details) || !IsStillValid(details))
                {
                    details = _lineage.Apply(Inspect(pid), alive);
                    resolved++;
                }

                details = _lineage.Refresh(details, alive);
                _cache[pid] = details;
                result[pid] = details;
            }

            _lineage.Flush();
            if (resolved > 0)
            {
                _log.LogDebug("Resolved {Count} new processes", resolved);
            }

            return result;
        }
    }

    private static bool IsStillValid(ProcessDetails details)
    {
        if (details.ProcessId <= 0 || details.Access == ProcessAccessLevel.System)
        {
            return true;
        }

        var start = MacProc.StartTimeOf(details.ProcessId);
        return details.Access == ProcessAccessLevel.Exited ? start is null : start == details.StartTime;
    }

    /// <summary>Reads fresh details for one process, bypassing the cache.</summary>
    public static ProcessDetails Inspect(int pid)
    {
        if (pid <= 0 || !MacProc.TryGetBsdInfo(pid, out var info) || info.IsZombie)
        {
            return ProcessDetails.Unknown(pid);
        }

        // launchd (pid 1) and kernel_task (pid 0) are the system itself.
        if (pid == 1)
        {
            return new ProcessDetails { ProcessId = pid, ImageName = info.Comm, StartTime = info.StartTime, Access = ProcessAccessLevel.System };
        }

        var path = MacProc.ExecutablePath(pid);
        var commandLine = MacProc.CommandLine(pid);
        var cwd = MacProc.WorkingDirectory(pid);
        var name = path is not null ? Path.GetFileName(path) : info.Comm;

        return new ProcessDetails
        {
            ProcessId = pid,
            ImageName = string.IsNullOrEmpty(name) ? $"PID {pid}" : name,
            ExecutablePath = path,
            CommandLine = commandLine,
            StartTime = info.StartTime,
            ParentProcessId = info.ParentId,
            Access = commandLine is null && cwd is null ? ProcessAccessLevel.Restricted : ProcessAccessLevel.Full,
            WorkingDirectory = cwd,
            Project = ProjectResolver.Resolve(cwd),
            Agent = FindAncestor(info.ParentId),
            IsDetached = info.ParentId == 1,
        };
    }

    /// <summary>The nearest agent session above a process, walking parents through libproc.</summary>
    public static AgentSession? FindAncestor(int parentId)
    {
        var current = parentId;
        for (var depth = 0; depth < MaxDepth && current > 1; depth++)
        {
            if (!MacProc.TryGetBsdInfo(current, out var info) || info.IsZombie)
            {
                return null;
            }

            var id = current;
            var executable = MacProc.ExecutablePath(current) is { } exe ? Path.GetFileName(exe) : info.Comm;
            if (AgentDetector.Identify(executable, () => MacProc.CommandLine(id)) is { } agent)
            {
                return new AgentSession(current, info.StartTime, agent.Name, MacProc.WorkingDirectory(current), IsRunning: true);
            }

            current = info.ParentId;
        }

        return null;
    }
}
