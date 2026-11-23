using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PortPilot.Cli;
using PortPilot.Core;
using PortPilot.Core.Diagnostics;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

if (!PlatformServices.IsSupported)
{
    Console.Error.WriteLine("PortPilot supports Linux and Windows.");
    return 1;
}

var flags = args.Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
var words = args.Where(a => !a.StartsWith('-')).ToList();
var command = words.Count > 0 ? words[0] : null;
var rest = words.Skip(1).ToList();

if (flags.Contains("-h") || flags.Contains("--help") || command == "help")
{
    Console.Write(Help());
    return 0;
}

if (flags.Contains("--version") || command == "version")
{
    Console.WriteLine($"portpilot {typeof(PlatformServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]}");
    return 0;
}

// Recognise any extra agents the user configured in the desktop app's settings.
AgentDetector.Configure(new PortPilot.Core.Settings.JsonSettingsStore(PortPilot.Core.Settings.JsonSettingsStore.DefaultPath).Load().CustomAgentNames);

var all = flags.Contains("-a") || flags.Contains("--all");
var stopFlag = flags.Contains("--stop");
var dev = flags.Contains("-d") || flags.Contains("--dev");

switch (command)
{
    case null when !Console.IsOutputRedirected && !Console.IsInputRedirected:
        return await new Tui().RunAsync(all, dev);
    case null:
    case "list" or "ls":
        return List(string.Join(' ', rest), all, dev, flags.Contains("--json"));
    case "who" or "inspect":
        return Who(rest);
    case "hook" when rest.FirstOrDefault() == "session-end":
        return await Integrations.SessionEndHookAsync(stopFlag);
    case "hook" when rest.FirstOrDefault() == "pre-bash":
        return await Guard.PreBashAsync();
    case "statusline":
        return await Guard.StatusLineAsync();
    case "install-hooks":
        return Integrations.InstallHooks(stopFlag);
    case "uninstall-hooks":
        return Integrations.UninstallHooks();
    case "watch":
        return await Integrations.WatchAsync();
    case "install-watcher":
        return Integrations.InstallWatcher();
    case "uninstall-watcher":
        return Integrations.UninstallWatcher();
    case "mcp":
        return await new McpServer().RunAsync();
    case "install-mcp":
        return Integrations.RegisterMcp();
    case "setup" when rest.FirstOrDefault() is null or "claude":
        return Integrations.SetupClaude(stopFlag);
    case "logs" or "log":
        return Logs(rest);
    case "health":
        return await HealthAsync(rest);
    case "projects" or "p":
        return Projects(all);
    case "cleanup" or "clean":
        return await CleanupAsync(flags.Contains("-y") || flags.Contains("--yes"));
    case "kill" or "stop":
        return await KillAsync(rest, flags.Contains("-y") || flags.Contains("--yes"), flags.Contains("--json"));
    default:
        // `portpilot 3000` is shorthand for `portpilot who 3000`.
        if (int.TryParse(command, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return Who(words);
        }

        Console.Error.WriteLine($"Unknown command '{command}'. Run portpilot --help.");
        return 2;
}

static List<PortEntry> Scan(bool all, PortFilter filter)
{
    var snapshot = PlatformServices.CreateScanner().Scan(all);
    var entries = snapshot.Entries.Where(filter.Matches).ToList();
    entries.Sort(Format.Compare);
    return entries;
}

static int List(string query, bool all, bool dev, bool json)
{
    var entries = Scan(all, new PortFilter(query, ListeningOnly: !all, DevPortsOnly: dev));

    if (json)
    {
        using var stdout = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true });
        w.WriteStartArray();
        foreach (var e in entries)
        {
            WriteJson(w, e);
        }

        w.WriteEndArray();
        w.Flush();
        stdout.WriteByte((byte)'\n');
        return 0;
    }

    var sb = new StringBuilder(entries.Count * 100);
    var width = Console.IsOutputRedirected ? 4096 : Math.Max(60, Console.WindowWidth);
    sb.Append(Header(width)).Append('\n');
    foreach (var e in entries)
    {
        AppendRow(sb, e, width);
        sb.Append('\n');
    }

    if (entries.Count == 0)
    {
        sb.Append("Nothing is listening. Suspicious, but congratulations.\n");
    }

    if (!OperatingSystem.IsWindows() && entries.Any(e => e.ProcessId == 0) && Environment.UserName != "root")
    {
        sb.Append("\nSome sockets belong to other users; run with sudo to see their owners.\n");
    }

    Console.Out.Write(sb);
    return 0;
}

