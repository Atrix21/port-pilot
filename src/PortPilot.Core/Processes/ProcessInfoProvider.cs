using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Interop;
using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Resolves process details with a PID-reuse-aware cache so steady-state scans touch the
/// process table only for processes that appeared since the previous scan.
/// </summary>
public sealed class ProcessInfoProvider(TimeProvider? clock = null, ILogger<ProcessInfoProvider>? logger = null) : IProcessInfoProvider
{
    /// <summary>Restricted processes cannot be re-validated by start time, so they expire instead.</summary>
    private static readonly TimeSpan RestrictedTtl = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<ProcessInfoProvider>.Instance;
    private readonly Dictionary<int, CacheEntry> _cache = [];
    private readonly Lock _gate = new();

    private sealed record CacheEntry(ProcessDetails Details, DateTimeOffset ResolvedAt);

    public IReadOnlyDictionary<int, ProcessDetails> Resolve(IReadOnlyCollection<int> processIds)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var wanted = processIds as ISet<int> ?? processIds.ToHashSet();

            // Forget processes that no longer own an endpoint.
            foreach (var stale in _cache.Keys.Where(pid => !wanted.Contains(pid)).ToList())
            {
                _cache.Remove(stale);
            }

            var result = new Dictionary<int, ProcessDetails>(wanted.Count);
            List<int>? unresolved = null;

            foreach (var pid in wanted)
            {
                if (_cache.TryGetValue(pid, out var cached) && IsStillValid(cached, now))
                {
                    result[pid] = cached.Details;
                }
                else
                {
                    (unresolved ??= []).Add(pid);
                }
            }

            if (unresolved is not null)
            {
                var names = NativeProcess.SnapshotProcessNames();
                foreach (var pid in unresolved)
                {
                    var details = Inspect(pid, names);
                    _cache[pid] = new CacheEntry(details, now);
                    result[pid] = details;
                }

                _log.LogDebug("Resolved {Count} new processes", unresolved.Count);
            }

            return result;
        }
    }

    /// <summary>Reads fresh details for one process, bypassing the cache.</summary>
    public static ProcessDetails Inspect(int pid) => Inspect(pid, null);

    private static bool IsStillValid(CacheEntry entry, DateTimeOffset now)
    {
        var details = entry.Details;
        switch (details.Access)
        {
            case ProcessAccessLevel.System:
                return true;
            case ProcessAccessLevel.Restricted:
            case ProcessAccessLevel.Exited:
                return now - entry.ResolvedAt < RestrictedTtl;
        }

        // Guard against PID reuse: the same PID must still belong to a process with the same start time.
        var open = NativeProcess.Open(details.ProcessId, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
        if (!open.Success)
        {
            return false;
        }

        using var handle = open.Handle!;
        return NativeProcess.GetStartTime(handle) == details.StartTime;
    }

    private static ProcessDetails Inspect(int pid, Dictionary<int, (string Name, int ParentId)>? names)
    {
        if (pid is 0 or 4)
        {
            return ProcessDetails.Unknown(pid);
        }

        names ??= NativeProcess.SnapshotProcessNames();
        var known = names.TryGetValue(pid, out var info);

        var open = NativeProcess.Open(pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
        if (!open.Success)
        {
            if (!known)
            {
                return ProcessDetails.Unknown(pid);
            }

            return new ProcessDetails
            {
                ProcessId = pid,
                ImageName = info.Name,
                ParentProcessId = info.ParentId,
                Access = open.NotFound ? ProcessAccessLevel.Exited : ProcessAccessLevel.Restricted,
            };
        }

        using var handle = open.Handle!;
        var path = NativeProcess.GetImagePath(handle);
        var imageName = known ? info.Name : Path.GetFileName(path) ?? $"PID {pid}";

        return new ProcessDetails
        {
            ProcessId = pid,
            ImageName = imageName,
            ExecutablePath = path,
            CommandLine = NativeProcess.GetCommandLine(handle),
            StartTime = NativeProcess.GetStartTime(handle),
            ParentProcessId = known ? info.ParentId : null,
            Access = path is null ? ProcessAccessLevel.Restricted : ProcessAccessLevel.Full,
        };
    }
}
