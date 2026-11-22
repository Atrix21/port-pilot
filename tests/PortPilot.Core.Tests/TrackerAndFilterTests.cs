using PortPilot.Core.Discovery;
using PortPilot.Core.Model;

namespace PortPilot.Core.Tests;

[TestClass]
public sealed class PortChangeTrackerTests
{
    private readonly ManualClock _clock = new();

    [TestMethod]
    public void Initial_snapshot_is_a_baseline_without_new_highlights()
    {
        var tracker = new PortChangeTracker(_clock);

        var changes = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000), Entries.Tcp(5432)));

        Assert.IsTrue(changes.IsInitial);
        Assert.AreEqual(2, changes.Added.Count);
        Assert.IsTrue(changes.Added.All(p => !p.IsNew(_clock.GetUtcNow(), tracker.NewWindow)));
    }

    [TestMethod]
    public void Ports_opened_after_baseline_are_new_until_the_window_elapses()
    {
        var tracker = new PortChangeTracker(_clock);
        tracker.Apply(Entries.Snapshot(Entries.Tcp(3000)));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var changes = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000), Entries.Tcp(5173)));

        var opened = changes.Added.Single();
        Assert.AreEqual(5173, opened.Entry.Port);
        Assert.IsTrue(opened.IsNew(_clock.GetUtcNow(), tracker.NewWindow));

        _clock.Advance(tracker.NewWindow);
        Assert.IsFalse(opened.IsNew(_clock.GetUtcNow(), tracker.NewWindow));
    }

    [TestMethod]
    public void Closed_ports_linger_then_expire()
    {
        var tracker = new PortChangeTracker(_clock);
        tracker.Apply(Entries.Snapshot(Entries.Tcp(3000), Entries.Tcp(8080)));

        _clock.Advance(TimeSpan.FromSeconds(1));
        var closing = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000)));
        Assert.AreEqual(8080, closing.Closed.Single().Entry.Port);
        Assert.AreEqual(2, tracker.Ports.Count, "Closed ports stay visible for a moment");

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, tracker.Apply(Entries.Snapshot(Entries.Tcp(3000))).Expired.Count);

        _clock.Advance(tracker.ClosedLinger);
        var expiring = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000)));
        Assert.AreEqual(8080, expiring.Expired.Single().Entry.Port);
        Assert.AreEqual(1, tracker.Ports.Count);
    }

    [TestMethod]
    public void A_port_that_reopens_while_lingering_is_revived()
    {
        var tracker = new PortChangeTracker(_clock);
        tracker.Apply(Entries.Snapshot(Entries.Tcp(3000)));
        tracker.Apply(Entries.Snapshot());

        _clock.Advance(TimeSpan.FromSeconds(1));
        var changes = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000)));

        var revived = changes.Reopened.Single();
        Assert.IsFalse(revived.IsClosed);
        Assert.IsTrue(revived.IsNew(_clock.GetUtcNow(), tracker.NewWindow));
        Assert.AreEqual(0, changes.Added.Count);
    }

    [TestMethod]
    public void Same_port_owned_by_a_new_process_is_a_different_endpoint()
    {
        var tracker = new PortChangeTracker(_clock);
        tracker.Apply(Entries.Snapshot(Entries.Tcp(3000, pid: 100)));

        var changes = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000, pid: 200)));

        Assert.AreEqual(200, changes.Added.Single().Entry.ProcessId);
        Assert.AreEqual(100, changes.Closed.Single().Entry.ProcessId);
    }

    [TestMethod]
    public void Changed_process_metadata_is_reported_as_update()
    {
        var tracker = new PortChangeTracker(_clock);
        tracker.Apply(Entries.Snapshot(Entries.Tcp(3000, image: "node.exe")));

        var changes = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000, image: "bun.exe")));

        Assert.AreEqual("bun.exe", changes.Updated.Single().Entry.Process.ImageName);
        Assert.IsFalse(changes.Added.Any());
    }

    [TestMethod]
    public void Reset_makes_the_next_snapshot_a_baseline()
    {
        var tracker = new PortChangeTracker(_clock);
        tracker.Apply(Entries.Snapshot(Entries.Tcp(3000)));
        tracker.Reset();

        var changes = tracker.Apply(Entries.Snapshot(Entries.Tcp(3000), Entries.Tcp(4000)));

        Assert.IsTrue(changes.IsInitial);
        Assert.IsTrue(changes.Added.All(p => p.OpenedAt is null));
    }
}

