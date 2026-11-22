using System.Diagnostics;
using System.Net;
using PortPilot.Core.Model;

namespace PortPilot.Core.Tests;

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public ManualClock() : this(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class Entries
{
    public static PortEntry Tcp(int port, int pid = 1000, string image = "node.exe", string address = "127.0.0.1", ConnectionState state = ConnectionState.Listening) =>
        Create(TransportProtocol.Tcp, port, pid, image, address, state);

    public static PortEntry Udp(int port, int pid = 1000, string image = "node.exe", string address = "0.0.0.0") =>
        Create(TransportProtocol.Udp, port, pid, image, address, ConnectionState.Bound);

    private static PortEntry Create(TransportProtocol protocol, int port, int pid, string image, string address, ConnectionState state)
    {
        var listening = state is ConnectionState.Listening or ConnectionState.Bound;
        var binding = new PortBinding(
            protocol,
            IPAddress.Parse(address),
            port,
            listening ? null : IPAddress.Loopback,
            listening ? 0 : 50000,
            state,
            pid);
        var process = new ProcessDetails
        {
            ProcessId = pid,
            ImageName = image,
            ExecutablePath = $@"C:\Tools\{image}",
            Access = ProcessAccessLevel.Full,
        };
        return new PortEntry(binding, process, Discovery.WellKnownPorts.Lookup(port));
    }

    public static PortSnapshot Snapshot(params PortEntry[] entries) => new(entries, DateTimeOffset.Now, TimeSpan.Zero);
}

/// <summary>A disposable child process that sleeps until killed.</summary>
internal sealed class SleepingProcess : IDisposable
{
    public SleepingProcess()
    {
        Process = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
    }

    public Process Process { get; }

    public int Id => Process.Id;

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        Process.Dispose();
    }
}
