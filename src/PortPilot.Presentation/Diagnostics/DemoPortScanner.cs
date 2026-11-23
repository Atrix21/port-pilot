using System.Net;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.App.Diagnostics;

/// <summary>
/// A believable developer machine, used by <c>--demo</c> and screenshot mode. Endpoints come and go
/// over time so the new / closed states can be seen without running real servers.
/// </summary>
public sealed class DemoPortScanner : IPortScanner
{
    // A neutral profile path so screenshots never reveal the real account name.
    private const string Home = @"C:\Users\dev";
    private const string LinuxHome = "/home/dev";
    private static readonly bool Win = OperatingSystem.IsWindows();
    private readonly DateTimeOffset _boot = DateTimeOffset.Now;
    private readonly HashSet<int> _terminated = [];
    private int _scan;

    private sealed record DemoProcess(int Pid, string Image, string? Path, string? CommandLine, TimeSpan Age, ProcessAccessLevel Access = ProcessAccessLevel.Full);

    // Projects and agent sessions: two Claude Code sessions are working, and one has ended but left
    // its servers running.
    private static readonly AgentSession LiveAgent = new(30120, DateTimeOffset.Now.AddMinutes(-52), "Claude Code", Win ? $@"{Home}\code\atlas-web" : $"{LinuxHome}/code/atlas-web", IsRunning: true);
    private static readonly AgentSession EndedAgent = new(28777, DateTimeOffset.Now.AddHours(-3), "Claude Code", Win ? $@"{Home}\code\ledger" : $"{LinuxHome}/code/ledger", IsRunning: false);

    private static readonly Dictionary<int, (ProjectInfo Project, AgentSession? Agent)> Projects = new()
    {
        [18244] = (Project("atlas-web", "feature/auth"), LiveAgent),
        [22408] = (Project("atlas-web", "blog-redesign", worktree: "atlas-web-blog"), LiveAgent),
        [20112] = (Project("storefront", "main"), EndedAgent),
        [9032] = (Project("ledger", "fix/rounding"), EndedAgent),
        [15520] = (Project("atlas", "main"), null),
    };

    private static ProjectInfo Project(string name, string branch, string? worktree = null)
    {
        var folder = worktree ?? name;
        var root = Win ? $@"{Home}\code\{folder}" : $"{LinuxHome}/code/{folder}";
        return new ProjectInfo(root, name, branch, worktree is not null);
    }

