using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PortPilot.Core;
using PortPilot.Core.Diagnostics;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Cli;

/// <summary>
/// A Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0), so coding agents can ask
/// PortPilot what is running instead of guessing: which ports a project already uses, whether its dev
/// server is up, what it logged, which port is free, and what an earlier session left behind.
/// </summary>
internal sealed class McpServer
{
    private const string FallbackProtocol = "2025-06-18";

    private readonly IPortScanner _scanner = PlatformServices.CreateScanner();
    private readonly IProcessTerminator _terminator = PlatformServices.CreateProcessTerminator();
    private readonly Stream _out = Console.OpenStandardOutput();

    private sealed record Tool(string Name, string Description, string Schema, Func<JsonElement, Task<string>> Run, bool Destructive = false);

    private Tool[] Tools => [
        new("list_ports",
            "List listening ports on this machine with the owning process, its git project and branch, the coding agent session that started it (if any), and whether it was left behind by an ended session.",
            """{"type":"object","properties":{"project":{"type":"string","description":"Only ports of this git project (name or branch)."},"dev_only":{"type":"boolean","description":"Only well-known development ports."},"include_connections":{"type":"boolean","description":"Also include established TCP connections."}}}""",
            ListPortsAsync),
        new("project_status",
            "What a project is running right now: its ports, processes, health of each HTTP server, and anything an ended agent session left behind. Call this before starting a dev server, to reuse one that is already running.",
            """{"type":"object","properties":{"path":{"type":"string","description":"A directory inside the project. Defaults to the server's working directory."}}}""",
            ProjectStatusAsync),
        new("port_owner",
            "Explain what is using a port: process, PID, command line, project, agent, and a health check.",
            """{"type":"object","properties":{"port":{"type":"integer"}},"required":["port"]}""",
            PortOwnerAsync),
        new("check_health",
            "Check whether a listening port answers: TCP connect, then an HTTP GET. Returns status code and latency.",
            """{"type":"object","properties":{"port":{"type":"integer"}},"required":["port"]}""",
            CheckHealthAsync),
        new("read_logs",
            "Read the recent output of the process on a port (or a PID), when it is written to a file or the systemd journal.",
            """{"type":"object","properties":{"port":{"type":"integer"},"pid":{"type":"integer"},"lines":{"type":"integer","description":"Default 40, at most 400."}}}""",
            ReadLogsAsync),
        new("find_free_port",
            "Find free TCP ports, starting at a preferred port, that nothing is listening on and that can be bound right now.",
            """{"type":"object","properties":{"preferred":{"type":"integer","description":"Start here, e.g. 3000. Default 3000."},"count":{"type":"integer","description":"How many to return. Default 1."}}}""",
            FindFreePortAsync),
        new("stop_process",
            "Stop the process on a port (or a PID): SIGTERM then SIGKILL on Linux, TerminateProcess on Windows. Refuses system processes and PIDs that were reused.",
            """{"type":"object","properties":{"port":{"type":"integer"},"pid":{"type":"integer"}}}""",
            StopProcessAsync,
            Destructive: true),
        new("cleanup_leftovers",
            "Stop every process an ended coding-agent session left running, optionally only in one project.",
            """{"type":"object","properties":{"project":{"type":"string"}}}""",
            CleanupAsync,
            Destructive: true),
    ];

    public async Task<int> RunAsync()
    {
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                Write(w => Error(w, null, -32700, "Parse error"));
                continue;
            }

