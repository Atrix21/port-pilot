using PortPilot.Core.Discovery;

namespace PortPilot.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum SortColumn
{
    Port,
    Process,
    Pid,
    Protocol,
    Address,
    Status,
}

public sealed record WindowPlacementSettings(double Left, double Top, double Width, double Height, bool IsMaximized);

/// <summary>
/// User preferences persisted between sessions.
/// </summary>
public sealed record AppSettings
{
    public static readonly int[] RefreshIntervalChoices = [1, 2, 5, 10];


    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public int RefreshIntervalSeconds { get; set; } = 2;

    public bool MinimizeToTray { get; set; }

    public bool CloseToTray { get; set; } = true;

    public bool NotifyOnNewDevPorts { get; set; } = true;

    public bool StartMinimized { get; set; }

    public ProtocolFilter Protocol { get; set; } = ProtocolFilter.All;

    public bool ListeningOnly { get; set; } = true;

    public bool DevPortsOnly { get; set; }

    public SortColumn SortColumn { get; set; } = SortColumn.Port;

    public bool SortDescending { get; set; }

    public WindowPlacementSettings? Window { get; set; }

    public bool HasShownTrayHint { get; set; }

    /// <summary>Show apps grouped by project (true) or the full port table (false).</summary>
    public bool OverviewMode { get; set; } = true;

    /// <summary>Extra agent executables to recognise, on top of the built-in list (Claude Code, Codex, Gemini CLI, Aider, …).</summary>
    /// <remarks>Comma-separated, so the record keeps value equality.</remarks>
    public string CustomAgents { get; set; } = "";

    public IReadOnlyList<string> CustomAgentNames =>
        (CustomAgents ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public AppSettings Normalize() => this with
    {
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 1, 60),
    };
}
