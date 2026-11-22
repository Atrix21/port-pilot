using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Interop;
using PortPilot.Core.Model;
using static PortPilot.Core.Interop.IpHelper;

namespace PortPilot.Core.Discovery;

/// <summary>
/// Reads endpoint tables through <c>GetExtendedTcpTable</c> / <c>GetExtendedUdpTable</c>.
/// These calls are cheap (sub-millisecond) and need no elevation.
/// </summary>
public sealed unsafe class IpHelperPortTableReader(ILogger<IpHelperPortTableReader>? logger = null) : IPortTableReader
{
    private readonly ILogger _log = logger ?? NullLogger<IpHelperPortTableReader>.Instance;

    // Re-used across scans; grows as needed. Scans are serialised by the caller.
    private byte[] _buffer = new byte[64 * 1024];
    private readonly Lock _gate = new();

    public IReadOnlyList<PortBinding> Read(bool includeAllTcpStates)
    {
        lock (_gate)
        {
            var results = new List<PortBinding>(256);
            var tcpClass = includeAllTcpStates ? TcpTableClass.OwnerPidAll : TcpTableClass.OwnerPidListener;

            ReadTable(AF_INET, (p, ref s) => GetExtendedTcpTable(p, ref s, false, AF_INET, tcpClass, 0), ParseTcp4, results);
            ReadTable(AF_INET6, (p, ref s) => GetExtendedTcpTable(p, ref s, false, AF_INET6, tcpClass, 0), ParseTcp6, results);
            ReadTable(AF_INET, (p, ref s) => GetExtendedUdpTable(p, ref s, false, AF_INET, UdpTableClass.OwnerPid, 0), ParseUdp4, results);
            ReadTable(AF_INET6, (p, ref s) => GetExtendedUdpTable(p, ref s, false, AF_INET6, UdpTableClass.OwnerPid, 0), ParseUdp6, results);

            return results;
        }
    }

    private delegate uint TableQuery(void* buffer, ref int size);

    private delegate void TableParser(byte* table, List<PortBinding> results);

    private void ReadTable(int family, TableQuery query, TableParser parse, List<PortBinding> results)
    {
        // The table can grow between the size probe and the read, so retry a few times.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var size = _buffer.Length;
            uint status;
            fixed (byte* p = _buffer)
            {
                status = query(p, ref size);
                if (status == NO_ERROR)
                {
                    parse(p, results);
                    return;
                }
            }

            if (status == ERROR_INSUFFICIENT_BUFFER)
            {
                _buffer = new byte[Math.Max(size + 4096, _buffer.Length * 2)];
                continue;
            }

            _log.LogWarning("Endpoint table query failed for family {Family}: {Error}", family, new Win32Exception((int)status).Message);
            return;
        }

        _log.LogWarning("Endpoint table for family {Family} kept growing; giving up for this scan", family);
    }

    private static void ParseTcp4(byte* table, List<PortBinding> results)
    {
        var count = *(uint*)table;
        var rows = (MIB_TCPROW_OWNER_PID*)(table + sizeof(uint));
        for (var i = 0; i < count; i++)
        {
            ref var row = ref rows[i];
            var state = (ConnectionState)row.dwState;
            var listening = state == ConnectionState.Listening;
            results.Add(new PortBinding(
                TransportProtocol.Tcp,
                new IPAddress(row.dwLocalAddr),
                DecodePort(row.dwLocalPort),
                listening ? null : new IPAddress(row.dwRemoteAddr),
                listening ? 0 : DecodePort(row.dwRemotePort),
                state,
                (int)row.dwOwningPid));
        }
    }

    private static void ParseTcp6(byte* table, List<PortBinding> results)
    {
        var count = *(uint*)table;
        var rows = (MIB_TCP6ROW_OWNER_PID*)(table + sizeof(uint));
        for (var i = 0; i < count; i++)
        {
            var row = rows + i;
            var state = (ConnectionState)row->dwState;
            var listening = state == ConnectionState.Listening;
            results.Add(new PortBinding(
                TransportProtocol.Tcp,
                ToAddress6(row->ucLocalAddr, row->dwLocalScopeId),
                DecodePort(row->dwLocalPort),
                listening ? null : ToAddress6(row->ucRemoteAddr, row->dwRemoteScopeId),
                listening ? 0 : DecodePort(row->dwRemotePort),
                state,
                (int)row->dwOwningPid));
        }
    }

    private static void ParseUdp4(byte* table, List<PortBinding> results)
    {
        var count = *(uint*)table;
        var rows = (MIB_UDPROW_OWNER_PID*)(table + sizeof(uint));
        for (var i = 0; i < count; i++)
        {
            ref var row = ref rows[i];
            results.Add(new PortBinding(
                TransportProtocol.Udp,
                new IPAddress(row.dwLocalAddr),
                DecodePort(row.dwLocalPort),
                null,
                0,
                ConnectionState.Bound,
                (int)row.dwOwningPid));
        }
    }

    private static void ParseUdp6(byte* table, List<PortBinding> results)
    {
        var count = *(uint*)table;
        var rows = (MIB_UDP6ROW_OWNER_PID*)(table + sizeof(uint));
        for (var i = 0; i < count; i++)
        {
            var row = rows + i;
            results.Add(new PortBinding(
                TransportProtocol.Udp,
                ToAddress6(row->ucLocalAddr, row->dwLocalScopeId),
                DecodePort(row->dwLocalPort),
                null,
                0,
                ConnectionState.Bound,
                (int)row->dwOwningPid));
        }
    }

    private static IPAddress ToAddress6(byte* bytes, uint scopeId) =>
        new(new ReadOnlySpan<byte>(bytes, 16), scopeId);
}
