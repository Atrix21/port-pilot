using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Interop;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Discovery;

/// <summary>
/// Reads endpoints on macOS by walking each process's descriptors with libproc and reading the socket
/// ones. Unlike Linux, the owner comes with the socket, so there is no inode-to-PID join. Without root,
/// macOS only lets a user list the descriptors of their own processes, so other users' sockets are not
/// visible at all.
/// </summary>
public sealed unsafe class MacPortTableReader(ILogger<MacPortTableReader>? logger = null) : IPortTableReader
{
    private readonly ILogger _log = logger ?? NullLogger<MacPortTableReader>.Instance;
    private readonly Lock _gate = new();
    private byte[] _fds = new byte[256 * LibProc.FdInfoSize];
    private readonly byte[] _socket = new byte[LibProc.SocketFdInfoBuffer];

    public IReadOnlyList<PortBinding> Read(bool includeAllTcpStates)
    {
        lock (_gate)
        {
            var results = new List<PortBinding>(256);
            foreach (var pid in MacProc.ListPids())
            {
                ReadProcess(pid, includeAllTcpStates, results);
            }

            return results;
        }
    }

    private void ReadProcess(int pid, bool includeAllTcpStates, List<PortBinding> results)
    {
        int bytes;
        while (true)
        {
            fixed (byte* p = _fds)
            {
                bytes = LibProc.proc_pidinfo(pid, LibProc.PROC_PIDLISTFDS, 0, p, _fds.Length);
            }

            if (bytes <= 0)
            {
                return; // gone, or not ours
            }

            if (bytes < _fds.Length)
            {
                break;
            }

            _fds = new byte[_fds.Length * 2];
        }

        for (var offset = 0; offset + LibProc.FdInfoSize <= bytes; offset += LibProc.FdInfoSize)
        {
            var fd = BinaryPrimitives.ReadInt32LittleEndian(_fds.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(_fds.AsSpan(offset + 4));
            if (type != LibProc.PROX_FDTYPE_SOCKET)
            {
                continue;
            }

            int read;
            fixed (byte* s = _socket)
            {
                read = LibProc.proc_pidfdinfo(pid, fd, LibProc.PROC_PIDFDSOCKETINFO, s, _socket.Length);
            }

            if (read < LibProc.TcpState + 4)
            {
                continue;
            }

            if (TryParse(_socket, pid, includeAllTcpStates) is { } binding)
            {
                results.Add(binding);
            }
        }
    }

    /// <summary>Parses a <c>socket_fdinfo</c>; internal for tests that feed captured buffers.</summary>
    internal static PortBinding? TryParse(ReadOnlySpan<byte> info, int pid, bool includeAllTcpStates)
    {
        var kind = BinaryPrimitives.ReadInt32LittleEndian(info[LibProc.SockKind..]);
        var protocol = BinaryPrimitives.ReadInt32LittleEndian(info[LibProc.SockProtocol..]);
        if (kind is not (LibProc.SOCKINFO_IN or LibProc.SOCKINFO_TCP) || protocol is not (LibProc.IPPROTO_TCP or LibProc.IPPROTO_UDP))
        {
            return null;
        }

        var tcp = protocol == LibProc.IPPROTO_TCP;
        var state = ConnectionState.Bound;
        if (tcp)
        {
            state = kind == LibProc.SOCKINFO_TCP ? MapTcpState(BinaryPrimitives.ReadInt32LittleEndian(info[LibProc.TcpState..])) : ConnectionState.Unknown;
            if (!includeAllTcpStates && state != ConnectionState.Listening)
            {
                return null;
            }
        }

        var ipv6 = (info[LibProc.InVersionFlag] & LibProc.INI_IPV6) != 0;
        var localPort = Port(info[LibProc.InLocalPort..]);
        if (localPort == 0)
        {
            return null; // created but never bound
        }

        var local = Address(info.Slice(LibProc.InLocalAddress, 16), ipv6);
        IPAddress? remote = null;
        var remotePort = 0;
        if (tcp && state != ConnectionState.Listening)
        {
            remote = Address(info.Slice(LibProc.InForeignAddress, 16), ipv6);
            remotePort = Port(info[LibProc.InForeignPort..]);
        }

        return new PortBinding(tcp ? TransportProtocol.Tcp : TransportProtocol.Udp, local, localPort, remote, remotePort, state, pid);
    }

    /// <summary>Ports are stored as an int holding a network-order u_short.</summary>
    private static int Port(ReadOnlySpan<byte> value) => (value[0] << 8) | value[1];

    private static IPAddress Address(ReadOnlySpan<byte> union, bool ipv6) =>
        ipv6 ? new IPAddress(union) : new IPAddress(union.Slice(12, 4)); // in4in6_addr: 12 bytes padding, then the IPv4 address

    private static ConnectionState MapTcpState(int state) => state switch
    {
        0 => ConnectionState.Closed,
        1 => ConnectionState.Listening,
        2 => ConnectionState.SynSent,
        3 => ConnectionState.SynReceived,
        4 => ConnectionState.Established,
        5 => ConnectionState.CloseWait,
        6 => ConnectionState.FinWait1,
        7 => ConnectionState.Closing,
        8 => ConnectionState.LastAck,
        9 => ConnectionState.FinWait2,
        10 => ConnectionState.TimeWait,
        _ => ConnectionState.Unknown,
    };
}
