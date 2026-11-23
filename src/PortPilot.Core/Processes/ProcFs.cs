using System.Buffers.Text;
using System.IO.Enumeration;
using System.Text;
using PortPilot.Core.Interop;

namespace PortPilot.Core.Processes;

/// <summary>
/// Allocation-conscious procfs helpers. Every method tolerates the process exiting underneath it.
/// </summary>
internal static unsafe class ProcFs
{
    /// <summary>The fields of <c>/proc/[pid]/stat</c> PortPilot cares about.</summary>
    public readonly record struct Stat(char State, int ParentId, long StartTicks)
    {
        public bool IsGone => State is 'Z' or 'X' or 'x';
    }

    private static readonly long ClockTicks = ReadClockTicks();

    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    [ThreadStatic]
    private static byte[]? t_buffer;

    /// <summary>Seconds since the epoch at which the machine booted, from <c>/proc/stat</c>.</summary>
    public static long BootTime { get; } = ReadBootTime();

    /// <summary>
    /// Reads a whole procfs file into <paramref name="buffer"/>, growing it as needed. procfs reports
    /// a size of zero, so the file is read until EOF. Returns -1 when the file cannot be read.
    /// </summary>
    public static int ReadAll(string path, ref byte[] buffer)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                var read = RandomAccess.Read(handle, buffer.AsSpan(total), total);
                if (read <= 0)
                {
                    return total;
                }

                total += read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Reads a small per-process file into a thread-local buffer. The span is valid until the next call.</summary>
    public static ReadOnlySpan<byte> ReadSmall(string path)
    {
        var buffer = t_buffer ??= new byte[4096];
        var length = ReadAll(path, ref buffer);
        t_buffer = buffer;
        return length < 0 ? default : buffer.AsSpan(0, length);
    }

    public static bool TryReadStat(int pid, out Stat stat) => TryReadStat(pid, out stat, out _, wantName: false);

    public static bool TryReadStat(int pid, out Stat stat, out string? name, bool wantName = true)
    {
        stat = default;
        name = null;
        var data = ReadSmall($"/proc/{pid}/stat");

        // "pid (comm) S ppid ..." – comm may itself contain spaces and parentheses, so split on the last ')'.
        var open = data.IndexOf((byte)'(');
        var close = data.LastIndexOf((byte)')');
        if (open < 0 || close < open || close + 2 >= data.Length)
        {
            return false;
        }

        if (wantName)
        {
            name = Encoding.UTF8.GetString(data[(open + 1)..close]);
        }

        var rest = data[(close + 2)..];
        var state = (char)rest[0];
        int ppid = 0;
        long start = 0;

        // Fields after the comm: 0 = state (field 3), 1 = ppid (field 4), 19 = starttime (field 22).
        for (var field = 0; field <= 19 && !rest.IsEmpty; field++)
        {
            var end = rest.IndexOf((byte)' ');
            var token = end < 0 ? rest : rest[..end];
            if (field == 1)
            {
                _ = Utf8Parser.TryParse(token, out ppid, out _);
            }
            else if (field == 19)
            {
                _ = Utf8Parser.TryParse(token, out start, out _);
            }

            rest = end < 0 ? default : rest[(end + 1)..];
        }

        stat = new Stat(state, ppid, start);
        return true;
    }

    /// <summary>Converts a starttime in clock ticks since boot to wall-clock time.</summary>
    public static DateTimeOffset ToStartTime(long startTicks) =>
        DateTimeOffset.FromUnixTimeSeconds(BootTime).AddTicks(startTicks * (TimeSpan.TicksPerSecond / ClockTicks));

    /// <summary>Reads a symbolic link into <paramref name="target"/>; returns the byte count or -1.</summary>
    public static int ReadLink(ReadOnlySpan<byte> nullTerminatedPath, Span<byte> target)
    {
        fixed (byte* p = nullTerminatedPath)
        fixed (byte* t = target)
        {
            return (int)Libc.readlink(p, t, target.Length);
        }
    }

    public static string? ReadLink(string path)
    {
        Span<byte> pathBytes = stackalloc byte[Encoding.UTF8.GetMaxByteCount(path.Length) + 1];
        var length = Encoding.UTF8.GetBytes(path, pathBytes);
        pathBytes[length] = 0;

        Span<byte> target = stackalloc byte[4096];
        var read = ReadLink(pathBytes, target);
        return read < 0 ? null : Encoding.UTF8.GetString(target[..read]);
    }

    /// <summary>Enumerates the numeric entries of a directory (PIDs in /proc, descriptors in /proc/[pid]/fd).</summary>
    public static IEnumerable<int> EnumerateNumeric(string directory) =>
        new FileSystemEnumerable<int>(directory, static (ref entry) => ParseInt(entry.FileName), DirectoryOptions)
        {
            ShouldIncludePredicate = static (ref entry) => ParseInt(entry.FileName) >= 0,
        };

    private static int ParseInt(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty || s.Length > 9)
        {
            return -1;
        }

        var value = 0;
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return -1;
            }

            value = (value * 10) + (c - '0');
        }

        return value;
    }

    private static long ReadClockTicks()
    {
        var ticks = OperatingSystem.IsLinux() ? Libc.sysconf(Libc._SC_CLK_TCK) : 0;
        return ticks > 0 && TimeSpan.TicksPerSecond % ticks == 0 ? ticks : 100;
    }

    private static long ReadBootTime()
    {
        var buffer = new byte[8192];
        var length = ReadAll("/proc/stat", ref buffer);
        if (length <= 0)
        {
            return 0;
        }

        var data = buffer.AsSpan(0, length);
        var at = data.IndexOf("\nbtime "u8);
        if (at < 0)
        {
            return 0;
        }

        return Utf8Parser.TryParse(data[(at + 7)..], out long seconds, out _) ? seconds : 0;
    }
}
