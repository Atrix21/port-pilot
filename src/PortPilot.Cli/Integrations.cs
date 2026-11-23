using PortPilot.Core;
using PortPilot.Core.Discovery;
using PortPilot.Core.Integrations;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Cli;

/// <summary>The CLI side of the agent integrations: the hook and watcher themselves, plus install commands.</summary>
internal static class Integrations
{
    private static string Self => Environment.ProcessPath ?? "portpilot";

    /// <summary>
    /// Runs as a Claude Code SessionEnd hook. The session is still alive at this point, with everything it
    /// started still attached below it, so this is the moment to record that tree. Once the session exits,
    /// those processes are recognised as left behind by every PortPilot, even ones that weren't running.
    /// </summary>
    public static async Task<int> SessionEndHookAsync(bool stop)
    {
        // The hook receives JSON on stdin (session_id, cwd, reason). Drain it so Claude isn't blocked on the pipe.
        if (Console.IsInputRedirected)
        {
            _ = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        }

        var self = Environment.ProcessId;
        if (ProcessTree.FindAgentAbove(self) is not { } agent)
        {
            return 0;
        }

        // Skip the hook's own process chain (the shell Claude used to run it, and this process).
        var parents = ProcessTree.ParentMap();
        var exclude = new HashSet<int> { self };
        for (var p = self; parents.TryGetValue(p, out var parent) && parent != agent.ProcessId && parent > 1; p = parent)
        {
            exclude.Add(parent);
        }

        var recorded = ProcessTree.CaptureSession(agent, exclude);
        if (!stop || recorded.Count == 0)
        {
            return 0;
        }

        // Optionally stop what is still holding ports: dev servers, watchers with live-reload sockets, …
        var listening = PlatformServices.CreateScanner().Scan(false).Entries
            .Where(e => recorded.Contains(e.ProcessId))
            .GroupBy(e => e.ProcessId)
            .Select(g => g.First())
            .ToList();
        var terminator = PlatformServices.CreateProcessTerminator();
        await Task.WhenAll(listening.Select(e => terminator.TerminateAsync(e.ProcessId, e.Process.StartTime))).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Scans quietly in the background so agent sessions are recorded even when no window is open.</summary>
    public static async Task<int> WatchAsync()
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var term = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stop.Cancel();
        });

        await using var monitor = new PortMonitor(PlatformServices.CreateScanner(), TimeSpan.FromSeconds(3));
        monitor.Start();
        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    public static int InstallHooks(bool stop) => Report(AgentIntegration.InstallHook(Self, stop));

    public static int UninstallHooks() => Report(AgentIntegration.UninstallHook());

    public static int InstallWatcher() => Report(AgentIntegration.InstallWatcher(Self));

    public static int UninstallWatcher() => Report(AgentIntegration.UninstallWatcher());

    public static int RegisterMcp() => Report(AgentIntegration.RegisterMcp(Self));

    /// <summary>Hook, watcher and MCP server in one go.</summary>
    public static int SetupClaude(bool stop)
    {
        var results = new[] { InstallHooks(stop), InstallWatcher(), RegisterMcp() };
        return results.All(r => r == 0) ? 0 : 1;
    }

    private static int Report(IntegrationResult result)
    {
        (result.Succeeded ? Console.Out : Console.Error).WriteLine(result.Message);
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>Picks the processes to show for a port argument (":3000", "3000") or "pid:123".</summary>
    public static List<PortEntry> Resolve(string target, IReadOnlyList<PortEntry> entries)
    {
        if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) && int.TryParse(target[4..], out var pid))
        {
            return entries.Where(e => e.ProcessId == pid).ToList();
        }

        return int.TryParse(target.TrimStart(':'), out var port) ? entries.Where(e => e.Port == port).ToList() : [];
    }
}
