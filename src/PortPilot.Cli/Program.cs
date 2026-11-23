using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PortPilot.Cli;
using PortPilot.Core;
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

var all = flags.Contains("-a") || flags.Contains("--all");
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
        if (p.Access == ProcessAccessLevel.Restricted)
        {
            sb.Append(OperatingSystem.IsWindows() ? "  Details need administrator access.\n" : "  Details need root; try sudo.\n");
        }

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

static string Header(int width)
{
    var sb = new StringBuilder();
    Format.Cell(sb, "PORT", 6);
    Format.Cell(sb, "PROTO", 6);
    Format.Cell(sb, "ADDRESS", 16);
    Format.Cell(sb, "PID", 8);
    Format.Cell(sb, "PROCESS", 18);
    Format.Cell(sb, "SERVICE", 15);
    Format.Cell(sb, "COMMAND", width - 69, padRight: false);
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
    Format.Cell(sb, e.Process.CommandLine ?? e.Process.ExecutablePath, width - 69, padRight: false);
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

    Live view keys
      ↑↓ / j k   move           /     search          x / Del   stop process
      PgUp PgDn  page           Esc   clear search    o         open in browser
      a          all TCP states d     dev ports only  t         tcp / udp / both
      r          refresh now    q     quit

    """;
