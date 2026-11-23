using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PortPilot.Core;
using PortPilot.Core.Diagnostics;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Cli;

/// <summary>
/// Claude Code integrations that run on every prompt or command, so they must be fast and quiet:
/// a PreToolUse guard that stops a second copy of a dev server from being started, and a status line.
/// </summary>
internal static partial class Guard
{
    // Commands that start a long-running dev server.
    [GeneratedRegex(@"(?x)
        \b(npm|pnpm|yarn|bun)\s+(run\s+)?(dev|start|serve|preview|storybook)\b
      | \b(npx\s+|bunx\s+|pnpm\s+(exec|dlx)\s+)?(vite|next\s+(dev|start)|nuxt\s+dev|nuxi\s+dev|astro\s+dev|remix\s+(vite:)?dev|svelte-kit\s+dev|ng\s+serve|vue-cli-service\s+serve|webpack(-dev-server|\s+serve)|parcel(\s+serve)?|gatsby\s+develop|eleventy\s+--serve|docusaurus\s+start|storybook\s+dev|expo\s+start|react-scripts\s+start|wrangler\s+dev|vercel\s+dev|netlify\s+dev)\b
      | \bmanage\.py\s+runserver\b | \bflask\s+run\b | \b(uvicorn|gunicorn|hypercorn|granian|fastapi\s+(dev|run))\b | streamlit\s+run | jupyter\s+(lab|notebook)\b
      | \brails\s+s(erver)?\b | \bbin/dev\b | \bphp\s+artisan\s+serve\b | \bphp\s+-S\b | \bsymfony\s+serve\b
      | \bhugo\s+server\b | \bjekyll\s+serve\b | \bmkdocs\s+serve\b | \bzola\s+serve\b
      | \bdotnet\s+(watch|run)\b | \bmvn\s+spring-boot:run\b | \bgradlew?\s+bootRun\b | \bcargo\s+(leptos\s+)?watch\b | \btrunk\s+serve\b
      | \bpython3?\s+-m\s+http\.server\b | \bhttp-server\b | \blive-server\b | \bserve\s+-[sl]\b
    ", RegexOptions.IgnoreCase)]
    private static partial Regex DevServer();

    [GeneratedRegex(@"(?:--port[=\s]+|-p\s+|\bPORT=|--listen[=\s]+\S*:|runserver\s+(?:\S*:)?|-S\s+\S*:|--bind[=\s]+\S*:|http\.server\s+)(\d{2,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequestedPort();

    /// <summary>
    /// PreToolUse hook for Bash. Silent (exit 0, no output) unless the command starts a dev server that
    /// is already running for this project, or asks for a port something else holds; then it denies the
    /// call and tells Claude what is running, where, and how to stop it if a restart is really wanted.
    /// </summary>
    public static async Task<int> PreBashAsync()
    {
        string? command = null, cwd = null;
        try
        {
            using var input = JsonDocument.Parse(await Console.In.ReadToEndAsync().ConfigureAwait(false));
            var root = input.RootElement;
            cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() : null;
            if (root.TryGetProperty("tool_input", out var tool) && tool.TryGetProperty("command", out var cmd))
            {
                command = cmd.GetString();
            }
        }
        catch (JsonException)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(command) || !DevServer().IsMatch(command) || string.IsNullOrEmpty(cwd))
        {
            return 0;
        }

        var entries = PlatformServices.CreateScanner().Scan(false).Entries
            .Where(e => e.Binding.Protocol == TransportProtocol.Tcp && e.Binding.State == ConnectionState.Listening)
            .ToList();

        // An explicit port that is taken: say by what, and suggest free ones.
        if (RequestedPort().Match(command) is { Success: true } m && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var port))
        {
            var holder = entries.FirstOrDefault(e => e.Port == port);
            if (holder is null)
            {
                return 0;
            }

            var mine = InProject(holder, cwd);
            return Deny(mine
                ? $"{Describe(holder)} is already running on port {port} for this project{await HealthSuffixAsync(holder)}. Reuse it at {Url(holder)} instead of starting another copy. If it really needs restarting, stop it first with: portpilot kill pid:{holder.ProcessId} -y"
                : $"Port {port} is already in use by {Describe(holder)}{(holder.Process.Project is { } p ? $" (project {p.Name})" : "")}. Use a free port instead, for example {string.Join(", ", FreePorts(port, entries))}, or stop it with: portpilot kill pid:{holder.ProcessId} -y");
        }

        // No port given: is this project already serving something that answers?
        var running = entries
            .Where(e => InProject(e, cwd) && e.Known?.Kind != KnownPortKind.DataStore && !IsAgent(e))
            .GroupBy(e => e.ProcessId)
            .Select(g => g.OrderBy(e => e.Port).First())
            .ToList();
        foreach (var server in running)
        {
            var health = await HealthProbe.CheckAsync(server).ConfigureAwait(false);
            if (health.State == HealthState.Healthy)
            {
                return Deny($"This project already has a dev server running: {Describe(server)} at {Url(server)} ({health.Summary}). Reuse it instead of starting a second copy. If it really needs restarting, stop it first with: portpilot kill pid:{server.ProcessId} -y");
            }
        }

        return 0;
    }

    /// <summary>
    /// Claude Code status line: this project's servers and anything left behind, in one short line.
    /// </summary>
    public static async Task<int> StatusLineAsync()
    {
        string? dir = null;
        try
        {
            using var input = JsonDocument.Parse(await Console.In.ReadToEndAsync().ConfigureAwait(false));
            var root = input.RootElement;
            dir = root.TryGetProperty("workspace", out var ws) && ws.TryGetProperty("current_dir", out var d) ? d.GetString()
                : root.TryGetProperty("cwd", out var c) ? c.GetString() : null;
        }
        catch (JsonException)
        {
        }

        dir ??= Directory.GetCurrentDirectory();
        var entries = PlatformServices.CreateScanner().Scan(false).Entries;
        var mine = entries
            .Where(e => e.Binding.Protocol == TransportProtocol.Tcp && e.Binding.State == ConnectionState.Listening && InProject(e, dir) && !IsAgent(e))
            .GroupBy(e => e.Port).Select(g => g.First()).OrderBy(e => e.Port).ToList();
        var leftovers = entries.Where(e => e.Process.IsOrphaned).Select(e => e.ProcessId).Distinct().Count();

        const string dim = "\e[2m", green = "\e[32m", yellow = "\e[33m", reset = "\e[0m";
        var sb = new StringBuilder();
        sb.Append(dim).Append("⚓ ").Append(reset);
        if (mine.Count == 0)
        {
            sb.Append(dim).Append("no dev servers").Append(reset);
        }
        else
        {
            sb.AppendJoin("  ", mine.Take(4).Select(e => $"{green}:{e.Port}{reset} {dim}{e.Known?.Name ?? e.Process.DisplayName}{reset}"));
            if (mine.Count > 4)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {dim}+{mine.Count - 4}{reset}");
            }
        }

        if (leftovers > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {yellow}⚠ {leftovers} left behind{reset}");
        }

        Console.Out.Write(sb.Append('\n').ToString());
        return 0;
    }

    /// <summary>Same git project as the directory, or running from inside it.</summary>
    private static bool InProject(PortEntry e, string dir)
    {
        var project = ProjectResolver.Resolve(dir);
        if (project is not null && e.Process.Project is { } p)
        {
            return p.Name == project.Name && (p.Root == project.Root || p.IsWorktree == project.IsWorktree);
        }

        var cwd = e.Process.WorkingDirectory;
        return cwd is not null && (cwd == dir || cwd.StartsWith(dir.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>The agent's own sockets (Claude Code listens on loopback ports) are not dev servers.</summary>
    private static bool IsAgent(PortEntry e) =>
        ToolingDetector.IsTooling(e.Process) || AgentDetector.Identify(e.Process.ImageName, () => e.Process.CommandLine) is not null;

    private static string Describe(PortEntry e) =>
        $"{e.Known?.Name ?? e.Process.DisplayName} (PID {e.ProcessId}{(e.Process.Agent is { } a ? $", started by {a.Name}" : "")})";

    private static string Url(PortEntry e) => $"{(e.Known?.PrefersHttps == true ? "https" : "http")}://localhost:{e.Port}";

    private static async Task<string> HealthSuffixAsync(PortEntry e)
    {
        var health = await HealthProbe.CheckAsync(e).ConfigureAwait(false);
        return health.State is HealthState.Healthy or HealthState.Accepting ? $", answering ({health.Summary})" : $", but not answering ({health.Summary})";
    }

    private static IEnumerable<int> FreePorts(int near, List<PortEntry> entries)
    {
        var used = entries.Select(e => e.Port).ToHashSet();
        return Enumerable.Range(near + 1, 200).Where(p => p <= 65535 && !used.Contains(p)).Take(3);
    }

    private static int Deny(string reason)
    {
        using var stdout = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stdout);
        w.WriteStartObject();
        w.WriteStartObject("hookSpecificOutput");
        w.WriteString("hookEventName", "PreToolUse");
        w.WriteString("permissionDecision", "deny");
        w.WriteString("permissionDecisionReason", "PortPilot: " + reason);
        w.WriteEndObject();
        w.WriteEndObject();
        w.Flush();
        return 0;
    }
}
