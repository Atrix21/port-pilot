using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>Snapshots of the process tree: who is whose parent, for hooks that capture a whole session.</summary>
public static class ProcessTree
{
    /// <summary>Every process with its parent, as of now.</summary>
    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>(512);
        if (OperatingSystem.IsLinux())
        {
            foreach (var pid in ProcFs.EnumerateNumeric("/proc"))
            {
                if (ProcFs.TryReadStat(pid, out var stat) && !stat.IsGone)
                {
                    map[pid] = stat.ParentId;
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var pid in MacProc.ListPids())
            {
                if (MacProc.TryGetBsdInfo(pid, out var info) && !info.IsZombie)
                {
                    map[pid] = info.ParentId;
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var (pid, info) in NativeProcess.SnapshotProcessNames())
            {
                map[pid] = info.ParentId;
            }
        }

        return map;
    }

    /// <summary>All descendants of <paramref name="root"/>, nearest first.</summary>
    public static List<int> Descendants(int root, Dictionary<int, int>? parents = null)
    {
        parents ??= ParentMap();
        var children = parents.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var result = new List<int>();
        var queue = new Queue<int>([root]);
        var seen = new HashSet<int> { root };
        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var kids))
            {
                continue;
            }

            foreach (var kid in kids.Where(seen.Add))
            {
                result.Add(kid);
                queue.Enqueue(kid);
            }
        }

        return result;
    }

    /// <summary>The nearest agent session above <paramref name="pid"/> (not the process itself).</summary>
    public static AgentSession? FindAgentAbove(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return ProcFs.TryReadStat(pid, out var stat) ? AgentDetector.FindAncestor(pid, stat.ParentId) : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacProc.TryGetBsdInfo(pid, out var info) ? MacProcessInfoProvider.FindAncestor(info.ParentId) : null;
        }

        if (OperatingSystem.IsWindows())
        {
            var names = NativeProcess.SnapshotProcessNames();
            return names.TryGetValue(pid, out var self) ? ProcessInfoProvider.FindAgent(self.ParentId, names) : null;
        }

        return null;
    }

    /// <summary>The start time of a running process, or null.</summary>
    public static DateTimeOffset? StartTimeOf(int pid) =>
        OperatingSystem.IsLinux() ? LinuxProcessInfoProvider.StartTimeOf(pid)
        : OperatingSystem.IsMacOS() ? MacProc.StartTimeOf(pid)
        : OperatingSystem.IsWindows() ? ProcessInfoProvider.StartTimeOf(pid)
        : null;

    /// <summary>
    /// Records every live descendant of <paramref name="agent"/> as started by it, so they are recognised
    /// as left behind once the agent exits. Called from the agent's session-end hook, while the tree is
    /// still intact. Returns the recorded processes.
    /// </summary>
    public static List<int> CaptureSession(AgentSession agent, IEnumerable<int> exclude)
    {
        var skip = exclude.ToHashSet();
        var lineage = new AgentLineage(StartTimeOf, AgentLineage.DefaultPath);
        var recorded = new List<int>();
        foreach (var pid in Descendants(agent.ProcessId).Where(p => !skip.Contains(p)))
        {
            if (StartTimeOf(pid) is { } start)
            {
                lineage.Record(pid, start, agent);
                recorded.Add(pid);
            }
        }

        lineage.Flush();
        return recorded;
    }
}
