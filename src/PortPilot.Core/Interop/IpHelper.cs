using System.Runtime.InteropServices;

namespace PortPilot.Core.Interop;

/// <summary>
/// P/Invoke surface for the IP Helper API (iphlpapi.dll).
/// </summary>
internal static unsafe partial class IpHelper
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    public const uint NO_ERROR = 0;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public enum TcpTableClass
    {
        OwnerPidListener = 3,
        OwnerPidConnections = 4,
        OwnerPidAll = 5,
    }

    public enum UdpTableClass
    {
        OwnerPid = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCP6ROW_OWNER_PID
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public fixed byte ucRemoteAddr[16];
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDP6ROW_OWNER_PID
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(
        void* pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        TcpTableClass tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedUdpTable(
        void* pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        UdpTableClass tableClass,
        uint reserved);

    /// <summary>Port numbers are stored big-endian in the low 16 bits of a DWORD.</summary>
    public static int DecodePort(uint networkPort) =>
        (int)(((networkPort & 0xFF) << 8) | ((networkPort >> 8) & 0xFF));
}
