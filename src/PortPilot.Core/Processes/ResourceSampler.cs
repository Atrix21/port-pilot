using System.Buffers.Binary;
using System.Diagnostics;
using PortPilot.Core.Interop;

namespace PortPilot.Core.Processes;

/// <summary>CPU (percent of one core, like top) and resident memory of one process.</summary>
public readonly record struct ResourceUsage(double CpuPercent, long MemoryBytes)
{
    public static ResourceUsage operator +(ResourceUsage a, ResourceUsage b) => new(a.CpuPercent + b.CpuPercent, a.MemoryBytes + b.MemoryBytes);
}

/// <summary>
/// Samples CPU and memory for a given set of processes. CPU % is the CPU time used since the previous
/// sample divided by the wall time between them, so it costs nothing beyond the per-process read the
/// platform needs anyway: one /proc stat read on Linux, one task-info call on macOS, two calls on an
/// open handle on Windows. Processes not asked for are forgotten. Not thread-safe; callers serialise.
/// </summary>
public sealed unsafe class ResourceSampler
{
    private readonly Dictionary<int, (long CpuNanos, long Wall)> _previous = [];
    private static readonly (uint Numer, uint Denom) MachTimebase = ReadTimebase();

    /// <summary>The machine's physical memory, for "using an unusual amount" judgements.</summary>
    public static long TotalMemoryBytes { get; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public Dictionary<int, ResourceUsage> Sample(IEnumerable<int> processIds)
    {
        var now = Stopwatch.GetTimestamp();
        var result = new Dictionary<int, ResourceUsage>();
        var seen = new HashSet<int>();
        foreach (var pid in processIds)
        {
            if (pid <= 0 || !seen.Add(pid) || !TryRead(pid, out var cpuNanos, out var memory))
            {
                continue;
            }

            var cpu = 0.0;
            if (_previous.TryGetValue(pid, out var last) && cpuNanos >= last.CpuNanos)
            {
                var wallNanos = Stopwatch.GetElapsedTime(last.Wall, now).TotalNanoseconds;
                cpu = wallNanos > 0 ? (cpuNanos - last.CpuNanos) / wallNanos * 100 : 0;
            }

            _previous[pid] = (cpuNanos, now);
            result[pid] = new ResourceUsage(Math.Round(cpu, 1), memory);
        }

        foreach (var stale in _previous.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            _previous.Remove(stale);
        }

        return result;
    }

    private static bool TryRead(int pid, out long cpuNanos, out long memory)
    {
        cpuNanos = 0;
        memory = 0;
        if (OperatingSystem.IsLinux())
        {
            if (!ProcFs.TryReadStat(pid, out var stat) || stat.IsGone)
            {
                return false;
            }

            cpuNanos = stat.CpuTicks * (1_000_000_000 / ProcFs.TicksPerSecond);
            memory = stat.RssPages * ProcFs.PageSize;
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            Span<byte> info = stackalloc byte[LibProc.TaskInfoSize];
            fixed (byte* p = info)
            {
                if (LibProc.proc_pidinfo(pid, LibProc.PROC_PIDTASKINFO, 0, p, info.Length) != info.Length)
                {
                    return false;
                }
            }

            memory = (long)BinaryPrimitives.ReadUInt64LittleEndian(info[LibProc.TaskResident..]);
            var ticks = BinaryPrimitives.ReadUInt64LittleEndian(info[LibProc.TaskUser..]) + BinaryPrimitives.ReadUInt64LittleEndian(info[LibProc.TaskSystem..]);
            cpuNanos = (long)(ticks * MachTimebase.Numer / MachTimebase.Denom);
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            var open = NativeProcess.Open(pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
            if (!open.Success)
            {
                return false;
            }

            using var handle = open.Handle!;
            if (!Kernel32.GetProcessTimes(handle, out _, out _, out var kernel, out var user))
            {
                return false;
            }

            cpuNanos = (kernel.ToInt64() + user.ToInt64()) * 100;
            Kernel32.PROCESS_MEMORY_COUNTERS counters = default;
            counters.cb = (uint)sizeof(Kernel32.PROCESS_MEMORY_COUNTERS);
            if (Kernel32.K32GetProcessMemoryInfo(handle, &counters, counters.cb))
            {
                memory = (long)counters.WorkingSetSize;
            }

            return true;
        }

        return false;
    }

    private static (uint, uint) ReadTimebase()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return (1, 1);
        }

        LibProc.mach_timebase_info_data_t info;
        return LibProc.mach_timebase_info(&info) == 0 && info.denom != 0 ? (info.numer, info.denom) : (1, 1);
    }
}
