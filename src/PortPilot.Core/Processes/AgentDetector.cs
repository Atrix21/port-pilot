using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>A coding agent PortPilot can recognise in a process tree.</summary>
/// <param name="Name">Full name, e.g. "Claude Code".</param>
/// <param name="ShortName">Tag text, e.g. "Claude".</param>
/// <param name="Executables">Executable names (without <c>.exe</c>) that are the agent itself.</param>
/// <param name="Markers">Command-line fragments that identify the agent when it runs under a runtime such as node or python.</param>
public sealed record AgentKind(string Name, string ShortName, string[] Executables, string[] Markers);

/// <summary>
/// Recognises coding agents (Claude Code, Codex, Gemini CLI, Aider, opencode, …) by executable name, or by
/// command line when they run under node, bun, deno or python, and finds the nearest one above a process.
/// </summary>
public static class AgentDetector
{
    private const int MaxDepth = 32;

    private static readonly string[] Runtimes = ["node", "bun", "deno", "python", "python3", "uv", "uvx", "npx", "pnpm"];

    public static IReadOnlyList<AgentKind> BuiltIn { get; } =
    [
        new("Claude Code", "Claude", ["claude"], ["@anthropic-ai/claude-code", "/claude-code/cli.js"]),
        new("Codex", "Codex", ["codex"], ["@openai/codex"]),
        new("Gemini CLI", "Gemini", ["gemini"], ["@google/gemini-cli"]),
        new("Aider", "Aider", ["aider"], ["aider-chat", "/bin/aider", "-m aider"]),
        new("opencode", "opencode", ["opencode"], ["opencode-ai"]),
        new("Cursor Agent", "Cursor", ["cursor-agent"], ["cursor-agent"]),
        new("Amp", "Amp", ["amp"], ["@sourcegraph/amp"]),
        new("Goose", "Goose", ["goose"], []),
    ];

    private static volatile IReadOnlyList<AgentKind> s_agents = BuiltIn;

    public static IReadOnlyList<AgentKind> Agents => s_agents;

    /// <summary>Adds user-defined agents by executable name (from settings), on top of the built-in list.</summary>
    public static void Configure(IEnumerable<string>? extraExecutables)
    {
        var extra = (extraExecutables ?? [])
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !BuiltIn.Any(a => a.Executables.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Select(n => new AgentKind(n, n, [n], []))
            .ToList();
        s_agents = [.. BuiltIn, .. extra];
    }

    /// <summary>The short tag for an agent name, e.g. "Claude" for "Claude Code".</summary>
    public static string ShortNameFor(string name) =>
        Agents.FirstOrDefault(a => a.Name == name)?.ShortName ?? name;

    /// <summary>Identifies an agent from its executable name and, for runtimes only, its command line.</summary>
    public static AgentKind? Identify(string? executable, Func<string?> commandLine)
    {
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        var name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable[..^4] : executable;
        foreach (var agent in Agents)
        {
            if (agent.Executables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return agent;
            }
        }

        if (!Runtimes.Contains(name, StringComparer.OrdinalIgnoreCase) && !name.StartsWith("python", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cmd = commandLine()?.Replace('\\', '/');
        if (string.IsNullOrEmpty(cmd))
        {
            return null;
        }

        foreach (var agent in Agents)
        {
            if (agent.Markers.Any(m => cmd.Contains(m, StringComparison.OrdinalIgnoreCase))
                || agent.Executables.Any(e => SecondArgumentIs(cmd, e)))
            {
                return agent;
            }
        }

        return null;
    }

    /// <summary>"node /usr/local/bin/claude …" – argv[1] is the agent's launcher script.</summary>
    private static bool SecondArgumentIs(string cmd, string executable)
    {
        var parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && Path.GetFileNameWithoutExtension(parts[1].Trim('"')).Equals(executable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Linux: the nearest agent above <paramref name="pid"/>. The process itself is not considered.</summary>
    public static AgentSession? FindAncestor(int pid, int parentId)
    {
        var current = parentId;
        for (var depth = 0; depth < MaxDepth && current > 1; depth++)
        {
            if (!ProcFs.TryReadStat(current, out var stat, out var comm) || stat.IsGone)
            {
                return null;
            }

            var id = current;
            if (Identify(comm, () => ReadCommandLine(id)) is { } agent)
            {
                return new AgentSession(current, ProcFs.ToStartTime(stat.StartTicks), agent.Name, ProcFs.ReadLink($"/proc/{current}/cwd"), IsRunning: true);
            }

            current = stat.ParentId;
        }

        return null;
    }

    private static string? ReadCommandLine(int pid)
    {
        var data = ProcFs.ReadSmall($"/proc/{pid}/cmdline").TrimEnd((byte)0);
        return data.IsEmpty ? null : System.Text.Encoding.UTF8.GetString(data).Replace('\0', ' ');
    }
}
