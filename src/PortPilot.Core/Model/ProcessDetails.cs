namespace PortPilot.Core.Model;

public enum ProcessAccessLevel
{
    /// <summary>Executable path and start time could be read.</summary>
    Full,

    /// <summary>The process is visible but the OS refused to open it (usually needs elevation or root).</summary>
    Restricted,

    /// <summary>A kernel-owned pseudo process (System, System Idle Process).</summary>
    System,

    /// <summary>The process exited before it could be inspected.</summary>
    Exited,
}

/// <summary>
/// What PortPilot knows about the process that owns an endpoint.
/// </summary>
public sealed record ProcessDetails
{
    public required int ProcessId { get; init; }

    /// <summary>Image name as reported by Windows, e.g. <c>node.exe</c>.</summary>
    public required string ImageName { get; init; }

    public string? ExecutablePath { get; init; }

    public string? CommandLine { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    public int? ParentProcessId { get; init; }

    public ProcessAccessLevel Access { get; init; }

    /// <summary>The process's current directory, when the OS shares it (Linux, own processes or root).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The git repository the process is working in, if any.</summary>
    public ProjectInfo? Project { get; init; }

    /// <summary>The coding agent session (such as Claude Code) that started this process, if any.</summary>
    public AgentSession? Agent { get; init; }

    /// <summary>Started by an agent session that has since ended, and still running.</summary>
    public bool IsOrphaned => Agent is { IsRunning: false };

    /// <summary>Friendly name without the <c>.exe</c> suffix.</summary>
    public string DisplayName => ImageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? ImageName[..^4]
        : ImageName;

    public bool IsSystem => Access == ProcessAccessLevel.System;

    /// <summary>
    /// A placeholder for a process that could not be inspected. On Windows PIDs 0 and 4 are kernel pseudo
    /// processes; on Linux PID 0 means the socket's owner could not be determined (it belongs to another user).
    /// </summary>
    public static ProcessDetails Unknown(int pid) => OperatingSystem.IsWindows()
        ? new()
        {
            ProcessId = pid,
            ImageName = pid switch { 0 => "System Idle Process", 4 => "System", _ => $"PID {pid}" },
            Access = pid is 0 or 4 ? ProcessAccessLevel.System : ProcessAccessLevel.Exited,
        }
        : new()
        {
            ProcessId = pid,
            ImageName = pid == 0 ? "Unknown owner" : $"PID {pid}",
            Access = pid == 0 ? ProcessAccessLevel.Restricted : ProcessAccessLevel.Exited,
        };
}

/// <summary>A git repository or worktree, identified from a process's working directory.</summary>
public sealed record ProjectInfo(string Root, string Name, string? Branch, bool IsWorktree)
{
    /// <summary><c>atlas-web</c>, or <c>atlas-web · feature/auth</c> when on a branch.</summary>
    public string DisplayText => Branch is null ? Name : $"{Name} · {Branch}";
}

/// <summary>An agent process that launched other processes, such as a Claude Code session.</summary>
public sealed record AgentSession(int ProcessId, DateTimeOffset? StartTime, string Name, string? WorkingDirectory, bool IsRunning)
{
    public string Key => $"{ProcessId}@{StartTime?.UtcTicks}";
}