static int Who(List<string> rest)
{
    if (rest.Count == 0 || !int.TryParse(rest[0].TrimStart(':'), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
    {
        Console.Error.WriteLine("Usage: portpilot who <port>");
        return 2;
    }

    var entries = Scan(false, PortFilter.Default).Where(e => e.Port == port).ToList();
    if (entries.Count == 0)
    {
        Console.WriteLine($"Nothing is listening on port {port}.");
        return 1;
    }

    var sb = new StringBuilder();
    foreach (var group in entries.GroupBy(e => e.ProcessId))
    {
        var e = group.First();
        var p = e.Process;
        sb.Append(CultureInfo.InvariantCulture, $"Port {port}{(e.Known is { } k ? $" ({k.Name})" : "")} is held by {Format.Process(p)}, PID {Format.Pid(e)}\n");
        sb.Append("  listening on  ").AppendJoin(", ", group.Select(x => $"{Format.Protocol(x.Binding)} {Format.Address(x.Binding.LocalAddress)}")).Append('\n');
        Line(sb, "executable", p.ExecutablePath);
        Line(sb, "command", p.CommandLine);
        Line(sb, "started", p.StartTime is { } s ? $"{s.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({Format.Age(s)} ago)" : null);
        Line(sb, "parent", p.ParentProcessId?.ToString(CultureInfo.InvariantCulture));
        Line(sb, "project", p.Project is { } project ? $"{project.DisplayText}{(project.IsWorktree ? " (worktree)" : "")}  {project.Root}" : null);
        Line(sb, "folder", p.Project is null ? p.WorkingDirectory : null);
        Line(sb, "agent", p.Agent switch
        {
            null => null,
            { IsRunning: true } a => $"started by {a.Name} (PID {a.ProcessId})",
            { } a => $"left behind by a {a.Name} session that has ended",
        });
        if (p.Access == ProcessAccessLevel.Restricted)
        {
            sb.Append(OperatingSystem.IsWindows() ? "  Details need administrator access.\n" : "  Details need root; try sudo.\n");
        }

        if (e.Binding.Protocol == TransportProtocol.Tcp)
        {
            Line(sb, "health", HealthProbe.CheckAsync(e).GetAwaiter().GetResult().Summary);
        }

        var logs = ProcessLogs.Read(e.ProcessId, 5);
        Line(sb, "output", logs.IsAvailable ? $"{logs.Source}  (portpilot logs {port})" : null);
        sb.Append(CultureInfo.InvariantCulture, $"  stop it with  portpilot kill {port}\n\n");
    }

    Console.Out.Write(sb.ToString().TrimEnd() + "\n");
    return 0;

    static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            sb.Append("  ").Append(label.PadRight(12)).Append(value).Append('\n');
        }
    }
}

