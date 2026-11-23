using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using PortPilot.Core.Interop;

namespace PortPilot.Core.Processes;

/// <summary>Allocation-conscious libproc helpers for macOS. Every method tolerates the process exiting.</summary>
internal static unsafe class MacProc
{
    public readonly record struct BsdInfo(int ParentId, uint Uid, string Comm, DateTimeOffset StartTime, bool IsZombie);

    [ThreadStatic]
    private static byte[]? t_args;

    /// <summary>Every PID on the system (other users' included; their details may be private).</summary>
    public static int[] ListPids()
    {
        var count = LibProc.proc_listpids(LibProc.PROC_ALL_PIDS, 0, null, 0);
        if (count <= 0)
        {
            return [];
        }

        // The table can grow between the two calls; leave headroom.
        var pids = new int[(count / sizeof(int)) + 64];
        fixed (int* p = pids)
        {
            var bytes = LibProc.proc_listpids(LibProc.PROC_ALL_PIDS, 0, p, pids.Length * sizeof(int));
            return bytes <= 0 ? [] : pids.AsSpan(0, bytes / sizeof(int)).ToArray().Where(pid => pid > 0).ToArray();
        }
    }

    public static bool TryGetBsdInfo(int pid, out BsdInfo info)
    {
        info = default;
        Span<byte> buffer = stackalloc byte[LibProc.BsdInfoSize];
        fixed (byte* p = buffer)
        {
            if (LibProc.proc_pidinfo(pid, LibProc.PROC_PIDTBSDINFO, 0, p, buffer.Length) != buffer.Length)
            {
                return false;
            }
        }

        var name = CString(buffer.Slice(LibProc.BsdName, 32));
        var comm = name.Length > 0 ? name : CString(buffer.Slice(LibProc.BsdComm, 16));
        var seconds = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer[LibProc.BsdStartSec..]);
        var micros = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer[LibProc.BsdStartUsec..]);
        info = new BsdInfo(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[LibProc.BsdPpid..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[LibProc.BsdUid..]),
            comm,
            DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(micros * 10),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[LibProc.BsdStatus..]) == LibProc.SZOMB);
        return true;
    }

    public static DateTimeOffset? StartTimeOf(int pid) =>
        TryGetBsdInfo(pid, out var info) && !info.IsZombie ? info.StartTime : null;

    public static string? ExecutablePath(int pid)
    {
        Span<byte> buffer = stackalloc byte[4 * LibProc.MAXPATHLEN];
        fixed (byte* p = buffer)
        {
            var length = LibProc.proc_pidpath(pid, p, (uint)buffer.Length);
            return length <= 0 ? null : Encoding.UTF8.GetString(buffer[..length]);
        }
    }

    public static string? WorkingDirectory(int pid)
    {
        var buffer = new byte[LibProc.VnodePathInfoSize];
        fixed (byte* p = buffer)
        {
            if (LibProc.proc_pidinfo(pid, LibProc.PROC_PIDVNODEPATHINFO, 0, p, buffer.Length) <= 0)
            {
                return null;
            }
        }

        var path = CString(buffer.AsSpan(LibProc.CwdPath, LibProc.MAXPATHLEN));
        return path.Length == 0 ? null : path;
    }

    /// <summary>The file a descriptor points at (e.g. a redirected stdout), or null for sockets, pipes and ttys' absence.</summary>
    public static string? DescriptorPath(int pid, int fd)
    {
        var buffer = new byte[LibProc.VnodeFdInfoSize];
        fixed (byte* p = buffer)
        {
            if (LibProc.proc_pidfdinfo(pid, fd, LibProc.PROC_PIDFDVNODEPATHINFO, p, buffer.Length) <= 0)
            {
                return null;
            }
        }

        var path = CString(buffer.AsSpan(LibProc.VnodeFdPath, LibProc.MAXPATHLEN));
        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// The command line, from <c>sysctl(KERN_PROCARGS2)</c>: argc, the executable path, NUL padding, then argv.
    /// Only readable for the user's own processes.
    /// </summary>
    public static string? CommandLine(int pid)
    {
        var buffer = t_args ??= new byte[ArgMax()];
        int* mib = stackalloc int[3];
        mib[0] = LibProc.CTL_KERN;
        mib[1] = LibProc.KERN_PROCARGS2;
        mib[2] = pid;
        nuint size = (nuint)buffer.Length;
        fixed (byte* p = buffer)
        {
            if (LibProc.sysctl(mib, 3, p, &size, null, 0) != 0 || size <= sizeof(int))
            {
                return null;
            }
        }

        var data = buffer.AsSpan(0, (int)size);
        var argc = BinaryPrimitives.ReadInt32LittleEndian(data);
        data = data[sizeof(int)..];

        // Skip the executable path and its padding.
        var end = data.IndexOf((byte)0);
        if (end < 0)
        {
            return null;
        }

        data = data[end..];
        var skip = 0;
        while (skip < data.Length && data[skip] == 0)
        {
            skip++;
        }

        data = data[skip..];
        var args = new List<string>(Math.Clamp(argc, 0, 256));
        for (var i = 0; i < argc && !data.IsEmpty; i++)
        {
            var next = data.IndexOf((byte)0);
            var arg = next < 0 ? data : data[..next];
            args.Add(Encoding.UTF8.GetString(arg));
            data = next < 0 ? default : data[(next + 1)..];
        }

        return args.Count == 0 ? null : string.Join(' ', args);
    }

    private static int ArgMax()
    {
        int* mib = stackalloc int[2];
        mib[0] = LibProc.CTL_KERN;
        mib[1] = LibProc.KERN_ARGMAX;
        int value;
        nuint size = sizeof(int);
        return LibProc.sysctl(mib, 2, &value, &size, null, 0) == 0 && value > 0 ? Math.Min(value, 1024 * 1024) : 256 * 1024;
    }

    private static string CString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    public static int LastError => Marshal.GetLastPInvokeError();
}
