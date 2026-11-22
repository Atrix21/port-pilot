namespace PortPilot.Core.Model;

public enum ProcessAccessLevel
{
    /// <summary>Executable path and start time could be read.</summary>
    Full,

    /// <summary>The process is visible but Windows refused to open it (usually needs elevation).</summary>
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

    /// <summary>Friendly name without the <c>.exe</c> suffix.</summary>
    public string DisplayName => ImageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? ImageName[..^4]
        : ImageName;

    public bool IsSystem => Access == ProcessAccessLevel.System;

    public static ProcessDetails Unknown(int pid) => new()
    {
        ProcessId = pid,
        ImageName = pid switch { 0 => "System Idle Process", 4 => "System", _ => $"PID {pid}" },
        Access = pid is 0 or 4 ? ProcessAccessLevel.System : ProcessAccessLevel.Exited,
    };
}
