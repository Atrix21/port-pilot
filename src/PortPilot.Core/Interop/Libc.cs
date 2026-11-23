using System.Runtime.InteropServices;

namespace PortPilot.Core.Interop;

/// <summary>
/// The handful of libc calls the Linux backend needs. Everything else comes from procfs.
/// </summary>
internal static unsafe partial class Libc
{
    public const int EPERM = 1;
    public const int ESRCH = 3;

    public const int SIGKILL = 9;
    public const int SIGTERM = 15;

    public const int _SC_CLK_TCK = 2;

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint readlink(byte* path, byte* buffer, nint size);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    public static partial long sysconf(int name);
}
