namespace PortPilot.Core.Model;

/// <summary>
/// An endpoint joined with information about its owning process.
/// </summary>
public sealed record PortEntry(PortBinding Binding, ProcessDetails Process, KnownPort? Known)
{
    public string Key => Binding.Key;

    public int Port => Binding.LocalPort;

    public int ProcessId => Binding.ProcessId;
}

/// <summary>
/// A point-in-time view of every endpoint on the machine.
/// </summary>
public sealed record PortSnapshot(IReadOnlyList<PortEntry> Entries, DateTimeOffset Timestamp, TimeSpan Duration)
{
    /// <summary>Endpoints grouped into apps with their CPU and memory; only when the scan was asked for it.</summary>
    public IReadOnlyList<Apps.AppInfo>? Apps { get; init; }

    public static PortSnapshot Empty { get; } = new([], DateTimeOffset.MinValue, TimeSpan.Zero);
}