static async Task<int> KillAsync(List<string> rest, bool yes, bool json)
{
    if (rest.Count == 0)
    {
        Console.Error.WriteLine("Usage: portpilot kill <port> | pid:<pid>  [-y]");
        return 2;
    }

    var target = rest[0];
    List<ProcessDetails> victims;
    if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) && int.TryParse(target[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
    {
        victims = [PlatformServices.Inspect(pid)];
    }
    else if (int.TryParse(target.TrimStart(':'), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
    {
        victims = Scan(false, PortFilter.Default).Where(e => e.Port == port).Select(e => e.Process).DistinctBy(p => p.ProcessId).ToList();
        if (victims.Count == 0)
        {
            Console.WriteLine($"Nothing is listening on port {port}.");
            return 1;
        }
    }
    else
    {
        Console.Error.WriteLine($"'{target}' is not a port or pid:<pid>.");
        return 2;
    }

    var terminator = PlatformServices.CreateProcessTerminator();
    var failed = 0;
    foreach (var victim in victims)
    {
        var label = $"{Format.Process(victim)} (PID {victim.ProcessId})";
        if (victim.ProcessId == 0 && !OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The owner belongs to another user; run with sudo to see and stop it.");
            failed++;
            continue;
        }

        if (!yes)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine($"Refusing to stop {label} without confirmation; pass -y.");
                return 2;
            }

            Console.Write($"Stop {label}{(victim.CommandLine is { } c ? $"\n  {c}\n" : " ")}? [y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                Console.WriteLine("Left it alone.");
                continue;
            }
        }

        var result = await terminator.TerminateAsync(victim.ProcessId, victim.StartTime);
        Console.WriteLine(result.Outcome switch
        {
            TerminationOutcome.Terminated => $"Stopped {label}.",
            TerminationOutcome.AlreadyExited => $"{label} had already exited.",
            TerminationOutcome.ProcessChanged => $"PID {victim.ProcessId} now belongs to a different process; nothing was stopped.",
            TerminationOutcome.AccessDenied => $"Not allowed to stop {label}.{(OperatingSystem.IsWindows() ? " Try an elevated terminal." : " Try sudo.")}",
            TerminationOutcome.Protected => $"{label} is protected. PortPilot will not stop it.",
            TerminationOutcome.TimedOut => $"{label} is still shutting down.",
            _ => $"Could not stop {label}: {result.Detail}",
        });
        if (!result.Succeeded)
        {
            failed++;
        }
    }

    return failed == 0 ? 0 : 1;
}

static int Logs(List<string> rest)
{
    if (rest.Count == 0)
    {
        Console.Error.WriteLine("Usage: portpilot logs <port> | pid:<pid>  [lines]");
        return 2;
    }

    var lines = rest.Count > 1 && int.TryParse(rest[1], out var n) ? Math.Clamp(n, 1, 2000) : 40;
    var entry = Integrations.Resolve(rest[0], Scan(false, PortFilter.Default)).FirstOrDefault();
    var pid = entry?.ProcessId ?? (rest[0].StartsWith("pid:", StringComparison.Ordinal) && int.TryParse(rest[0][4..], out var p) ? p : 0);
    if (pid == 0)
    {
        Console.WriteLine($"Nothing is listening on {rest[0]}.");
        return 1;
    }

    var tail = ProcessLogs.Read(pid, lines);
    if (!tail.IsAvailable)
    {
        Console.WriteLine(tail.Unavailable);
        return 1;
    }

    Console.Error.WriteLine($"── {tail.Source}");
    Console.Out.Write(string.Join('\n', tail.Lines) + "\n");
    return 0;
}

static async Task<int> HealthAsync(List<string> rest)
{
    var entries = Scan(false, PortFilter.Default).Where(e => e.Binding.Protocol == TransportProtocol.Tcp).ToList();
    var targets = rest.Count == 0
        ? entries.Where(e => e.Known?.Kind == KnownPortKind.DevServer || e.Process.Project is not null).GroupBy(e => e.Port).Select(g => g.First()).ToList()
        : rest.SelectMany(r => Integrations.Resolve(r, entries).Take(1)).ToList();
    if (targets.Count == 0)
    {
        Console.WriteLine(rest.Count == 0 ? "No dev servers to check." : "Nothing is listening there.");
        return 1;
    }

    var results = await Task.WhenAll(targets.Select(e => HealthProbe.CheckAsync(e)));
    var failing = 0;
    for (var i = 0; i < targets.Count; i++)
    {
        var e = targets[i];
        var r = results[i];
        var mark = r.State switch { HealthState.Healthy or HealthState.Accepting => "ok  ", HealthState.Erroring => "err ", _ => "down" };
        failing += r.State is HealthState.Healthy or HealthState.Accepting ? 0 : 1;
        Console.WriteLine($"{mark}  :{e.Port,-6} {Format.Process(e.Process),-16} {r.Summary}{(e.Process.Project is { } proj ? $"   {proj.DisplayText}" : "")}");
    }

    return failing == 0 ? 0 : 1;
}

static string Header(int width)
{
    var sb = new StringBuilder();
    Format.Cell(sb, "PORT", 6);
    Format.Cell(sb, "PROTO", 6);
    Format.Cell(sb, "ADDRESS", 16);
    Format.Cell(sb, "PID", 8);
    Format.Cell(sb, "PROCESS", 18);
    Format.Cell(sb, "SERVICE", 15);
    Format.Cell(sb, "PROJECT", 24);
    Format.Cell(sb, "COMMAND", width - 93, padRight: false);
    return sb.ToString();
}

static void AppendRow(StringBuilder sb, PortEntry e, int width)
{
    Format.Cell(sb, e.Port.ToString(CultureInfo.InvariantCulture), 6);
    Format.Cell(sb, Format.Protocol(e.Binding), 6);
    Format.Cell(sb, Format.Address(e.Binding.LocalAddress), 16);
    Format.Cell(sb, Format.Pid(e), 8);
    Format.Cell(sb, Format.Process(e.Process), 18);
    Format.Cell(sb, e.Known?.Name ?? "", 15);
    Format.Cell(sb, Format.Project(e.Process), 24);
    Format.Cell(sb, e.Process.CommandLine ?? e.Process.ExecutablePath, width - 93, padRight: false);
}

/// <summary>What each git project is running, and what ended agent sessions left behind.</summary>
static int Projects(bool all)
{
    var entries = Scan(all, new PortFilter(ListeningOnly: !all));
    var groups = entries.Where(e => e.Process.Project is not null)
        .GroupBy(e => e.Process.Project!.Name)
        .OrderByDescending(g => g.Any(e => e.Process.IsOrphaned))
        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (groups.Count == 0)
    {
        Console.WriteLine("No listening process is working inside a git project.");
        return 0;
    }

    var sb = new StringBuilder();
    foreach (var group in groups)
    {
        var branches = string.Join(", ", group.Select(e => e.Process.Project!.Branch).Where(b => b is not null).Distinct());
        sb.Append(group.Key);
        if (branches.Length > 0)
        {
            sb.Append("  (").Append(branches).Append(')');
        }

        sb.Append('\n');
        foreach (var process in group.GroupBy(e => e.ProcessId))
        {
            var e = process.First();
            var ports = string.Join(" ", process.Select(x => ":" + x.Port.ToString(CultureInfo.InvariantCulture)).Distinct());
            var agent = e.Process.Agent switch
            {
                null => "",
                { IsRunning: true } a => $"  [{a.Name} {a.ProcessId}]",
                { } a => $"  [left behind by {a.Name}]",
            };
            sb.Append(CultureInfo.InvariantCulture, $"  {ports,-16} {Format.Process(e.Process)} ({Format.Pid(e)}){agent}\n");
        }

        sb.Append('\n');
    }

    var orphans = entries.Where(e => e.Process.IsOrphaned).Select(e => e.ProcessId).Distinct().Count();
    if (orphans > 0)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{orphans} {(orphans == 1 ? "process was" : "processes were")} left behind by ended agent sessions. Stop them with: portpilot cleanup\n");
    }

    Console.Out.Write(sb.ToString());
    return 0;
}

