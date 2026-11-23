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
public sealed class LinuxProcessInfoProvider(ILogger<LinuxProcessInfoProvider>? logger = null) : IProcessInfoProvider
{
    private readonly ILogger _log = logger ?? NullLogger<LinuxProcessInfoProvider>.Instance;
    private readonly Dictionary<int, ProcessDetails> _cache = [];
    private readonly Lock _gate = new();

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
            foreach (var pid in wanted)
            {
                if (!_cache.TryGetValue(pid, out var details) || !IsStillValid(details))
                {
                    details = Inspect(pid);
                    _cache[pid] = details;
                    resolved++;
                }

                result[pid] = details;
            }

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

        return new ProcessDetails
        {
            ProcessId = pid,
            ImageName = ImageName(path, commandLine, comm) ?? $"PID {pid}",
            ExecutablePath = path,
            CommandLine = commandLine,
            StartTime = startTime,
            ParentProcessId = stat.ParentId,
            Access = path is null ? ProcessAccessLevel.Restricted : ProcessAccessLevel.Full,
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
