using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Tests;

[TestClass]
public sealed class ProcNetParsingTests
{
    [TestMethod]
    public void Parses_ipv4_listener_row()
    {
        var line = "   0: 0100007F:0CEA 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 123456 1 0000000000000000 100 0 0 10 0"u8;

        Assert.IsTrue(ProcNetPortTableReader.TryParseRow(line, TransportProtocol.Tcp, ipv6: false, includeAllTcpStates: false, out var row));

        Assert.AreEqual(IPAddress.Loopback, row.Local);
        Assert.AreEqual(3306, row.LocalPort);
        Assert.AreEqual(ConnectionState.Listening, row.State);
        Assert.IsNull(row.Remote);
        Assert.AreEqual(123456UL, row.Inode);
    }

    [TestMethod]
    public void Established_rows_are_skipped_unless_all_states_are_requested()
    {
        var line = "   1: 0100007F:1435 0100007F:C350 01 00000000:00000000 00:00000000 00000000  1000        0 99 1"u8;

        Assert.IsFalse(ProcNetPortTableReader.TryParseRow(line, TransportProtocol.Tcp, false, false, out _));
        Assert.IsTrue(ProcNetPortTableReader.TryParseRow(line, TransportProtocol.Tcp, false, true, out var row));
        Assert.AreEqual(ConnectionState.Established, row.State);
        Assert.AreEqual(5173, row.LocalPort);
        Assert.AreEqual(50000, row.RemotePort);
    }

    [TestMethod]
    public void Parses_ipv6_addresses_in_host_word_order()
    {
        Assert.IsTrue(ProcNetPortTableReader.TryParseEndpoint("00000000000000000000000001000000:1F90"u8, ipv6: true, out var address, out var port));
        Assert.AreEqual(IPAddress.IPv6Loopback, address);
        Assert.AreEqual(8080, port);

        Assert.IsTrue(ProcNetPortTableReader.TryParseEndpoint("00000000000000000000000000000000:0050"u8, ipv6: true, out var any, out _));
        Assert.AreEqual(IPAddress.IPv6Any, any);
    }

    [TestMethod]
    public void Rejects_malformed_rows()
    {
        Assert.IsFalse(ProcNetPortTableReader.TryParseRow("garbage"u8, TransportProtocol.Tcp, false, true, out _));
        Assert.IsFalse(ProcNetPortTableReader.TryParseEndpoint("0100007F"u8, false, out _, out _));
    }
}

[TestClass]
[OSCondition(OperatingSystems.Linux)]
public sealed class LinuxPortTableReaderTests
{
    private readonly ProcNetPortTableReader _reader = new();

    [TestMethod]
    public void Finds_tcp_and_udp_endpoints_owned_by_this_process()
    {
        using var tcp4 = new TcpListener(IPAddress.Loopback, 0);
        using var tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        tcp4.Start();
        tcp6.Start();
        using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var tcp4Port = ((IPEndPoint)tcp4.LocalEndpoint).Port;
        var tcp6Port = ((IPEndPoint)tcp6.LocalEndpoint).Port;
        var udpPort = ((IPEndPoint)udp4.Client.LocalEndPoint!).Port;

        var bindings = _reader.Read(false);

        var t4 = bindings.Single(b => b.Protocol == TransportProtocol.Tcp && b.LocalPort == tcp4Port);
        Assert.AreEqual(Environment.ProcessId, t4.ProcessId);
        Assert.AreEqual(IPAddress.Loopback, t4.LocalAddress);
        Assert.AreEqual(ConnectionState.Listening, t4.State);

        var t6 = bindings.Single(b => b.Protocol == TransportProtocol.Tcp && b.LocalPort == tcp6Port);
        Assert.AreEqual(IPAddress.IPv6Loopback, t6.LocalAddress);
        Assert.AreEqual(Environment.ProcessId, t6.ProcessId);

        var u = bindings.Single(b => b.Protocol == TransportProtocol.Udp && b.LocalPort == udpPort);
        Assert.AreEqual(ConnectionState.Bound, u.State);
        Assert.AreEqual(Environment.ProcessId, u.ProcessId);
    }

    [TestMethod]
    public void All_states_mode_includes_established_connections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var server = listener.AcceptTcpClient();
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        Assert.IsFalse(_reader.Read(false).Any(b => b.LocalPort == clientPort));
        var established = _reader.Read(true).Single(b => b.LocalPort == clientPort && b.Protocol == TransportProtocol.Tcp);
        Assert.AreEqual(ConnectionState.Established, established.State);
        Assert.AreEqual(port, established.RemotePort);
        Assert.AreEqual(Environment.ProcessId, established.ProcessId);
    }

    [TestMethod]
    public void Closed_listener_disappears_and_new_one_is_attributed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.IsTrue(_reader.Read(false).Any(b => b.LocalPort == port));
        listener.Stop();
        Assert.IsFalse(_reader.Read(false).Any(b => b.LocalPort == port && b.State == ConnectionState.Listening));

        // A socket created after the cache was warm must still be attributed.
        using var second = new TcpListener(IPAddress.Loopback, 0);
        second.Start();
        var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
        Assert.AreEqual(Environment.ProcessId, _reader.Read(false).Single(b => b.LocalPort == secondPort).ProcessId);
    }

    [TestMethod]
    public void Scanner_joins_endpoints_with_process_details()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var snapshot = PlatformServices.CreateScanner().Scan(false);

        var entry = snapshot.Entries.Single(e => e.Port == port && e.Binding.Protocol == TransportProtocol.Tcp);
        Assert.AreEqual(ProcessAccessLevel.Full, entry.Process.Access);
        Assert.AreEqual(Environment.ProcessPath, entry.Process.ExecutablePath);
        Assert.AreEqual(snapshot.Entries.Count, snapshot.Entries.Select(e => e.Key).Distinct().Count());
    }
}