[TestClass]
public sealed class PortFilterTests
{
    private static readonly PortEntry Vite = Entries.Tcp(5173, pid: 18244, image: "node.exe");
    private static readonly PortEntry Postgres = Entries.Tcp(5432, pid: 4120, image: "postgres.exe", address: "0.0.0.0");
    private static readonly PortEntry Mdns = Entries.Udp(5353, pid: 2210, image: "svchost.exe");
    private static readonly PortEntry Ephemeral = Entries.Tcp(49821, pid: 18244, state: ConnectionState.Established);
    private static readonly PortEntry Rpc = Entries.Tcp(135, pid: 1100, image: "svchost.exe");

    private static PortEntry[] All => [Vite, Postgres, Mdns, Ephemeral, Rpc];

    private static int[] Apply(PortFilter filter) => All.Where(filter.Matches).Select(e => e.Port).ToArray();

    [TestMethod]
    public void Default_filter_shows_listening_endpoints_only()
    {
        CollectionAssert.AreEquivalent(new[] { 5173, 5432, 5353, 135 }, Apply(PortFilter.Default));
    }

    [TestMethod]
    public void Protocol_filters()
    {
        CollectionAssert.AreEquivalent(new[] { 5353 }, Apply(new PortFilter(Protocol: ProtocolFilter.Udp)));
        CollectionAssert.AreEquivalent(new[] { 5173, 5432, 135 }, Apply(new PortFilter(Protocol: ProtocolFilter.Tcp)));
        CollectionAssert.AreEquivalent(new[] { 5173, 5432, 49821, 135 }, Apply(new PortFilter(Protocol: ProtocolFilter.Tcp, ListeningOnly: false)));
    }

    [TestMethod]
    public void Search_by_process_name_is_case_insensitive()
    {
        CollectionAssert.AreEquivalent(new[] { 5432 }, Apply(new PortFilter("POSTGRES")));
        CollectionAssert.AreEquivalent(new[] { 5353, 135 }, Apply(new PortFilter("svchost")));
    }

    [TestMethod]
    public void Numeric_search_matches_port_prefix_and_exact_pid()
    {
        CollectionAssert.AreEquivalent(new[] { 5173 }, Apply(new PortFilter("51")));
        CollectionAssert.AreEquivalent(new[] { 5432 }, Apply(new PortFilter("4120")));
        CollectionAssert.AreEquivalent(new[] { 5173, 5432, 5353 }, Apply(new PortFilter("5")));
    }

    [TestMethod]
    public void Prefixed_search_targets_ports_or_pids_explicitly()
    {
        CollectionAssert.AreEquivalent(new[] { 135 }, Apply(new PortFilter(":13")));
        CollectionAssert.AreEquivalent(new[] { 5173 }, Apply(new PortFilter("pid:18244")));
    }

    [TestMethod]
    public void Search_matches_known_port_names_and_addresses()
    {
        CollectionAssert.AreEquivalent(new[] { 5173 }, Apply(new PortFilter("vite")));
        CollectionAssert.AreEquivalent(new[] { 5432, 5353 }, Apply(new PortFilter("0.0.0.0")));
    }

    [TestMethod]
    public void Dev_ports_only()
    {
        CollectionAssert.AreEquivalent(new[] { 5173, 5432 }, Apply(new PortFilter(DevPortsOnly: true)));
    }

    [TestMethod]
    public void Well_known_ports_cover_common_development_defaults()
    {
        foreach (var port in new[] { 3000, 5173, 8000, 8080, 5432, 3306, 6379 })
        {
            Assert.IsTrue(WellKnownPorts.IsKnown(port), $"{port} should be recognised");
        }

        Assert.AreEqual("PostgreSQL", WellKnownPorts.Lookup(5432)!.Name);
        Assert.IsNull(WellKnownPorts.Lookup(49821));
    }
}
