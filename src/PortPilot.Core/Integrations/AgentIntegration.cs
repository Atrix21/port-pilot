using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortPilot.Core.Integrations;

/// <summary>The outcome of an install or uninstall, with a sentence for the user.</summary>
public sealed record IntegrationResult(bool Succeeded, string Message);

/// <summary>
/// Connects PortPilot to coding agents: a Claude Code SessionEnd hook, a background watcher that starts
/// at login, and registration of the MCP server. Every entry point runs the terminal app
/// (<see cref="CliPath"/>), which ships next to both desktop apps.
/// </summary>
public static class AgentIntegration
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ClaudeSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>
    /// The terminal app: this process when it is the CLI, otherwise <c>portpilot</c> beside the desktop app
    /// (<c>cli\portpilot.exe</c> on Windows, where file names are case-insensitive).
    /// </summary>
    public static string? CliPath
    {
        get
        {
            var self = Environment.ProcessPath;
            if (self is not null && Path.GetFileNameWithoutExtension(self) == "portpilot")
            {
                return self;
            }

            var dir = AppContext.BaseDirectory;
            string[] candidates = OperatingSystem.IsWindows()
                ? [Path.Combine(dir, "cli", "portpilot.exe")]
                : [Path.Combine(dir, "portpilot"), "/usr/local/bin/portpilot", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "portpilot")];
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    // ═════════════ Claude Code hook ═════════════

    public static bool IsHookInstalled => FindHook(ReadClaudeSettings()) is not null;

    public static bool HookStopsLeftovers => FindHook(ReadClaudeSettings())?.Contains("--stop", StringComparison.Ordinal) == true;

    public static IntegrationResult InstallHook(string cliPath, bool stop)
    {
        try
        {
            var settings = ReadClaudeSettings();
            var hooks = settings["hooks"] as JsonObject ?? [];
            settings["hooks"] = hooks;
            var sessionEnd = hooks["SessionEnd"] as JsonArray ?? [];
            hooks["SessionEnd"] = sessionEnd;
            RemoveOurHook(sessionEnd);
            sessionEnd.Add((JsonNode)new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"\"{cliPath}\" hook session-end{(stop ? " --stop" : "")}",
                    ["timeout"] = 15,
                }),
            });
            WriteClaudeSettings(settings);
            return new(true, stop
                ? "When a Claude Code session ends, PortPilot records what it started and stops anything still holding a port."
                : "When a Claude Code session ends, PortPilot records what it started, so leftovers show up as \"Left behind\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, $"Couldn't update {ClaudeSettingsPath}: {ex.Message}");
        }
    }

    public static IntegrationResult UninstallHook()
    {
        try
        {
            var settings = ReadClaudeSettings();
            if (settings["hooks"]?["SessionEnd"] is JsonArray sessionEnd)
            {
                RemoveOurHook(sessionEnd);
                if (sessionEnd.Count == 0)
                {
                    (settings["hooks"] as JsonObject)?.Remove("SessionEnd");
                }

                WriteClaudeSettings(settings);
            }

            return new(true, "Removed PortPilot's Claude Code hook.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, $"Couldn't update {ClaudeSettingsPath}: {ex.Message}");
        }
    }

    private static bool IsOurs(JsonObject hook) => hook["command"] is JsonValue v && v.TryGetValue<string>(out var command) && command.Contains("hook session-end", StringComparison.Ordinal);

    private static string? FindHook(JsonObject settings) =>
        (settings["hooks"]?["SessionEnd"] as JsonArray)?.OfType<JsonObject>()
            .SelectMany(g => (g["hooks"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(IsOurs)
            .Select(h => h["command"]!.GetValue<string>())
            .FirstOrDefault();

    private static void RemoveOurHook(JsonArray sessionEnd)
    {
        foreach (var group in sessionEnd.OfType<JsonObject>().ToList())
        {
            if (group["hooks"] is JsonArray list)
            {
                foreach (var hook in list.OfType<JsonObject>().Where(IsOurs).ToList())
                {
                    list.Remove(hook);
                }

                if (list.Count == 0)
                {
                    sessionEnd.Remove(group);
                }
            }
        }
    }

    private static JsonObject ReadClaudeSettings()
    {
        if (!File.Exists(ClaudeSettingsPath))
        {
            return [];
        }

        var text = File.ReadAllText(ClaudeSettingsPath);
        return string.IsNullOrWhiteSpace(text)
            ? []
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [];
    }

    private static void WriteClaudeSettings(JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSettingsPath)!);
        if (File.Exists(ClaudeSettingsPath))
        {
            File.Copy(ClaudeSettingsPath, ClaudeSettingsPath + ".portpilot-backup", overwrite: true);
        }

        var temp = ClaudeSettingsPath + ".tmp";
        File.WriteAllText(temp, settings.ToJsonString(WriteOptions) + "\n");
        File.Move(temp, ClaudeSettingsPath, overwrite: true);
    }

    // ═════════════ Background watcher ═════════════

    private static string SystemdUnitPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user", "portpilot-watch.service");

    private const string WindowsTask = "PortPilot Watcher";

    public static bool IsWatcherInstalled => OperatingSystem.IsWindows() ? Run("schtasks", "/Query", "/TN", WindowsTask) : File.Exists(SystemdUnitPath);

    public static IntegrationResult InstallWatcher(string cliPath)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SystemdUnitPath)!);
            File.WriteAllText(SystemdUnitPath, $"""
                [Unit]
                Description=PortPilot background watcher (records what agent sessions start)

                [Service]
                ExecStart="{cliPath}" watch
                Restart=on-failure
                Nice=10

                [Install]
                WantedBy=default.target

                """);
            var ok = Run("systemctl", "--user", "daemon-reload") && Run("systemctl", "--user", "enable", "--now", "portpilot-watch.service");
            return new(ok, ok
                ? "The watcher is running, and starts with your session (systemd user service portpilot-watch)."
                : "Wrote the service, but systemctl --user failed. Start it with: systemctl --user enable --now portpilot-watch");
        }

        if (OperatingSystem.IsWindows())
        {
            var ok = Run("schtasks", "/Create", "/F", "/SC", "ONLOGON", "/TN", WindowsTask, "/TR", $"\"{cliPath}\" watch", "/RL", "LIMITED")
                && Run("schtasks", "/Run", "/TN", WindowsTask);
            return new(ok, ok ? "The watcher is running, and starts when you sign in (scheduled task \"PortPilot Watcher\")." : "Couldn't create the scheduled task.");
        }

        return new(false, "The watcher supports Linux and Windows.");
    }

    public static IntegrationResult UninstallWatcher()
    {
        if (OperatingSystem.IsLinux())
        {
            Run("systemctl", "--user", "disable", "--now", "portpilot-watch.service");
            File.Delete(SystemdUnitPath);
            Run("systemctl", "--user", "daemon-reload");
        }
        else if (OperatingSystem.IsWindows())
        {
            Run("schtasks", "/End", "/TN", WindowsTask);
            Run("schtasks", "/Delete", "/F", "/TN", WindowsTask);
        }

        return new(true, "The watcher is stopped and removed.");
    }

    // ═════════════ MCP server ═════════════

    public static IntegrationResult RegisterMcp(string cliPath)
    {
        var ok = Run("claude", "mcp", "add", "--scope", "user", "portpilot", "--", cliPath, "mcp");
        return new(ok, ok
            ? "Registered the PortPilot MCP server with Claude Code. New sessions can ask it about ports, projects, health and logs."
            : $"Couldn't run `claude mcp add`. Register it yourself:\n  claude mcp add --scope user portpilot -- \"{cliPath}\" mcp");
    }

    public static IntegrationResult UnregisterMcp()
    {
        var ok = Run("claude", "mcp", "remove", "--scope", "user", "portpilot");
        return new(ok, ok ? "Removed the PortPilot MCP server from Claude Code." : "Couldn't run `claude mcp remove --scope user portpilot`.");
    }

    public static bool IsMcpRegistered
    {
        get
        {
            // ~/.claude.json keeps user-scoped MCP servers under "mcpServers".
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            try
            {
                return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["mcpServers"]?["portpilot"] is not null;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return false;
            }
        }
    }

    private static bool Run(string program, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