    // Each process has a Windows and a Linux flavour, so demo screenshots look native on both.
    private static readonly DemoProcess Vite = Win
        ? new(18244, "node.exe", @"C:\Program Files\nodejs\node.exe", $@"""C:\Program Files\nodejs\node.exe"" {Home}\code\atlas-web\node_modules\vite\bin\vite.js --port 5173", TimeSpan.FromMinutes(47))
        : new(18244, "node", "/usr/bin/node", $"node {LinuxHome}/code/atlas-web/node_modules/vite/bin/vite.js --port 5173", TimeSpan.FromMinutes(47));
    private static readonly DemoProcess Next = Win
        ? new(20112, "node.exe", @"C:\Program Files\nodejs\node.exe", @"""C:\Program Files\nodejs\node.exe"" node_modules\next\dist\bin\next dev --turbo", TimeSpan.FromHours(2.3))
        : new(20112, "node", "/usr/bin/node", "node node_modules/next/dist/bin/next dev --turbo", TimeSpan.FromHours(2.3));
    private static readonly DemoProcess Django = Win
        ? new(9032, "python.exe", $@"{Home}\code\ledger\.venv\Scripts\python.exe", "python manage.py runserver 0.0.0.0:8000", TimeSpan.FromMinutes(12))
        : new(9032, "python3", $"{LinuxHome}/code/ledger/.venv/bin/python3", "python3 manage.py runserver 0.0.0.0:8000", TimeSpan.FromMinutes(12));
    private static readonly DemoProcess Postgres = Win
        ? new(4120, "postgres.exe", @"C:\Program Files\PostgreSQL\17\bin\postgres.exe", @"""C:/Program Files/PostgreSQL/17/bin/postgres.exe"" -D ""C:/Program Files/PostgreSQL/17/data""", TimeSpan.FromDays(3.2))
        : new(4120, "postgres", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted);
    private static readonly DemoProcess Redis = Win
        ? new(6688, "redis-server.exe", @"C:\Program Files\Redis\redis-server.exe", @"""C:\Program Files\Redis\redis-server.exe"" --service-run redis.windows-service.conf", TimeSpan.FromDays(3.2))
        : new(6688, "redis-server", "/usr/bin/redis-server", "redis-server 127.0.0.1:6379", TimeSpan.FromDays(3.2));
    private static readonly DemoProcess Api = Win
        ? new(15520, "Atlas.Api.exe", $@"{Home}\code\atlas\src\Atlas.Api\bin\Debug\net10.0\Atlas.Api.exe", "Atlas.Api.exe --urls https://localhost:5001", TimeSpan.FromMinutes(5))
        : new(15520, "Atlas.Api", $"{LinuxHome}/code/atlas/src/Atlas.Api/bin/Debug/net10.0/Atlas.Api", "Atlas.Api --urls https://localhost:5001", TimeSpan.FromMinutes(5));
    private static readonly DemoProcess Mongo = Win
        ? new(7744, "mongod.exe", @"C:\Program Files\MongoDB\Server\8.0\bin\mongod.exe", @"""C:\Program Files\MongoDB\Server\8.0\bin\mongod.exe"" --config ""C:\Program Files\MongoDB\Server\8.0\bin\mongod.cfg"" --service", TimeSpan.FromDays(3.2))
        : new(7744, "mongod", "/usr/bin/mongod", "mongod --config /etc/mongod.conf", TimeSpan.FromDays(3.2));
    private static readonly DemoProcess Code = Win
        ? new(11876, "Code.exe", $@"{Home}\AppData\Local\Programs\Microsoft VS Code\Code.exe", null, TimeSpan.FromHours(5.1))
        : new(11876, "code", "/usr/share/code/code", "/usr/share/code/code --type=utility --inspect-port=9229", TimeSpan.FromHours(5.1));
    private static readonly DemoProcess Docker = Win
        ? new(5236, "com.docker.backend.exe", @"C:\Program Files\Docker\Docker\resources\com.docker.backend.exe", null, TimeSpan.FromDays(1.4))
        : new(5236, "dockerd", null, null, TimeSpan.FromDays(1.4), ProcessAccessLevel.Restricted);
    private static readonly DemoProcess Astro = Win
        ? new(22408, "node.exe", @"C:\Program Files\nodejs\node.exe", @"""C:\Program Files\nodejs\node.exe"" node_modules\astro\astro.js dev", TimeSpan.FromSeconds(4))
        : new(22408, "node", "/usr/bin/node", "node node_modules/astro/astro.js dev", TimeSpan.FromSeconds(4));
    private static readonly DemoProcess Spotify = Win
        ? new(13340, "Spotify.exe", $@"{Home}\AppData\Roaming\Spotify\Spotify.exe", null, TimeSpan.FromHours(6))
        : new(13340, "spotify", "/usr/share/spotify/spotify", "/usr/share/spotify/spotify", TimeSpan.FromHours(6));
    private static readonly DemoProcess Rpc = Win
        ? new(1180, "svchost.exe", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted)
        : new(1180, "sshd", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted);
    private static readonly DemoProcess Mdns = Win
        ? new(2604, "svchost.exe", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted)
        : new(2604, "avahi-daemon", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted);
    private static readonly DemoProcess Lsass = Win
        ? new(1036, "lsass.exe", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted)
        : new(1036, "cupsd", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted);
    private static readonly DemoProcess System = Win
        ? new(4, "System", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.System)
        : new(812, "systemd-resolved", null, null, TimeSpan.FromDays(3.2), ProcessAccessLevel.Restricted);

    public bool IncludeApps { get; set; }

    // Believable footprints for the demo processes: (MB, CPU %).
    private static readonly Dictionary<int, (int Mb, double Cpu)> Footprint = new()
    {
        [18244] = (412, 3.1), [20112] = (686, 1.4), [9032] = (96, 0.2), [4120] = (148, 0.4), [6688] = (12, 0.1), [15520] = (231, 0.8),
        [7744] = (305, 0.6), [11876] = (520, 2.2), [5236] = (88, 0.3), [22408] = (174, 4.6), [13340] = (380, 1.1),
    };

    /// <summary>When true the dataset is frozen with one "new" and one "closed" endpoint – used for screenshots.</summary>
    public bool Staged { get; init; }

    public PortSnapshot Scan(bool includeAllTcpStates)
    {
        _scan++;
        var entries = new List<PortEntry>
        {
            Tcp(Vite, 5173, IPAddress.Loopback),
            Tcp(Vite, 5173, IPAddress.IPv6Loopback),
            Tcp(Vite, 24678, IPAddress.Loopback),
            Tcp(Next, 3000, IPAddress.IPv6Any),
            Tcp(Django, 8000, IPAddress.Any),
            Tcp(Postgres, 5432, IPAddress.Any),
            Tcp(Postgres, 5432, IPAddress.IPv6Any),
            Tcp(Redis, 6379, IPAddress.Loopback),
            Tcp(Api, 5001, IPAddress.Loopback),
            Tcp(Mongo, 27017, IPAddress.Loopback),
            Tcp(Code, 9229, IPAddress.Loopback),
            Tcp(Docker, 2375, IPAddress.Loopback),
            Tcp(Rpc, Win ? 135 : 22, IPAddress.Any),
            Tcp(System, Win ? 445 : 53, Win ? IPAddress.Any : IPAddress.Parse("127.0.0.53")),
            Tcp(Lsass, Win ? 49664 : 631, Win ? IPAddress.Any : IPAddress.Loopback),
            Udp(Mdns, 5353, IPAddress.Any),
            Udp(Spotify, 57621, IPAddress.Any),
            Udp(Code, 54112, IPAddress.Loopback),
        };

        // Astro dev server appears on the second scan; the Docker API goes away on the third.
        var astroOpen = Staged ? _scan >= 2 : _scan % 12 is >= 2 and < 9;
        if (astroOpen)
        {
            entries.Add(Tcp(Astro, 4321, IPAddress.Loopback));
        }

        var dockerClosed = Staged ? _scan >= 3 : _scan % 15 is >= 6 and < 12;
        if (dockerClosed)
        {
            entries.RemoveAll(e => e.ProcessId == Docker.Pid);
        }

        lock (_terminated)
        {
            entries.RemoveAll(e => _terminated.Contains(e.ProcessId));
        }

        if (includeAllTcpStates)
        {
            entries.Add(Connection(Code, 51544, IPAddress.Parse("140.82.112.22"), 443));
            entries.Add(Connection(Next, 51602, IPAddress.Loopback, 5432));
            entries.Add(Connection(Spotify, 51877, IPAddress.Parse("35.186.224.24"), 443));
        }

        return new PortSnapshot(entries, DateTimeOffset.Now, TimeSpan.FromMilliseconds(3.4)) { Apps = IncludeApps ? Apps(entries) : null };
    }

    private static List<Core.Apps.AppInfo> Apps(List<PortEntry> entries) =>
        entries.Where(e => e.Binding.IsListening).GroupBy(e => e.ProcessId).Select(g =>
        {
            var primary = g.OrderBy(e => e.Port).First();
            var (name, kind) = Core.Apps.AppNamer.Name(primary.Process, null, primary.Known);
            var (mb, cpu) = Footprint.GetValueOrDefault(g.Key, (0, 0));
            var restricted = primary.Process.Access != ProcessAccessLevel.Full;
            return new Core.Apps.AppInfo
            {
                Key = $"demo:{g.Key}",
                Name = name,
                Kind = kind,
                RootProcessId = g.Key,
                ProcessIds = [g.Key],
                Endpoints = g.OrderBy(e => e.Port).ToList(),
                IsTooling = primary.Process.ImageName.StartsWith("code", StringComparison.OrdinalIgnoreCase) || primary.Process.ImageName.StartsWith("Code", StringComparison.Ordinal),
                Started = primary.Process.StartTime,
                Usage = new Core.Processes.ResourceUsage(restricted ? 0 : cpu, restricted ? 0 : mb * 1024L * 1024),
                CommandLine = primary.Process.CommandLine,
                WorkingDirectory = primary.Process.WorkingDirectory,
            };
        }).ToList();

    internal void MarkTerminated(int pid)
    {
        lock (_terminated)
        {
            _terminated.Add(pid);
        }
    }

    private PortEntry Tcp(DemoProcess process, int port, IPAddress address) =>
        Create(process, new PortBinding(TransportProtocol.Tcp, address, port, null, 0, ConnectionState.Listening, process.Pid));

    private PortEntry Udp(DemoProcess process, int port, IPAddress address) =>
        Create(process, new PortBinding(TransportProtocol.Udp, address, port, null, 0, ConnectionState.Bound, process.Pid));

    private PortEntry Connection(DemoProcess process, int localPort, IPAddress remote, int remotePort) =>
        Create(process, new PortBinding(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.24"), localPort, remote, remotePort, ConnectionState.Established, process.Pid));

    private PortEntry Create(DemoProcess process, PortBinding binding)
    {
        var project = Projects.TryGetValue(process.Pid, out var p) ? p : default;
        return new(
            binding,
            new ProcessDetails
            {
                ProcessId = process.Pid,
                ImageName = process.Image,
                ExecutablePath = process.Path,
                CommandLine = process.CommandLine,
                StartTime = process.Access == ProcessAccessLevel.Full ? _boot - process.Age : null,
                Access = process.Access,
                Project = project.Project,
                Agent = project.Agent,
                WorkingDirectory = project.Project?.Root,
            },
            WellKnownPorts.Lookup(binding.LocalPort));
    }
}


/// <summary>Pretends to terminate demo processes so the full flow can be exercised safely.</summary>
public sealed class DemoProcessTerminator(DemoPortScanner scanner) : IProcessTerminator
{
    public TerminationAccess ProbeAccess(int processId) => processId switch
    {
        4 => TerminationAccess.Protected,
        1180 or 2604 or 1036 => TerminationAccess.RequiresElevation,
        812 or 4120 or 5236 when !OperatingSystem.IsWindows() => TerminationAccess.RequiresElevation,
        _ => TerminationAccess.Allowed,
    };

    public async Task<TerminationResult> TerminateAsync(int processId, DateTimeOffset? expectedStartTime, CancellationToken cancellationToken = default)
    {
        await Task.Delay(180, cancellationToken);
        switch (ProbeAccess(processId))
        {
            case TerminationAccess.Protected:
                return new TerminationResult(TerminationOutcome.Protected, processId, OperatingSystem.IsWindows() ? "Windows marks this process as critical." : "Kernel threads cannot be terminated.");
            case TerminationAccess.RequiresElevation:
                return new TerminationResult(TerminationOutcome.AccessDenied, processId, "Access is denied.");
        }

        scanner.MarkTerminated(processId);
        return new TerminationResult(TerminationOutcome.Terminated, processId);
    }
}