using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PortPilot.Core.Diagnostics;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Tests;

[TestClass]
public sealed class AgentDetectorTests
{
    [TestCleanup]
    public void Reset() => AgentDetector.Configure([]);

    [TestMethod]
    public void Recognises_agents_by_executable_and_by_runtime_command_line()
    {
        Assert.AreEqual("Claude Code", AgentDetector.Identify("claude", () => null)?.Name);
        Assert.AreEqual("Claude Code", AgentDetector.Identify("claude.exe", () => null)?.Name);
        Assert.AreEqual("Codex", AgentDetector.Identify("node", () => "node /usr/lib/node_modules/@openai/codex/bin/codex.js")?.Name);
        Assert.AreEqual("Gemini CLI", AgentDetector.Identify("node", () => "node /usr/local/bin/gemini")?.Name);
        Assert.AreEqual("Aider", AgentDetector.Identify("python3", () => "python3 -m aider --model sonnet")?.Name);
        Assert.IsNull(AgentDetector.Identify("node", () => "node server.js"));
        Assert.IsNull(AgentDetector.Identify("bash", () => "bash -c claude"));
    }

    [TestMethod]
    public void Custom_agents_extend_the_list()
    {
        Assert.IsNull(AgentDetector.Identify("devin", () => null));
        AgentDetector.Configure(["devin.exe", " ", "claude"]);
        Assert.AreEqual("devin", AgentDetector.Identify("devin", () => null)?.Name);
        Assert.AreEqual(1, AgentDetector.Agents.Count(a => a.Name == "Claude Code"));
        Assert.AreEqual("Claude", AgentDetector.ShortNameFor("Claude Code"));
    }
}

[TestClass]
public sealed class AgentLineageTests
{
    [TestMethod]
    public void History_is_shared_through_the_file_and_dropped_for_exited_processes()
    {
        var path = Path.Combine(Path.GetTempPath(), "pp-lineage-" + Guid.NewGuid().ToString("N"));
        var start = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        var live = new Dictionary<int, DateTimeOffset> { [100] = start, [200] = start };
        DateTimeOffset? StartOf(int pid) => live.TryGetValue(pid, out var s) ? s : null;

        try
        {
            var agent = new AgentSession(200, start, "Claude Code", "/code", IsRunning: true);
            var writer = new AgentLineage(StartOf, path);
            writer.Record(100, start, agent);
            writer.Flush();

            // A second PortPilot sees the attribution; once the agent exits, the process is left behind.
            live.Remove(200);
            var reader = new AgentLineage(StartOf, path);
            var details = reader.Apply(new ProcessDetails { ProcessId = 100, ImageName = "node", StartTime = start });
            Assert.IsTrue(details.IsOrphaned);
            Assert.AreEqual("Claude Code", details.Agent!.Name);

            // A PID reused by a different process is not attributed.
            live[100] = start.AddMinutes(5);
            var other = new AgentLineage(StartOf, path);
            Assert.IsNull(other.Apply(new ProcessDetails { ProcessId = 100, ImageName = "node", StartTime = start.AddMinutes(5) }).Agent);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

[TestClass]
public sealed class DiagnosticsTests
{
    [TestMethod]
    public async Task Health_probe_reports_http_status_and_unreachable_ports()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 204;
            context.Response.Close();
        });

        var binding = new PortBinding(TransportProtocol.Tcp, IPAddress.Loopback, port, null, 0, ConnectionState.Listening, Environment.ProcessId);
        var ok = await HealthProbe.CheckAsync(binding, null);
        await serve;
        Assert.AreEqual(HealthState.Healthy, ok.State);
        Assert.AreEqual(204, ok.StatusCode);

        listener.Stop();
        var down = await HealthProbe.CheckAsync(binding with { LocalPort = FreePort() }, null);
        Assert.AreEqual(HealthState.Unreachable, down.State);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void Logs_are_read_from_a_redirected_file_and_explained_otherwise()
    {
        var log = Path.GetTempFileName();
        try
        {
            using var child = Process.Start(new ProcessStartInfo("sh", ["-c", $"for i in 1 2 3; do printf '\\033[32mline %s\\033[0m\\n' $i; done > {log}; exec sleep 30 >> {log} 2>&1"]) { UseShellExecute = false })!;
            Thread.Sleep(300);
            try
            {
                var tail = ProcessLogs.Read(child.Id, 2);
                Assert.IsTrue(tail.IsAvailable, tail.Unavailable);
                Assert.AreEqual(log, tail.Source);
                CollectionAssert.AreEqual(new[] { "line 2", "line 3" }, tail.Lines.ToArray());
            }
            finally
            {
                child.Kill();
            }
        }
        finally
        {
            File.Delete(log);
        }

        Assert.IsFalse(ProcessLogs.Read(0).IsAvailable);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void Session_capture_records_every_descendant_of_the_agent()
    {
        using var parent = Process.Start(new ProcessStartInfo("sh", ["-c", "sleep 30 & sleep 30 & wait"]) { UseShellExecute = false })!;
        Thread.Sleep(200);
        try
        {
            var descendants = ProcessTree.Descendants(parent.Id);
            Assert.AreEqual(2, descendants.Count);
            Assert.IsTrue(descendants.All(pid => ProcessTree.StartTimeOf(pid) is not null));
        }
        finally
        {
            foreach (var pid in ProcessTree.Descendants(parent.Id))
            {
                try { Process.GetProcessById(pid).Kill(); } catch (ArgumentException) { }
            }

            parent.Kill();
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
