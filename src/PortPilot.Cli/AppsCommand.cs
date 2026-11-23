using System.Globalization;
using System.Text;
using System.Text.Json;
using PortPilot.Core;
using PortPilot.Core.Apps;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Cli;

/// <summary>
/// The human view of the machine: apps (not ports) grouped by project, with CPU and memory, and a
/// streaming JSON mode so editors can keep one quiet process instead of spawning one per refresh.
/// </summary>
internal static class AppsCommand
{
    /// <summary>`portpilot apps`: one look, two samples 300 ms apart so CPU % means something.</summary>
    public static async Task<int> RunAsync(bool json, bool all, bool tooling)
    {
        var scanner = PlatformServices.CreateScanner();
        scanner.IncludeApps = true;
        scanner.Scan(all);
        await Task.Delay(300);
        var snapshot = scanner.Scan(all);
        var apps = Visible(snapshot.Apps ?? [], tooling);

        if (json)
        {
            using var stdout = Console.OpenStandardOutput();
            using var w = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true });
            WriteApps(w, apps);
            w.Flush();
            stdout.WriteByte((byte)'\n');
            return 0;
        }

        Console.Out.Write(Render(apps, Console.IsOutputRedirected ? 4096 : Math.Max(70, Console.WindowWidth)));
        return 0;
    }

    /// <summary>
    /// `portpilot stream`: one JSON line per scan ({"entries": […], "apps": […]}) until stdin closes or the
    /// process is stopped. Apps (and their CPU and memory) only when --apps is given.
    /// </summary>
    public static async Task<int> StreamAsync(double seconds, bool apps, bool all)
    {
        var scanner = PlatformServices.CreateScanner();
        scanner.IncludeApps = apps;
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        // The parent closing our stdin is the signal to exit, so an editor that dies never leaves us behind.
        _ = Task.Run(async () =>
        {
            var buffer = new char[256];
            while (await Console.In.ReadAsync(buffer, stop.Token).ConfigureAwait(false) > 0)
            {
            }

            await stop.CancelAsync().ConfigureAwait(false);
        });

        using var stdout = Console.OpenStandardOutput();
        var interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 0.5, 60));
        while (!stop.IsCancellationRequested)
        {
            var snapshot = scanner.Scan(all);
            using (var w = new Utf8JsonWriter(stdout))
            {
                w.WriteStartObject();
                w.WriteNumber("scanMs", Math.Round(snapshot.Duration.TotalMilliseconds, 2));
                w.WriteStartArray("entries");
                foreach (var e in snapshot.Entries)
                {
                    JsonOut.Entry(w, e);
                }

                w.WriteEndArray();
                if (snapshot.Apps is { } list)
                {
                    w.WritePropertyName("apps");
                    WriteApps(w, list);
                }

                w.WriteEndObject();
            }

            stdout.WriteByte((byte)'\n');
            stdout.Flush();
            try
            {
                await Task.Delay(interval, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return 0;
    }

    public static IReadOnlyList<AppInfo> Visible(IReadOnlyList<AppInfo> apps, bool tooling) =>
        apps.Where(a => tooling || !a.IsTooling).ToList();

    public static void WriteApps(Utf8JsonWriter w, IReadOnlyList<AppInfo> apps)
    {
        w.WriteStartArray();
        foreach (var a in apps)
        {
            w.WriteStartObject();
            w.WriteString("key", a.Key);
            w.WriteString("name", a.Name);
            w.WriteString("kind", a.Kind);
            w.WriteNumber("pid", a.RootProcessId);
            w.WriteNumber("processes", a.ProcessIds.Count);
            w.WriteStartArray("pids");
            foreach (var pid in a.ProcessIds)
            {
                w.WriteNumberValue(pid);
            }

            w.WriteEndArray();
            w.WriteBoolean("system", a.IsSystem);
            w.WriteNumber("cpu", a.Usage.CpuPercent);
            w.WriteNumber("memory", a.Usage.MemoryBytes);
            w.WriteStartArray("ports");
            foreach (var port in a.Endpoints.Select(e => e.Port).Distinct())
            {
                w.WriteNumberValue(port);
            }

            w.WriteEndArray();
            if (a.Started is { } started)
            {
                w.WriteString("started", started);
            }

            w.WriteString("commandLine", a.CommandLine);
            w.WriteString("workingDirectory", a.WorkingDirectory);
            if (a.Project is { } project)
            {
                w.WriteString("project", project.Name);
                w.WriteString("branch", project.Branch);
            }

            if (a.Agent is { } agent)
            {
                w.WriteString("agent", agent.Name);
                w.WriteBoolean("agentRunning", agent.IsRunning);
            }

            w.WriteBoolean("leftBehind", a.IsOrphaned);
            w.WriteBoolean("tooling", a.IsTooling);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static string Render(IReadOnlyList<AppInfo> apps, int width)
    {
        if (apps.Count == 0)
        {
            return "Nothing is listening.\n";
        }

        var sb = new StringBuilder();
        foreach (var group in apps.GroupBy(a => a.Project?.Name).OrderBy(g => g.Key is null).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var total = group.Aggregate(default(ResourceUsage), (sum, a) => sum + a.Usage);
            var branches = string.Join(", ", group.Select(a => a.Project?.Branch).Where(b => b is not null).Distinct());
            sb.Append(group.Key ?? "Other").Append(branches.Length > 0 ? $"  ({branches})" : "")
              .Append(CultureInfo.InvariantCulture, $"  · {group.Count()} {(group.Count() == 1 ? "app" : "apps")} · {Memory(total.MemoryBytes)} · {total.CpuPercent:0.#}% CPU\n");
            foreach (var a in group.OrderByDescending(a => a.IsOrphaned).ThenByDescending(a => a.Usage.MemoryBytes))
            {
                var ports = a.Endpoints.Select(e => e.Port).Distinct().ToList();
                var portText = $":{ports[0]}" + (ports.Count > 1 ? $" +{ports.Count - 1}" : "");
                var tags = new List<string>();
                if (a.IsOrphaned)
                {
                    tags.Add("LEFT BEHIND");
                }
                else if (a.Agent is { } agent)
                {
                    tags.Add(AgentDetector.ShortNameFor(agent.Name));
                }

                var line = new StringBuilder("  ");
                Format.Cell(line, a.Name, 34);
                Format.Cell(line, portText, 12);
                Format.Cell(line, Memory(a.Usage.MemoryBytes), 10);
                Format.Cell(line, $"{a.Usage.CpuPercent:0.#}%", 8);
                Format.Cell(line, Format.Age(a.Started), 6);
                Format.Cell(line, string.Join(" ", tags), Math.Max(0, width - 72), padRight: false);
                sb.Append(line.ToString().TrimEnd()).Append('\n');
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    public static string Memory(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024L * 1024 => $"{bytes / 1024} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };
}
