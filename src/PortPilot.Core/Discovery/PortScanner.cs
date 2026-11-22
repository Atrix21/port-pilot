using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Discovery;

public interface IPortScanner
{
    PortSnapshot Scan(bool includeAllTcpStates);
}

/// <summary>
/// Joins the endpoint tables with process details into a <see cref="PortSnapshot"/>.
/// </summary>
public sealed class PortScanner(
    IPortTableReader reader,
    IProcessInfoProvider processes,
    TimeProvider? clock = null,
    ILogger<PortScanner>? logger = null) : IPortScanner
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<PortScanner>.Instance;

    public PortSnapshot Scan(bool includeAllTcpStates)
    {
        var started = Stopwatch.GetTimestamp();

        var bindings = reader.Read(includeAllTcpStates);
        var pids = bindings.Select(b => b.ProcessId).ToHashSet();
        var details = processes.Resolve(pids);

        var entries = new List<PortEntry>(bindings.Count);
        var seen = new HashSet<string>(bindings.Count);
        foreach (var binding in bindings)
        {
            // The OS occasionally reports the same endpoint twice while a socket is being torn down.
            if (!seen.Add(binding.Key))
            {
                continue;
            }

            var process = details.GetValueOrDefault(binding.ProcessId) ?? ProcessDetails.Unknown(binding.ProcessId);
            entries.Add(new PortEntry(binding, process, WellKnownPorts.Lookup(binding.LocalPort)));
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        _log.LogDebug("Scan found {Endpoints} endpoints across {Processes} processes in {ElapsedMs:0.0} ms", entries.Count, pids.Count, elapsed.TotalMilliseconds);
        return new PortSnapshot(entries, _clock.GetLocalNow(), elapsed);
    }
}
