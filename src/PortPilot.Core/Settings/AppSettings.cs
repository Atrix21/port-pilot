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

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public int RefreshIntervalSeconds { get; init; } = 2;

    public bool MinimizeToTray { get; init; }

    public bool CloseToTray { get; init; } = true;

    public bool NotifyOnNewDevPorts { get; init; } = true;

    public bool StartMinimized { get; init; }

    public ProtocolFilter Protocol { get; init; } = ProtocolFilter.All;

    public bool ListeningOnly { get; init; } = true;

    public bool DevPortsOnly { get; init; }

    public SortColumn SortColumn { get; init; } = SortColumn.Port;

    public bool SortDescending { get; init; }

    public WindowPlacementSettings? Window { get; init; }

    public bool HasShownTrayHint { get; init; }

    public AppSettings Normalize() => this with
    {
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 1, 60),
    };
}
