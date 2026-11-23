using System.Buffers.Text;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Discovery;

/// <summary>
/// Reads endpoint tables on Linux from <c>/proc/net/{tcp,tcp6,udp,udp6}</c>.
/// </summary>
/// <remarks>
/// procfs reports the socket inode rather than the owning PID. Mapping inodes to PIDs means walking
/// <c>/proc/[pid]/fd</c>, which is the expensive part of a scan, so the mapping is cached: a full walk
/// only happens when a socket appears that has not been seen before, and stops as soon as every new
/// inode has been found. Sockets owned by other users cannot be attributed without root; those are
/// remembered as unresolved and retried only occasionally.
/// </remarks>
public sealed class ProcNetPortTableReader(TimeProvider? clock = null, ILogger<ProcNetPortTableReader>? logger = null) : IPortTableReader
{
    private static readonly TimeSpan UnresolvedRetry = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<ProcNetPortTableReader>.Instance;
    private readonly Lock _gate = new();

    private byte[] _buffer = new byte[64 * 1024];

    // inode -> owning pid (0 when it could not be attributed).
    private Dictionary<ulong, int> _owners = [];
    private Dictionary<ulong, int> _nextOwners = [];
    private DateTimeOffset _lastFullWalk = DateTimeOffset.MinValue;

    /// <summary>One parsed row, before the inode is mapped to a PID.</summary>
    internal readonly record struct RawRow(TransportProtocol Protocol, IPAddress Local, int LocalPort, IPAddress? Remote, int RemotePort, ConnectionState State, ulong Inode);

    public IReadOnlyList<PortBinding> Read(bool includeAllTcpStates)
    {
        lock (_gate)
        {
            var rows = new List<RawRow>(256);
            ReadTable("/proc/net/tcp", TransportProtocol.Tcp, ipv6: false, includeAllTcpStates, rows);
            ReadTable("/proc/net/tcp6", TransportProtocol.Tcp, ipv6: true, includeAllTcpStates, rows);
            ReadTable("/proc/net/udp", TransportProtocol.Udp, ipv6: false, includeAllTcpStates, rows);
            ReadTable("/proc/net/udp6", TransportProtocol.Udp, ipv6: true, includeAllTcpStates, rows);

            ResolveOwners(rows);

            var results = new List<PortBinding>(rows.Count);
            foreach (var row in rows)
            {
                results.Add(new PortBinding(row.Protocol, row.Local, row.LocalPort, row.Remote, row.RemotePort, row.State, _owners.GetValueOrDefault(row.Inode)));
            }

            return results;
        }
    }

    private void ResolveOwners(List<RawRow> rows)
    {
        // Carry over known owners for sockets that still exist; anything left is new.
        _nextOwners.Clear();
        HashSet<ulong>? missing = null;
        HashSet<int>? checkedPids = null;
        foreach (var row in rows)
        {
            var inode = row.Inode;
            if (inode == 0 || _nextOwners.ContainsKey(inode))
            {
                continue;
            }

            if (_owners.TryGetValue(inode, out var pid) && (pid == 0 || StillAlive(pid, ref checkedPids)))
            {
                _nextOwners[inode] = pid;
            }
            else
            {
                (missing ??= []).Add(inode);
            }
        }

        // Unattributed sockets are retried periodically: their owner may have been mid-startup.
        var now = _clock.GetUtcNow();
        if (now - _lastFullWalk >= UnresolvedRetry)
        {
            foreach (var (inode, pid) in _nextOwners)
            {
                if (pid == 0)
                {
                    (missing ??= []).Add(inode);
                }
            }
        }

        (_owners, _nextOwners) = (_nextOwners, _owners);

        if (missing is { Count: > 0 })
        {
            var wanted = missing.Count;
            FindOwners(missing, _owners);
            _lastFullWalk = now;
            foreach (var inode in missing)
            {
                _owners[inode] = 0;
            }

            _log.LogDebug("Attributed {Found} of {Wanted} new sockets", wanted - missing.Count, wanted);
        }
    }

    private static bool StillAlive(int pid, ref HashSet<int>? checkedPids)
    {
        checkedPids ??= [];
        return checkedPids.Contains(pid) || (Directory.Exists($"/proc/{pid}/fd") && checkedPids.Add(pid));
    }