/// <summary>Stops every process an ended agent session left behind.</summary>
static async Task<int> CleanupAsync(bool yes)
{
    // Attribution survives the agent exiting only inside one PortPilot process, so a one-shot CLI run
    // sees orphans whose agent ended while the scan ran, or that inherited a dead agent from a running
    // PortPilot monitor. In practice: use `portpilot` (live view) or the desktop app to catch them.
    var orphans = Scan(false, new PortFilter(OrphansOnly: true)).GroupBy(e => e.ProcessId).Select(g => g.First()).ToList();
    if (orphans.Count == 0)
    {
        Console.WriteLine("Nothing was left behind by an ended agent session.");
        return 0;
    }

    foreach (var e in orphans)
    {
        Console.WriteLine($"  :{e.Port}  {Format.Process(e.Process)} ({e.ProcessId})  {Format.Project(e.Process)}");
    }

    if (!yes)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Refusing to stop processes without confirmation; pass -y.");
            return 2;
        }

        Console.Write($"Stop {(orphans.Count == 1 ? "it" : $"all {orphans.Count}")}? [y/N] ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            Console.WriteLine("Left them alone.");
            return 0;
        }
    }

    var terminator = PlatformServices.CreateProcessTerminator();
    var results = await Task.WhenAll(orphans.Select(e => terminator.TerminateAsync(e.ProcessId, e.Process.StartTime)));
    var stopped = results.Count(r => r.Succeeded);
    Console.WriteLine($"Stopped {stopped} of {orphans.Count}.");
    return stopped == orphans.Count ? 0 : 1;
}