[TestClass]
[OSCondition(OperatingSystems.Linux)]
public sealed class LinuxProcessTests
{
    private readonly LinuxProcessTerminator _terminator = new();

    private static Process Sleep(string seconds = "120") => Process.Start(new ProcessStartInfo("sleep", seconds) { UseShellExecute = false })!;

    [TestMethod]
    public void Inspects_the_current_process()
    {
        using var self = Process.GetCurrentProcess();
        var details = LinuxProcessInfoProvider.Inspect(Environment.ProcessId);

        Assert.AreEqual(ProcessAccessLevel.Full, details.Access);
        Assert.AreEqual(Environment.ProcessPath, details.ExecutablePath);
        Assert.AreEqual(Path.GetFileName(Environment.ProcessPath), details.ImageName);
        Assert.IsNotNull(details.CommandLine);
        Assert.IsTrue(Math.Abs((self.StartTime.ToUniversalTime() - details.StartTime!.Value.UtcDateTime).TotalSeconds) < 2);
    }

    [TestMethod]
    public void Reads_command_line_of_child_process()
    {
        using var child = Sleep("120");
        try
        {
            // The child may still be between fork and exec for a moment.
            var details = LinuxProcessInfoProvider.Inspect(child.Id);
            for (var i = 0; i < 40 && details.CommandLine != "sleep 120"; i++)
            {
                Thread.Sleep(25);
                details = LinuxProcessInfoProvider.Inspect(child.Id);
            }

            Assert.AreEqual("sleep", details.ImageName);
            Assert.AreEqual("sleep 120", details.CommandLine);
            Assert.AreEqual(Environment.ProcessId, details.ParentProcessId);
        }
        finally
        {
            child.Kill();
        }
    }

    [TestMethod]
    public void Unknown_owner_and_missing_pids_are_reported_gracefully()
    {
        Assert.AreEqual(ProcessAccessLevel.Restricted, LinuxProcessInfoProvider.Inspect(0).Access);
        Assert.AreEqual(ProcessAccessLevel.Exited, LinuxProcessInfoProvider.Inspect(int.MaxValue - 1).Access);
    }

    [TestMethod]
    public void Resolve_caches_and_detects_exit()
    {
        var provider = new LinuxProcessInfoProvider();
        var child = Sleep();
        var first = provider.Resolve([child.Id, Environment.ProcessId]);
        var second = provider.Resolve([child.Id, Environment.ProcessId]);
        Assert.AreSame(first[child.Id], second[child.Id]);

        child.Kill();
        child.WaitForExit();
        Assert.AreNotEqual(ProcessAccessLevel.Full, provider.Resolve([child.Id])[child.Id].Access);
        child.Dispose();
    }

    [TestMethod]
    public async Task Terminates_a_process_and_waits_for_exit()
    {
        using var child = Sleep();
        var start = LinuxProcessInfoProvider.Inspect(child.Id).StartTime;

        Assert.AreEqual(TerminationAccess.Allowed, _terminator.ProbeAccess(child.Id));
        var result = await _terminator.TerminateAsync(child.Id, start);

        Assert.AreEqual(TerminationOutcome.Terminated, result.Outcome);
        Assert.IsTrue(child.WaitForExit(1000));
    }

    [TestMethod]
    public async Task Escalates_to_sigkill_when_sigterm_is_ignored()
    {
        using var child = Process.Start(new ProcessStartInfo("sh", ["-c", "trap '' TERM; sleep 120 & wait"]) { UseShellExecute = false })!;
        await Task.Delay(100);

        var result = await _terminator.TerminateAsync(child.Id, null);

        Assert.AreEqual(TerminationOutcome.Terminated, result.Outcome);
    }

    [TestMethod]
    public async Task Refuses_when_pid_was_reused()
    {
        using var child = Sleep();
        try
        {
            var wrong = LinuxProcessInfoProvider.Inspect(child.Id).StartTime!.Value.AddSeconds(-30);
            Assert.AreEqual(TerminationOutcome.ProcessChanged, (await _terminator.TerminateAsync(child.Id, wrong)).Outcome);
            Assert.IsFalse(child.HasExited);
        }
        finally
        {
            child.Kill();
        }
    }

