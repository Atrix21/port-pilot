using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Interop;
using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Resolves process details with a PID-reuse-aware cache so steady-state scans touch the
/// process table only for processes that appeared since the previous scan.
/// </summary>
public sealed class ProcessInfoProvider(TimeProvider? clock = null, ILogger<ProcessInfoProvider>? logger = null, string? lineagePath = null) : IProcessInfoProvider
{
    private readonly AgentLineage _lineage = new(StartTimeOf, lineagePath);

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

            var alive = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var pid in wanted)
            {
                if (_cache.TryGetValue(pid, out var cached) && IsStillValid(cached, now))
                {
                    var refreshed = _lineage.Refresh(cached.Details, alive);
                    if (!ReferenceEquals(refreshed, cached.Details))
                    {
                        _cache[pid] = cached with { Details = refreshed };
                    }

                    result[pid] = refreshed;
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
                    var details = _lineage.Apply(Inspect(pid, names), alive);
                    _cache[pid] = new CacheEntry(details, now);
                    result[pid] = details;
                }

                _log.LogDebug("Resolved {Count} new processes", unresolved.Count);
            }

            _lineage.Flush();
            return result;
        }
    }

    /// <summary>The start time of a running process, or null.</summary>
    public static DateTimeOffset? StartTimeOf(int pid)
    {
        var open = NativeProcess.Open(pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
        if (!open.Success)
        {
            return null;
        }

        using var handle = open.Handle!;
        return NativeProcess.HasExited(handle) ? null : NativeProcess.GetStartTime(handle);
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
        var start = NativeProcess.GetStartTime(handle);
        var cwd = path is null ? null : NativeProcess.GetCurrentDirectory(pid);

        return new ProcessDetails
        {
            ProcessId = pid,
            ImageName = imageName,
            ExecutablePath = path,
            CommandLine = NativeProcess.GetCommandLine(handle),
            StartTime = start,
            ParentProcessId = known ? info.ParentId : null,
            Access = path is null ? ProcessAccessLevel.Restricted : ProcessAccessLevel.Full,
            WorkingDirectory = cwd,
            Project = ProjectResolver.Resolve(cwd),
            Agent = known ? FindAgent(info.ParentId, names) : null,
            IsDetached = known && IsParentGone(info.ParentId, start, names),
        };
    }

    /// <summary>Walks the parent chain (from the process snapshot) to the nearest agent.</summary>
    internal static AgentSession? FindAgent(int parentId, Dictionary<int, (string Name, int ParentId)> names)
    {
        var current = parentId;
        var seen = new HashSet<int>();
        for (var depth = 0; depth < 32 && current > 4 && seen.Add(current) && names.TryGetValue(current, out var entry); depth++)
        {
            var id = current;
            if (AgentDetector.Identify(entry.Name, () => CommandLineOf(id)) is { } agent)
            {
                return new AgentSession(current, StartTimeOf(current), agent.Name, NativeProcess.GetCurrentDirectory(current), IsRunning: true);
            }

            current = entry.ParentId;
        }

        return null;
    }

    /// <summary>
    /// Windows never re-parents, so a dead parent PID, or one reused by a process that started later,
    /// means whatever launched this process has gone.
    /// </summary>
    private static bool IsParentGone(int parentId, DateTimeOffset? start, Dictionary<int, (string Name, int ParentId)> names)
    {
        if (parentId <= 4)
        {
            return false;
        }

        if (!names.ContainsKey(parentId))
        {
            return true;
        }

        return start is { } childStart && StartTimeOf(parentId) is { } parentStart && parentStart > childStart;
    }

    private static string? CommandLineOf(int pid)
    {
        var open = NativeProcess.Open(pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
        if (!open.Success)
        {
            return null;
        }

        using var handle = open.Handle!;
        return NativeProcess.GetCommandLine(handle);
    }
}