static void WriteJson(Utf8JsonWriter w, PortEntry e)
{
    var p = e.Process;
    w.WriteStartObject();
    w.WriteNumber("port", e.Port);
    w.WriteString("protocol", Format.Protocol(e.Binding));
    w.WriteString("address", e.Binding.LocalAddress.ToString());
    w.WriteString("state", e.Binding.State.ToString());
    if (e.Binding.RemoteAddress is { } remote)
    {
        w.WriteString("remoteAddress", remote.ToString());
        w.WriteNumber("remotePort", e.Binding.RemotePort);
    }

    w.WriteNumber("pid", e.ProcessId);
    w.WriteString("process", p.ImageName);
    w.WriteString("executable", p.ExecutablePath);
    w.WriteString("commandLine", p.CommandLine);
    if (p.StartTime is { } start)
    {
        w.WriteString("started", start);
    }

    w.WriteString("access", p.Access.ToString());
    w.WriteString("workingDirectory", p.WorkingDirectory);
    if (p.Project is { } project)
    {
        w.WriteStartObject("project");
        w.WriteString("name", project.Name);
        w.WriteString("branch", project.Branch);
        w.WriteString("root", project.Root);
        w.WriteBoolean("worktree", project.IsWorktree);
        w.WriteEndObject();
    }

    if (p.Agent is { } agent)
    {
        w.WriteStartObject("agent");
        w.WriteString("name", agent.Name);
        w.WriteNumber("pid", agent.ProcessId);
        w.WriteBoolean("running", agent.IsRunning);
        w.WriteEndObject();
    }

    w.WriteBoolean("orphaned", p.IsOrphaned);
    w.WriteBoolean("tooling", PortPilot.Core.Processes.ToolingDetector.IsTooling(p));
    w.WriteString("service", e.Known?.Name);
    w.WriteEndObject();
}

static string Help() => """
    PortPilot: who is on port 3000, and how to make them leave.

    Usage
      portpilot                      live view (interactive terminal)
      portpilot list [query]         print listening ports   -a all TCP states, -d dev ports, --json
      portpilot who <port>           explain what holds a port (also: portpilot <port>)
      portpilot kill <port>          stop whatever holds a port   -y skip confirmation
      portpilot kill pid:<pid>       stop a process by PID
      portpilot projects             what each git project is running, and which agent session started it
      portpilot cleanup              stop what ended agent sessions left running   -y skip confirmation
      portpilot health [port…]       is it actually answering? (all dev servers when no port is given)
      portpilot logs <port> [n]      the last n lines it wrote (redirected to a file, or the journal)

    Agents and integrations
      portpilot setup claude         everything below in one go   --stop also stops leftovers when a session ends
      portpilot install-hooks        record what each Claude Code session started, and stop it starting a
                                     second copy of a dev server that is already running
      portpilot statusline           this project's servers, for Claude Code's status line
      portpilot install-watcher      a background watcher that starts at login
      portpilot install-mcp          let Claude ask PortPilot directly (MCP server: portpilot mcp)
      portpilot uninstall-hooks | uninstall-watcher

    Live view keys
      ↑↓ / j k   move           /     search          x / Del   stop process
      PgUp PgDn  page           Esc   clear search    o         open in browser
      a          all TCP states d     dev ports only  t         tcp / udp / both
      r          refresh now    c     clean up leftovers  q   quit

    """;