            using (doc)
            {
                await HandleAsync(doc.RootElement).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private async Task HandleAsync(JsonElement message)
    {
        var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
        var hasId = message.TryGetProperty("id", out var id);
        var parameters = message.TryGetProperty("params", out var p) ? p : default;

        switch (method)
        {
            case "initialize":
                var version = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
                Write(w => Result(w, id, r =>
                {
                    r.WriteString("protocolVersion", version ?? FallbackProtocol);
                    r.WriteStartObject("capabilities");
                    r.WriteStartObject("tools");
                    r.WriteEndObject();
                    r.WriteEndObject();
                    r.WriteStartObject("serverInfo");
                    r.WriteString("name", "portpilot");
                    r.WriteString("version", typeof(PlatformServices).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
                    r.WriteEndObject();
                    r.WriteString("instructions", "PortPilot knows every listening port on this machine, which git project and agent session each belongs to, and whether it is healthy. Use project_status before starting a dev server, find_free_port instead of guessing a port, and read_logs or check_health when a server misbehaves.");
                }));
                break;
            case "ping":
                Write(w => Result(w, id, _ => { }));
                break;
            case "tools/list":
                Write(w => Result(w, id, r =>
                {
                    r.WriteStartArray("tools");
                    foreach (var tool in Tools)
                    {
                        r.WriteStartObject();
                        r.WriteString("name", tool.Name);
                        r.WriteString("description", tool.Description);
                        r.WritePropertyName("inputSchema");
                        using (var schema = JsonDocument.Parse(tool.Schema))
                        {
                            schema.RootElement.WriteTo(r);
                        }

                        r.WriteStartObject("annotations");
                        r.WriteBoolean("readOnlyHint", !tool.Destructive);
                        r.WriteBoolean("destructiveHint", tool.Destructive);
                        r.WriteEndObject();
                        r.WriteEndObject();
                    }

                    r.WriteEndArray();
                }));
                break;
            case "tools/call":
                var name = parameters.TryGetProperty("name", out var n) ? n.GetString() : null;
                var args = parameters.TryGetProperty("arguments", out var a) ? a : default;
                var tool = Tools.FirstOrDefault(t => t.Name == name);
                if (tool is null)
                {
                    Write(w => Error(w, id, -32602, $"Unknown tool: {name}"));
                    break;
                }

                string text;
                bool isError;
                try
                {
                    text = await tool.Run(args).ConfigureAwait(false);
                    isError = false;
                }
                catch (Exception ex)
                {
                    text = ex.Message;
                    isError = true;
                }

                Write(w => Result(w, id, r =>
                {
                    r.WriteStartArray("content");
                    r.WriteStartObject();
                    r.WriteString("type", "text");
                    r.WriteString("text", text);
                    r.WriteEndObject();
                    r.WriteEndArray();
                    r.WriteBoolean("isError", isError);
                }));
                break;
            default:
                // Notifications (no id) such as notifications/initialized need no answer.
                if (hasId)
                {
                    Write(w => Error(w, id, -32601, $"Method not found: {method}"));
                }

                break;
        }
    }

    // ═════════════ Tools ═════════════

    private Task<string> ListPortsAsync(JsonElement args)
    {
        var project = Str(args, "project");
        var filter = new PortFilter(project is null ? "" : $"project:{project}", ListeningOnly: !Bool(args, "include_connections"), DevPortsOnly: Bool(args, "dev_only"));
        var entries = _scanner.Scan(!filter.ListeningOnly).Entries.Where(filter.Matches).OrderBy(e => e.Port).ToList();
        return Task.FromResult(Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("count", entries.Count);
            w.WriteStartArray("ports");
            foreach (var e in entries)
            {
                WriteEntry(w, e, null);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private async Task<string> ProjectStatusAsync(JsonElement args)
    {
        var path = Str(args, "path") ?? Directory.GetCurrentDirectory();
        var project = ProjectResolver.Resolve(Path.GetFullPath(path));
        var entries = _scanner.Scan(false).Entries
            .Where(e => project is null
                ? e.Process.WorkingDirectory?.StartsWith(path, StringComparison.Ordinal) == true
                : e.Process.Project?.Name == project.Name)
            .OrderBy(e => e.Port)
            .ToList();
        var health = await HealthForAsync(entries).ConfigureAwait(false);

        return Json(w =>
        {
            w.WriteStartObject();
            if (project is not null)
            {
                w.WriteString("project", project.Name);
                w.WriteString("branch", project.Branch);
                w.WriteString("root", project.Root);
            }
            else
            {
                w.WriteString("directory", path);
                w.WriteString("note", "Not inside a git repository; matched processes by working directory.");
            }

            w.WriteNumber("ports", entries.Count);
            w.WriteNumber("leftovers", entries.Where(e => e.Process.IsOrphaned).Select(e => e.ProcessId).Distinct().Count());
            w.WriteStartArray("running");
            foreach (var e in entries)
            {
                WriteEntry(w, e, health.GetValueOrDefault(e.Key));
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private async Task<string> PortOwnerAsync(JsonElement args)
    {
        var port = Int(args, "port") ?? throw new ArgumentException("port is required");
        var entries = _scanner.Scan(false).Entries.Where(e => e.Port == port).ToList();
        if (entries.Count == 0)
        {
            return $"Nothing is listening on port {port}.";
        }

        var health = await HealthForAsync(entries).ConfigureAwait(false);
        return Json(w =>
        {
            w.WriteStartArray();
            foreach (var e in entries)
            {
                WriteEntry(w, e, health.GetValueOrDefault(e.Key));
            }

            w.WriteEndArray();
        });
    }

    private async Task<string> CheckHealthAsync(JsonElement args)
    {
        var port = Int(args, "port") ?? throw new ArgumentException("port is required");
        var entry = _scanner.Scan(false).Entries.FirstOrDefault(e => e.Port == port && e.Binding.Protocol == TransportProtocol.Tcp);
        if (entry is null)
        {
            return $"Nothing is listening on TCP port {port}.";
        }

        var result = await HealthProbe.CheckAsync(entry).ConfigureAwait(false);
        return Json(w => WriteHealth(w, result));
    }

    private Task<string> ReadLogsAsync(JsonElement args)
    {
        var pid = Int(args, "pid") ?? (Int(args, "port") is { } port
            ? _scanner.Scan(false).Entries.FirstOrDefault(e => e.Port == port)?.ProcessId ?? throw new ArgumentException($"Nothing is listening on port {port}.")
            : throw new ArgumentException("port or pid is required"));
        var tail = ProcessLogs.Read(pid, Math.Clamp(Int(args, "lines") ?? 40, 1, 400));
        return Task.FromResult(tail.IsAvailable ? $"From {tail.Source}:\n{string.Join('\n', tail.Lines)}" : tail.Unavailable!);
    }

    private Task<string> FindFreePortAsync(JsonElement args)
    {
        var start = Math.Clamp(Int(args, "preferred") ?? 3000, 1024, 65535);
        var count = Math.Clamp(Int(args, "count") ?? 1, 1, 20);
        var used = _scanner.Scan(false).Entries.Where(e => e.Binding.Protocol == TransportProtocol.Tcp).Select(e => e.Port).ToHashSet();
        var free = new List<int>();
        for (var port = start; port <= 65535 && free.Count < count; port++)
        {
            if (!used.Contains(port) && CanBind(port))
            {
                free.Add(port);
            }
        }

        return Task.FromResult(Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("free");
            foreach (var port in free)
            {
                w.WriteNumberValue(port);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private async Task<string> StopProcessAsync(JsonElement args)
    {
        var entries = _scanner.Scan(false).Entries;
        var target = Int(args, "pid") is { } pid
            ? entries.FirstOrDefault(e => e.ProcessId == pid)?.Process ?? PlatformServices.Inspect(pid)
            : Int(args, "port") is { } port
                ? entries.FirstOrDefault(e => e.Port == port)?.Process ?? throw new ArgumentException($"Nothing is listening on port {port}.")
                : throw new ArgumentException("port or pid is required");

        var result = await _terminator.TerminateAsync(target.ProcessId, target.StartTime).ConfigureAwait(false);
        return $"{target.DisplayName} (PID {target.ProcessId}): {result.Outcome}{(result.Detail is null ? "" : $" – {result.Detail}")}";
    }

    private async Task<string> CleanupAsync(JsonElement args)
    {
        var project = Str(args, "project");
        var orphans = _scanner.Scan(false).Entries
            .Where(e => e.Process.IsOrphaned && (project is null || e.Process.Project?.Name == project))
            .GroupBy(e => e.ProcessId)
            .Select(g => g.First())
            .ToList();
        if (orphans.Count == 0)
        {
            return "Nothing was left behind by an ended agent session.";
        }

        var results = await Task.WhenAll(orphans.Select(e => _terminator.TerminateAsync(e.ProcessId, e.Process.StartTime))).ConfigureAwait(false);
        var sb = new StringBuilder();
        for (var i = 0; i < orphans.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $":{orphans[i].Port} {orphans[i].Process.DisplayName} (PID {orphans[i].ProcessId}): {results[i].Outcome}\n");
        }

        return sb.ToString();
    }

    // ═════════════ Helpers ═════════════

    private static async Task<Dictionary<string, HealthResult>> HealthForAsync(List<PortEntry> entries)
    {
        var tcp = entries.Where(e => e.Binding.Protocol == TransportProtocol.Tcp && e.Binding.State == ConnectionState.Listening).ToList();
        var results = await Task.WhenAll(tcp.Select(e => HealthProbe.CheckAsync(e))).ConfigureAwait(false);
        return tcp.Zip(results).ToDictionary(x => x.First.Key, x => x.Second);
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void WriteEntry(Utf8JsonWriter w, PortEntry e, HealthResult? health)
    {
        var p = e.Process;
        w.WriteStartObject();
        w.WriteNumber("port", e.Port);
        w.WriteString("protocol", Format.Protocol(e.Binding));
        w.WriteString("address", e.Binding.LocalAddress.ToString());
        if (e.Known is { } known)
        {
            w.WriteString("service", known.Name);
        }

        w.WriteNumber("pid", e.ProcessId);
        w.WriteString("process", p.DisplayName);
        w.WriteString("commandLine", p.CommandLine);
        w.WriteString("workingDirectory", p.WorkingDirectory);
        if (p.StartTime is { } start)
        {
            w.WriteString("started", start);
        }

        if (p.Project is { } project)
        {
            w.WriteString("project", project.Name);
            w.WriteString("branch", project.Branch);
        }

        if (p.Agent is { } agent)
        {
            w.WriteString("agent", agent.Name);
            w.WriteNumber("agentPid", agent.ProcessId);
            w.WriteBoolean("agentRunning", agent.IsRunning);
        }

        w.WriteBoolean("leftBehind", p.IsOrphaned);
        if (p.Access != ProcessAccessLevel.Full)
        {
            w.WriteString("access", p.Access.ToString());
        }

        if (health is not null)
        {
            w.WritePropertyName("health");
            WriteHealth(w, health);
        }

        w.WriteEndObject();
    }

    private static void WriteHealth(Utf8JsonWriter w, HealthResult health)
    {
        w.WriteStartObject();
        w.WriteString("state", health.State.ToString());
        if (health.StatusCode is { } code)
        {
            w.WriteNumber("status", code);
        }

        w.WriteNumber("latencyMs", Math.Round(health.Latency.TotalMilliseconds, 1));
        w.WriteString("summary", health.Summary);
        w.WriteEndObject();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            write(w);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private void Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            write(w);
        }

        buffer.WriteByte((byte)'\n');
        _out.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        _out.Flush();
    }

    private static void Result(Utf8JsonWriter w, JsonElement id, Action<Utf8JsonWriter> body)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WriteStartObject("result");
        body(w);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Error(Utf8JsonWriter w, JsonElement? id, int code, string message)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        if (id is { } value)
        {
            value.WriteTo(w);
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
