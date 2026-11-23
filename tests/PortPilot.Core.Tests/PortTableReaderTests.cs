using System.Net;
using System.Net.Sockets;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class PortTableReaderTests
{
    private readonly IpHelperPortTableReader _reader = new();

    [TestMethod]
    public void Finds_tcp_ipv4_listener_owned_by_this_process()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var bindings = _reader.Read(includeAllTcpStates: false);

        var match = bindings.SingleOrDefault(b => b.Protocol == TransportProtocol.Tcp && b.LocalPort == port && !b.IsIPv6);
        Assert.IsNotNull(match, $"Expected a TCP listener on {port}");
        Assert.AreEqual(Environment.ProcessId, match.ProcessId);
        Assert.AreEqual(ConnectionState.Listening, match.State);
        Assert.AreEqual(IPAddress.Loopback, match.LocalAddress);
        Assert.IsNull(match.RemoteAddress);
    }

    [TestMethod]
    public void Finds_tcp_ipv6_listener()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var match = _reader.Read(false).SingleOrDefault(b => b.Protocol == TransportProtocol.Tcp && b.LocalPort == port);

        Assert.IsNotNull(match);
        Assert.IsTrue(match.IsIPv6);
        Assert.AreEqual(IPAddress.IPv6Loopback, match.LocalAddress);
        Assert.AreEqual(Environment.ProcessId, match.ProcessId);
    }

    [TestMethod]
    public void Finds_udp_endpoints_for_both_families()
    {
        using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var port4 = ((IPEndPoint)udp4.Client.LocalEndPoint!).Port;
        var port6 = ((IPEndPoint)udp6.Client.LocalEndPoint!).Port;

        var bindings = _reader.Read(false);

        var match4 = bindings.SingleOrDefault(b => b.Protocol == TransportProtocol.Udp && b.LocalPort == port4 && !b.IsIPv6);
        var match6 = bindings.SingleOrDefault(b => b.Protocol == TransportProtocol.Udp && b.LocalPort == port6 && b.IsIPv6);
        Assert.IsNotNull(match4);
        Assert.IsNotNull(match6);
        Assert.AreEqual(ConnectionState.Bound, match4.State);
        Assert.IsTrue(match4.IsListening);
        Assert.AreEqual(Environment.ProcessId, match6.ProcessId);
    }

    [TestMethod]
    public void Listener_only_mode_excludes_established_connections_but_all_states_mode_includes_them()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var server = listener.AcceptTcpClient();
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var listenersOnly = _reader.Read(includeAllTcpStates: false);
        var everything = _reader.Read(includeAllTcpStates: true);

        Assert.IsFalse(listenersOnly.Any(b => b.LocalPort == clientPort && b.Protocol == TransportProtocol.Tcp));
        var established = everything.SingleOrDefault(b => b.LocalPort == clientPort && b.Protocol == TransportProtocol.Tcp);
        Assert.IsNotNull(established);
        Assert.AreEqual(ConnectionState.Established, established.State);
        Assert.AreEqual(port, established.RemotePort);
        Assert.IsFalse(established.IsListening);
    }

    [TestMethod]
    public void Closed_listener_disappears_from_the_table()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.IsTrue(_reader.Read(false).Any(b => b.LocalPort == port && b.Protocol == TransportProtocol.Tcp));

        listener.Stop();

        Assert.IsFalse(_reader.Read(false).Any(b => b.LocalPort == port && b.Protocol == TransportProtocol.Tcp && b.ProcessId == Environment.ProcessId));
    }

    [TestMethod]
    public void Port_numbers_are_decoded_from_network_byte_order()
    {
        Assert.AreEqual(5173, IpHelperDecode(0x0000_3514));
        Assert.AreEqual(80, IpHelperDecode(0x0000_5000));
        Assert.AreEqual(65535, IpHelperDecode(0x0000_FFFF));
    }

    private static int IpHelperDecode(uint value) => Interop.IpHelper.DecodePort(value);

    [TestMethod]
    public void Scanner_joins_endpoints_with_process_details()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var scanner = new PortScanner(_reader, new ProcessInfoProvider());

        var snapshot = scanner.Scan(false);

        var entry = snapshot.Entries.Single(e => e.Port == port && e.Binding.Protocol == TransportProtocol.Tcp && !e.Binding.IsIPv6);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.AreEqual(ProcessAccessLevel.Full, entry.Process.Access);
        Assert.AreEqual(self.MainModule!.FileName, entry.Process.ExecutablePath, ignoreCase: true);
        Assert.IsNotNull(entry.Process.StartTime);
        Assert.AreEqual(snapshot.Entries.Count, snapshot.Entries.Select(e => e.Key).Distinct().Count(), "Keys must be unique");
    }
}
