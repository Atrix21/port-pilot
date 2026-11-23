using System.Runtime.InteropServices;

namespace PortPilot.Core.Interop;

/// <summary>
/// macOS process and socket introspection through libproc, the API <c>lsof</c> and Activity Monitor use.
/// Struct offsets follow xnu's <c>sys/proc_info.h</c> (stable since macOS 10.7, identical on x64 and arm64).
/// </summary>
internal static unsafe partial class LibProc
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    public const int PROC_ALL_PIDS = 1;
    public const int PROC_PIDLISTFDS = 1;
    public const int PROC_PIDTBSDINFO = 3;
    public const int PROC_PIDTASKINFO = 4;

    // struct proc_taskinfo: virtual u64 @0, resident u64 @8, total_user u64 @16, total_system u64 @24 (mach time units).
    public const int TaskInfoSize = 96;
    public const int TaskResident = 8;
    public const int TaskUser = 16;
    public const int TaskSystem = 24;

    [StructLayout(LayoutKind.Sequential)]
    public struct mach_timebase_info_data_t
    {
        public uint numer;
        public uint denom;
    }

    [LibraryImport(LibSystem)]
    public static partial int mach_timebase_info(mach_timebase_info_data_t* info);
    public const int PROC_PIDVNODEPATHINFO = 9;
    public const int PROC_PIDFDVNODEPATHINFO = 2;
    public const int PROC_PIDFDSOCKETINFO = 3;

    public const int PROX_FDTYPE_VNODE = 1;
    public const int PROX_FDTYPE_SOCKET = 2;

    public const int SOCKINFO_IN = 1;
    public const int SOCKINFO_TCP = 2;

    public const int INI_IPV4 = 0x1;
    public const int INI_IPV6 = 0x2;

    public const int IPPROTO_TCP = 6;
    public const int IPPROTO_UDP = 17;

    public const int SZOMB = 5;

    public const int MAXPATHLEN = 1024;

    // ── struct proc_fdinfo { int32 proc_fd; uint32 proc_fdtype; } ──
    public const int FdInfoSize = 8;

    // ── struct socket_fdinfo { proc_fileinfo pfi (24); socket_info psi; } ──
    // socket_info: vinfo_stat (136), soi_so, soi_pcb, type@152, protocol@156, family@160, …, kind@232, soi_proto@240.
    public const int SocketFdInfoBuffer = 1024;
    public const int SockProtocol = 24 + 156;
    public const int SockKind = 24 + 232;
    public const int SockProto = 24 + 240;

    // in_sockinfo (inside soi_proto): fport@0, lport@4, vflag@24, faddr@32 (16), laddr@48 (16); tcp_sockinfo.tcpsi_state@80.
    public const int InForeignPort = SockProto + 0;
    public const int InLocalPort = SockProto + 4;
    public const int InVersionFlag = SockProto + 24;
    public const int InForeignAddress = SockProto + 32;
    public const int InLocalAddress = SockProto + 48;
    public const int TcpState = SockProto + 80;

    // ── struct proc_bsdinfo (136 bytes) ──
    public const int BsdInfoSize = 136;
    public const int BsdStatus = 4;
    public const int BsdPpid = 16;
    public const int BsdUid = 20;
    public const int BsdComm = 48;   // char[16]
    public const int BsdName = 64;   // char[32]
    public const int BsdStartSec = 120;
    public const int BsdStartUsec = 128;

    // ── struct proc_vnodepathinfo { vnode_info_path cdir; vnode_info_path rdir; } ──
    // vnode_info_path { vnode_info (152); char path[1024]; }
    public const int VnodePathInfoSize = 2 * (152 + MAXPATHLEN);
    public const int CwdPath = 152;

    // ── struct vnode_fdinfowithpath { proc_fileinfo (24); vnode_info_path; } ──
    public const int VnodeFdInfoSize = 24 + 152 + MAXPATHLEN;
    public const int VnodeFdPath = 24 + 152;

    [LibraryImport(LibSystem, SetLastError = true)]
    public static partial int proc_listpids(uint type, uint typeinfo, void* buffer, int buffersize);

    [LibraryImport(LibSystem, SetLastError = true)]
    public static partial int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int buffersize);

    [LibraryImport(LibSystem, SetLastError = true)]
    public static partial int proc_pidfdinfo(int pid, int fd, int flavor, void* buffer, int buffersize);

    [LibraryImport(LibSystem, SetLastError = true)]
    public static partial int proc_pidpath(int pid, byte* buffer, uint buffersize);

    [LibraryImport(LibSystem, SetLastError = true)]
    public static partial int sysctl(int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

    public const int CTL_KERN = 1;
    public const int KERN_ARGMAX = 8;
    public const int KERN_PROCARGS2 = 49;
}