    [TestMethod]
    public async Task Protects_init_self_and_reports_other_users()
    {
        Assert.AreEqual(TerminationOutcome.Protected, (await _terminator.TerminateAsync(1, null)).Outcome);
        Assert.AreEqual(TerminationOutcome.Protected, (await _terminator.TerminateAsync(Environment.ProcessId, null)).Outcome);
        Assert.AreEqual(TerminationAccess.Protected, _terminator.ProbeAccess(2));
        Assert.AreEqual(TerminationAccess.NotRunning, _terminator.ProbeAccess(int.MaxValue - 1));

        if (Environment.UserName != "root")
        {
            // Find any process owned by root other than init.
            var rootPid = Directory.EnumerateDirectories("/proc")
                .Select(d => int.TryParse(Path.GetFileName(d), out var p) ? p : 0)
                .Where(p => p > 2)
                .FirstOrDefault(p => File.Exists($"/proc/{p}/status")
                    && Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{p}/status")).Contains("\nUid:\t0\t", StringComparison.Ordinal)
                    && LinuxProcessInfoProvider.Inspect(p).Access != ProcessAccessLevel.System);
            if (rootPid > 0)
            {
                Assert.AreEqual(TerminationAccess.RequiresElevation, _terminator.ProbeAccess(rootPid));
            }
        }
    }
}

[TestClass]
[OSCondition(OperatingSystems.OSX)]
public sealed class MacTests
{
    [TestMethod]
    public void Finds_tcp_and_udp_endpoints_owned_by_this_process()
    {
        using var tcp4 = new TcpListener(IPAddress.Loopback, 0);
        using var tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        tcp4.Start();
        tcp6.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var p4 = ((IPEndPoint)tcp4.LocalEndpoint).Port;
        var p6 = ((IPEndPoint)tcp6.LocalEndpoint).Port;
        var pu = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var bindings = new MacPortTableReader().Read(false);

        var t4 = bindings.Single(b => b.Protocol == TransportProtocol.Tcp && b.LocalPort == p4);
        Assert.AreEqual(IPAddress.Loopback, t4.LocalAddress);
        Assert.AreEqual(ConnectionState.Listening, t4.State);
        Assert.AreEqual(Environment.ProcessId, t4.ProcessId);
        Assert.AreEqual(IPAddress.IPv6Loopback, bindings.Single(b => b.Protocol == TransportProtocol.Tcp && b.LocalPort == p6).LocalAddress);
        Assert.AreEqual(ConnectionState.Bound, bindings.Single(b => b.Protocol == TransportProtocol.Udp && b.LocalPort == pu).State);
    }

    [TestMethod]
    public void All_states_mode_includes_established_connections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var server = listener.AcceptTcpClient();
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var reader = new MacPortTableReader();
        Assert.IsFalse(reader.Read(false).Any(b => b.LocalPort == clientPort));
        var established = reader.Read(true).Single(b => b.LocalPort == clientPort && b.Protocol == TransportProtocol.Tcp);
        Assert.AreEqual(ConnectionState.Established, established.State);
        Assert.AreEqual(port, established.RemotePort);
    }

    [TestMethod]
    public void Inspects_this_process_and_a_child()
    {
        var self = MacProcessInfoProvider.Inspect(Environment.ProcessId);
        Assert.AreEqual(ProcessAccessLevel.Full, self.Access);
        Assert.AreEqual(Environment.ProcessPath, self.ExecutablePath);
        Assert.AreEqual(Directory.GetCurrentDirectory(), self.WorkingDirectory);
        Assert.IsNotNull(self.StartTime);

        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var details = MacProcessInfoProvider.Inspect(child.Id);
            Assert.AreEqual("sleep", details.ImageName);
            Assert.AreEqual("sleep 30", details.CommandLine);
            Assert.AreEqual(Environment.ProcessId, details.ParentProcessId);
            Assert.IsTrue(Math.Abs((child.StartTime.ToUniversalTime() - details.StartTime!.Value.UtcDateTime).TotalSeconds) < 2);
        }
        finally
        {
            child.Kill();
        }
    }

    [TestMethod]
    public async Task Terminates_and_protects()
    {
        var terminator = new LinuxProcessTerminator();
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        var start = MacProcessInfoProvider.Inspect(child.Id).StartTime;
        Assert.AreEqual(TerminationAccess.Allowed, terminator.ProbeAccess(child.Id));
        Assert.AreEqual(TerminationOutcome.ProcessChanged, (await terminator.TerminateAsync(child.Id, start!.Value.AddSeconds(-30))).Outcome);
        Assert.AreEqual(TerminationOutcome.Terminated, (await terminator.TerminateAsync(child.Id, start)).Outcome);
        Assert.AreEqual(TerminationOutcome.Protected, (await terminator.TerminateAsync(1, null)).Outcome);
    }

    [TestMethod]
    public void Scanner_joins_endpoints_with_process_details()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var entry = PlatformServices.CreateScanner().Scan(false).Entries.Single(e => e.Port == port && e.Binding.Protocol == TransportProtocol.Tcp);
        Assert.AreEqual(Environment.ProcessPath, entry.Process.ExecutablePath);
    }
}