    /// <summary>Walks every process's descriptors until each inode in <paramref name="missing"/> is found.</summary>
    private static void FindOwners(HashSet<ulong> missing, Dictionary<ulong, int> owners)
    {
        Span<byte> path = stackalloc byte[64];
        Span<byte> target = stackalloc byte[64];
        var prefix = "socket:["u8;

        foreach (var pid in ProcFs.EnumerateNumeric("/proc"))
        {
            "/proc/"u8.CopyTo(path);
            var length = 6;
            length += WriteInt(path[length..], pid);
            "/fd/"u8.CopyTo(path[length..]);
            length += 4;
            var dirLength = length;

            IEnumerable<int> descriptors;
            try
            {
                descriptors = ProcFs.EnumerateNumeric($"/proc/{pid}/fd");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                foreach (var fd in descriptors)
                {
                    var end = dirLength + WriteInt(path[dirLength..], fd);
                    path[end] = 0;

                    var read = ProcFs.ReadLink(path[..(end + 1)], target);
                    if (read <= prefix.Length || !target.StartsWith(prefix))
                    {
                        continue;
                    }

                    if (Utf8Parser.TryParse(target[prefix.Length..read], out ulong inode, out _) && missing.Remove(inode))
                    {
                        owners[inode] = pid;
                        if (missing.Count == 0)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process exited or its descriptors are private.
            }
        }
    }

    private static int WriteInt(Span<byte> destination, int value)
    {
        Utf8Formatter.TryFormat(value, destination, out var written);
        return written;
    }

    private void ReadTable(string path, TransportProtocol protocol, bool ipv6, bool includeAllTcpStates, List<RawRow> rows)
    {
        var length = ProcFs.ReadAll(path, ref _buffer);
        if (length < 0)
        {
            // IPv6 may be disabled; that is not worth a warning.
            if (!ipv6)
            {
                _log.LogWarning("Could not read {Path}", path);
            }

            return;
        }

        var data = _buffer.AsSpan(0, length);

        // Skip the header line.
        var newline = data.IndexOf((byte)'\n');
        data = newline < 0 ? default : data[(newline + 1)..];

        while (!data.IsEmpty)
        {
            newline = data.IndexOf((byte)'\n');
            var line = newline < 0 ? data : data[..newline];
            data = newline < 0 ? default : data[(newline + 1)..];

            if (TryParseRow(line, protocol, ipv6, includeAllTcpStates, out var row))
            {
                rows.Add(row);
            }
        }
    }

    /// <summary>
    /// Parses one row: <c>sl local rem st tx:rx tr:when retrnsmt uid timeout inode ...</c>.
    /// </summary>
    internal static bool TryParseRow(ReadOnlySpan<byte> line, TransportProtocol protocol, bool ipv6, bool includeAllTcpStates, out RawRow row)
    {
        row = default;
        ReadOnlySpan<byte> local = default, remote = default, state = default, inode = default;
        for (var field = 0; field <= 9; field++)
        {
            line = line.TrimStart((byte)' ');
            if (line.IsEmpty)
            {
                return false;
            }

            var end = line.IndexOf((byte)' ');
            var token = end < 0 ? line : line[..end];
            line = end < 0 ? default : line[end..];

            switch (field)
            {
                case 1: local = token; break;
                case 2: remote = token; break;
                case 3: state = token; break;
                case 9: inode = token; break;
            }
        }

        if (!Utf8Parser.TryParse(state, out byte rawState, out _, 'x'))
        {
            return false;
        }

        ConnectionState connection;
        if (protocol == TransportProtocol.Udp)
        {
            connection = ConnectionState.Bound;
        }
        else
        {
            connection = MapTcpState(rawState);
            if (!includeAllTcpStates && connection != ConnectionState.Listening)
            {
                return false;
            }
        }

        if (!TryParseEndpoint(local, ipv6, out var localAddress, out var localPort)
            || !Utf8Parser.TryParse(inode, out ulong inodeValue, out _))
        {
            return false;
        }

        IPAddress? remoteAddress = null;
        var remotePort = 0;
        if (protocol == TransportProtocol.Tcp && connection != ConnectionState.Listening
            && !TryParseEndpoint(remote, ipv6, out remoteAddress, out remotePort))
        {
            return false;
        }

        row = new RawRow(protocol, localAddress, localPort, remoteAddress, remotePort, connection, inodeValue);
        return true;
    }

    /// <summary>Parses <c>0100007F:0CEA</c>; addresses are 32-bit words printed in host byte order.</summary>
    internal static bool TryParseEndpoint(ReadOnlySpan<byte> token, bool ipv6, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;
        var colon = token.IndexOf((byte)':');
        var hexLength = ipv6 ? 32 : 8;
        if (colon != hexLength || !Utf8Parser.TryParse(token[(colon + 1)..], out ushort rawPort, out _, 'x'))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        for (var word = 0; word < hexLength / 8; word++)
        {
            if (!Utf8Parser.TryParse(token.Slice(word * 8, 8), out uint value, out _, 'x'))
            {
                return false;
            }

            MemoryMarshal.Write(bytes[(word * 4)..], in value);
        }

        address = ipv6 ? new IPAddress(bytes) : new IPAddress(bytes[..4]);
        port = rawPort;
        return true;
    }

    private static ConnectionState MapTcpState(byte state) => state switch
    {
        0x01 => ConnectionState.Established,
        0x02 => ConnectionState.SynSent,
        0x03 => ConnectionState.SynReceived,
        0x04 => ConnectionState.FinWait1,
        0x05 => ConnectionState.FinWait2,
        0x06 => ConnectionState.TimeWait,
        0x07 => ConnectionState.Closed,
        0x08 => ConnectionState.CloseWait,
        0x09 => ConnectionState.LastAck,
        0x0A => ConnectionState.Listening,
        0x0B => ConnectionState.Closing,
        _ => ConnectionState.Unknown,
    };
}
