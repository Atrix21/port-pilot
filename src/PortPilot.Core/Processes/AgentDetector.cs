using System.Text;
using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Finds the coding agent session, such as Claude Code, that a process descends from, by walking its
/// parent chain on Linux. Agents are recognised by their command name or executable path.
/// </summary>
public static class AgentDetector
{
    private const int MaxDepth = 32;

    /// <summary>Returns the nearest agent ancestor of <paramref name="pid"/>, or null. The process itself is not considered.</summary>
    public static AgentSession? FindAncestor(int pid, int parentId)
    {
        var current = parentId;
        for (var depth = 0; depth < MaxDepth && current > 1; depth++)
        {
            if (!ProcFs.TryReadStat(current, out var stat, out var comm) || stat.IsGone)
            {
                return null;
            }

            if (Identify(current, comm) is { } name)
            {
                return new AgentSession(current, ProcFs.ToStartTime(stat.StartTicks), name, ProcFs.ReadLink($"/proc/{current}/cwd"), IsRunning: true);
            }

            current = stat.ParentId;
        }

        return null;
    }

    /// <summary>The agent's display name if <paramref name="pid"/> is an agent process.</summary>
    public static string? Identify(int pid, string? comm)
    {
        if (comm == "claude")
        {
            return "Claude Code";
        }

        // Claude Code installed through npm runs as "node …/@anthropic-ai/claude-code/cli.js".
        if (comm is "node" or "bun")
        {
            var cmdline = ProcFs.ReadSmall($"/proc/{pid}/cmdline");
            if (cmdline.IndexOf("@anthropic-ai/claude-code"u8) >= 0 || EndsWithArg(cmdline, "/claude"u8))
            {
                return "Claude Code";
            }
        }

        return null;
    }

    private static bool EndsWithArg(ReadOnlySpan<byte> cmdline, ReadOnlySpan<byte> suffix)
    {
        // argv[1] of "node /usr/local/bin/claude".
        var first = cmdline.IndexOf((byte)0);
        if (first < 0)
        {
            return false;
        }

        var rest = cmdline[(first + 1)..];
        var end = rest.IndexOf((byte)0);
        var arg = end < 0 ? rest : rest[..end];
        return arg.EndsWith(suffix);
    }
}
