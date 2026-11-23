using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Tests;

[TestClass]
public sealed class ProjectResolverTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pp-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void Finds_repository_and_branch_from_a_subdirectory()
    {
        var repo = Path.Combine(_root, "atlas-web");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/feature/auth\n");
        var nested = Directory.CreateDirectory(Path.Combine(repo, "packages", "ui")).FullName;

        var project = ProjectResolver.Resolve(nested);

        Assert.IsNotNull(project);
        Assert.AreEqual("atlas-web", project.Name);
        Assert.AreEqual("feature/auth", project.Branch);
        Assert.AreEqual(repo, project.Root);
        Assert.IsFalse(project.IsWorktree);
        Assert.AreEqual("atlas-web · feature/auth", project.DisplayText);
    }

    [TestMethod]
    public void Worktrees_are_named_after_their_main_checkout()
    {
        var main = Path.Combine(_root, "ledger");
        var worktreeGit = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "ledger-fix")).FullName;
        File.WriteAllText(Path.Combine(worktreeGit, "HEAD"), "ref: refs/heads/fix/rounding\n");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "ledger-fix")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}\n");

        var project = ProjectResolver.Resolve(worktree);

        Assert.IsNotNull(project);
        Assert.AreEqual("ledger", project.Name);
        Assert.AreEqual("fix/rounding", project.Branch);
        Assert.IsTrue(project.IsWorktree);
        Assert.AreEqual(worktree, project.Root);
    }

    [TestMethod]
    public void Detached_head_shows_the_short_commit_and_non_repos_resolve_to_null()
    {
        var repo = Path.Combine(_root, "detached");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");

        Assert.AreEqual("0123456", ProjectResolver.Resolve(repo)?.Branch);
        Assert.IsNull(ProjectResolver.Resolve(Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName));
        Assert.IsNull(ProjectResolver.Resolve(null));
    }

    [TestMethod]
    public void Filter_matches_projects_and_orphans()
    {
        var entry = Entries.Tcp(5173) with
        {
            Process = Entries.Tcp(5173).Process with
            {
                Project = new ProjectInfo("/code/atlas-web", "atlas-web", "main", false),
                Agent = new AgentSession(42, null, "Claude Code", null, IsRunning: false),
            },
        };
        var other = Entries.Tcp(8000);

        Assert.IsTrue(new PortFilter("project:atlas").Matches(entry));
        Assert.IsFalse(new PortFilter("project:atlas").Matches(other));
        Assert.IsTrue(new PortFilter("atlas-web").Matches(entry));
        Assert.IsTrue(new PortFilter(OrphansOnly: true).Matches(entry));
        Assert.IsFalse(new PortFilter(OrphansOnly: true).Matches(other));
        Assert.IsTrue(entry.Process.IsOrphaned);
    }
}

[TestClass]
[OSCondition(OperatingSystems.Linux)]
public sealed class AgentTrackingTests
{
    [TestMethod]
    public void Process_started_by_an_agent_is_attributed_and_becomes_orphaned_when_the_agent_exits()
    {
        // A stand-in agent: a shell whose command name is "claude". It starts a server, then exits.
        var dir = Directory.CreateTempSubdirectory("pp-agent").FullName;
        var fakeAgent = Path.Combine(dir, "claude");
        File.Copy("/bin/sh", fakeAgent);
        File.SetUnixFileMode(fakeAgent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var port = FreePort();
        var pidFile = Path.Combine(dir, "server.pid");
        using var agent = Process.Start(new ProcessStartInfo(fakeAgent, ["-c", $"python3 -m http.server {port} --bind 127.0.0.1 >/dev/null 2>&1 & echo $! > {pidFile}; sleep 1.5"])
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        })!;

        var reader = new ProcNetPortTableReader();
        var provider = new LinuxProcessInfoProvider();
        var scanner = new PortScanner(reader, provider);
        int serverPid = 0;
        try
        {
            var entry = WaitFor(() => scanner.Scan(false).Entries.FirstOrDefault(e => e.Port == port));
            serverPid = entry.ProcessId;
            Assert.AreEqual(agent.Id, entry.Process.Agent?.ProcessId, "the server should be attributed to the fake agent");
            Assert.IsTrue(entry.Process.Agent!.IsRunning);
            Assert.IsFalse(entry.Process.IsOrphaned);

            agent.WaitForExit();
            var after = WaitFor(() => scanner.Scan(false).Entries.FirstOrDefault(e => e.Port == port && e.Process.IsOrphaned));
            Assert.AreEqual(serverPid, after.ProcessId);
            Assert.AreEqual("Claude Code", after.Process.Agent!.Name);
        }
        finally
        {
            if (serverPid == 0 && File.Exists(pidFile))
            {
                int.TryParse(File.ReadAllText(pidFile).Trim(), out serverPid);
            }

            if (serverPid > 0)
            {
                try { Process.GetProcessById(serverPid).Kill(); } catch (ArgumentException) { }
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Working_directory_and_project_are_read_for_own_processes()
    {
        var details = LinuxProcessInfoProvider.Inspect(Environment.ProcessId);
        Assert.AreEqual(Directory.GetCurrentDirectory(), details.WorkingDirectory);
    }

    private static PortEntry WaitFor(Func<PortEntry?> probe)
    {
        for (var i = 0; i < 100; i++)
        {
            if (probe() is { } entry)
            {
                return entry;
            }

            Thread.Sleep(50);
        }

        Assert.Fail("Timed out waiting for the endpoint");
        return null!;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
