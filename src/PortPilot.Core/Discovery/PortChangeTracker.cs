using PortPilot.Core.Model;

namespace PortPilot.Core.Discovery;

/// <summary>
/// The lifecycle of one endpoint as seen across successive snapshots.
/// </summary>
public sealed class TrackedPort(PortEntry entry, DateTimeOffset? openedAt)
{
    public PortEntry Entry { get; internal set; } = entry;

    /// <summary>When PortPilot first saw the endpoint open; null for endpoints present at startup.</summary>
    public DateTimeOffset? OpenedAt { get; internal set; } = openedAt;

    /// <summary>When the endpoint disappeared; null while it is still open.</summary>
    public DateTimeOffset? ClosedAt { get; internal set; }

    public string Key => Entry.Key;

    public bool IsClosed => ClosedAt is not null;

    public bool IsNew(DateTimeOffset now, TimeSpan window) =>
        ClosedAt is null && OpenedAt is { } opened && now - opened < window;
}

public sealed record PortChangeSet(
    IReadOnlyList<TrackedPort> Added,
    IReadOnlyList<TrackedPort> Updated,
    IReadOnlyList<TrackedPort> Closed,
    IReadOnlyList<TrackedPort> Reopened,
    IReadOnlyList<TrackedPort> Expired,
    bool IsInitial)
{
    public bool HasChanges => Added.Count + Updated.Count + Closed.Count + Reopened.Count + Expired.Count > 0;
}

/// <summary>
/// Diffs snapshots to highlight newly opened endpoints and keep recently closed ones visible for a moment.
/// </summary>
public sealed class PortChangeTracker(TimeProvider? clock = null)
{
    public static readonly TimeSpan DefaultNewWindow = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DefaultClosedLinger = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, TrackedPort> _ports = [];
    private bool _initialized;

    public TimeSpan NewWindow { get; init; } = DefaultNewWindow;

    public TimeSpan ClosedLinger { get; init; } = DefaultClosedLinger;

    public IReadOnlyCollection<TrackedPort> Ports => _ports.Values;

    public PortChangeSet Apply(PortSnapshot snapshot)
    {
        var now = _clock.GetUtcNow();
        var isInitial = !_initialized;
        var added = new List<TrackedPort>();
        var updated = new List<TrackedPort>();
        var closed = new List<TrackedPort>();
        var reopened = new List<TrackedPort>();
        var seen = new HashSet<string>(snapshot.Entries.Count);

        foreach (var entry in snapshot.Entries)
        {
            if (!seen.Add(entry.Key))
            {
                continue;
            }

            if (_ports.TryGetValue(entry.Key, out var tracked))
            {
                if (tracked.IsClosed)
                {
                    tracked.ClosedAt = null;
                    tracked.OpenedAt = now;
                    tracked.Entry = entry;
                    reopened.Add(tracked);
                }
                else if (!Equals(tracked.Entry, entry))
                {
                    tracked.Entry = entry;
                    updated.Add(tracked);
                }
            }
            else
            {
                tracked = new TrackedPort(entry, isInitial ? null : now);
                _ports.Add(entry.Key, tracked);
                added.Add(tracked);
            }
        }

        foreach (var tracked in _ports.Values)
        {
            if (!tracked.IsClosed && !seen.Contains(tracked.Key))
            {
                tracked.ClosedAt = now;
                closed.Add(tracked);
            }
        }

        var expired = Expire(now);
        _initialized = true;
        return new PortChangeSet(added, updated, closed, reopened, expired, isInitial);
    }

    /// <summary>Removes closed endpoints whose linger period has elapsed.</summary>
    public IReadOnlyList<TrackedPort> Expire(DateTimeOffset now)
    {
        List<TrackedPort>? expired = null;
        foreach (var tracked in _ports.Values)
        {
            if (tracked.ClosedAt is { } closedAt && now - closedAt >= ClosedLinger)
            {
                (expired ??= []).Add(tracked);
            }
        }

        if (expired is null)
        {
            return [];
        }

        foreach (var tracked in expired)
        {
            _ports.Remove(tracked.Key);
        }

        return expired;
    }

    /// <summary>Forgets everything, e.g. when scan options change so the next snapshot is treated as a baseline.</summary>
    public void Reset()
    {
        _ports.Clear();
        _initialized = false;
    }
}
