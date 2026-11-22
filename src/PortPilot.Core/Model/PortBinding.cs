using System.Net;
using System.Net.Sockets;

namespace PortPilot.Core.Model;

public enum TransportProtocol
{
    Tcp,
    Udp,
}

/// <summary>
/// Connection state. Mirrors MIB_TCP_STATE; UDP endpoints are always <see cref="Bound"/>.
/// </summary>
public enum ConnectionState
{
    Unknown = 0,
    Closed = 1,
    Listening = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
    Bound = 100,
}

/// <summary>
/// A single socket endpoint reported by the operating system.
/// </summary>
public sealed record PortBinding(
    TransportProtocol Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int RemotePort,
    ConnectionState State,
    int ProcessId)
{
    public bool IsIPv6 => LocalAddress.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>True for TCP listeners and bound UDP sockets – the endpoints other software can reach.</summary>
    public bool IsListening => State is ConnectionState.Listening or ConnectionState.Bound;

    /// <summary>Stable identity used to correlate the same endpoint across snapshots.</summary>
    public string Key => RemoteAddress is null
        ? $"{Protocol}|{LocalAddress}|{LocalPort}|{ProcessId}"
        : $"{Protocol}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}|{ProcessId}";
}
