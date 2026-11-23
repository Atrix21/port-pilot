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
    private readonly ILogger _log;
    private readonly Dictionary<int, ProcessDetails> _cache = [];
    private readonly Lock _gate = new();
    private readonly AgentLineage _lineage;

    /// <param name="lineagePath">
    /// Where to share agent attribution with other PortPilot processes (see <see cref="AgentLineage"/>).
    /// Null keeps it in memory only.
    /// </param>
    public LinuxProcessInfoProvider(ILogger<LinuxProcessInfoProvider>? logger = null, string? lineagePath = null)
    {
        _log = logger ?? NullLogger<LinuxProcessInfoProvider>.Instance;
        _lineage = new AgentLineage(StartTimeOf, lineagePath);
    }

    /// <summary>The start time of a running process, or null.</summary>
    public static DateTimeOffset? StartTimeOf(int pid) =>
        ProcFs.TryReadStat(pid, out var stat) && !stat.IsGone ? ProcFs.ToStartTime(stat.StartTicks) : null;

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
            var alive = new Dictionary<string, bool>(StringComparer.Ordinal);
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
            IsDetached = IsReparented(stat.ParentId),
        };
    }

    /// <summary>
    /// Whether the process was handed to init or a user service manager, which is what happens when
    /// the shell, terminal or agent that started it has gone.
    /// </summary>
    private static bool IsReparented(int parentId)
    {
        if (parentId == 1)
        {
            return true;
        }

        return ProcFs.TryReadStat(parentId, out _, out var comm) && comm == "systemd";
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
