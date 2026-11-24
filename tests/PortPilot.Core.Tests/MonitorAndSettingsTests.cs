using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Settings;

namespace PortPilot.Core.Tests;

[TestClass]
public sealed class PortMonitorTests
{
    private sealed class CountingScanner : IPortScanner
    {
        private int _scans;

        public int Scans => Volatile.Read(ref _scans);

        public bool LastIncludeAll { get; private set; }

        public PortSnapshot Scan(bool includeAllTcpStates)
        {
            LastIncludeAll = includeAllTcpStates;
            Interlocked.Increment(ref _scans);
            return Entries.Snapshot(Entries.Tcp(3000));
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("Condition not met in time");
            }

            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task Scans_immediately_on_start_and_then_on_interval()
    {
        var scanner = new CountingScanner();
        await using var monitor = new PortMonitor(scanner, TimeSpan.FromMilliseconds(500));
        var snapshots = 0;
        monitor.SnapshotAvailable += (_, _) => Interlocked.Increment(ref snapshots);

        monitor.Start();

        await WaitUntil(() => Volatile.Read(ref snapshots) >= 1, 400);
        await WaitUntil(() => Volatile.Read(ref snapshots) >= 3, 2500);
    }

    [TestMethod]
    public async Task Paused_monitor_only_scans_on_demand()
    {
        var scanner = new CountingScanner();
        await using var monitor = new PortMonitor(scanner, TimeSpan.FromMilliseconds(500));
        monitor.Start();
        await WaitUntil(() => scanner.Scans >= 1);

        monitor.IsPaused = true;
        await Task.Delay(100);
        var baseline = scanner.Scans;
        await Task.Delay(1200);
        Assert.AreEqual(baseline, scanner.Scans, "No scans while paused");

        monitor.RefreshNow();
        await WaitUntil(() => scanner.Scans == baseline + 1);
    }

    [TestMethod]
    public async Task Changing_scan_scope_triggers_a_scan_with_new_options()
    {
        var scanner = new CountingScanner();
        await using var monitor = new PortMonitor(scanner, TimeSpan.FromSeconds(30));
        monitor.Start();
        await WaitUntil(() => scanner.Scans >= 1);

        monitor.IncludeAllTcpStates = true;

        await WaitUntil(() => scanner.Scans >= 2 && scanner.LastIncludeAll);
    }

    [TestMethod]
    public async Task Scan_failures_are_reported_and_do_not_stop_the_loop()
    {
        var calls = 0;
        var scanner = new DelegateScanner(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return PortSnapshot.Empty;
        });
        await using var monitor = new PortMonitor(scanner, TimeSpan.FromMilliseconds(500));
        Exception? failure = null;
        monitor.ScanFailed += (_, ex) => failure = ex;

        monitor.Start();

        await WaitUntil(() => Volatile.Read(ref calls) >= 2, 2000);
        Assert.IsInstanceOfType<InvalidOperationException>(failure);
    }

    private sealed class DelegateScanner(Func<PortSnapshot> scan) : IPortScanner
    {
        public PortSnapshot Scan(bool includeAllTcpStates) => scan();
    }
}

[TestClass]
public sealed class SettingsStoreTests
{
    private string _path = null!;

    [TestInitialize]
    public void Init() => _path = Path.Combine(Path.GetTempPath(), $"portpilot-test-{Guid.NewGuid():N}", "settings.json");

    [TestCleanup]
    public void Cleanup()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Missing_file_yields_defaults()
    {
        var settings = new JsonSettingsStore(_path).Load();

        Assert.AreEqual(2, settings.RefreshIntervalSeconds);
        Assert.AreEqual(ThemePreference.System, settings.Theme);
    }

    [TestMethod]
    public void Round_trips_settings()
    {
        var store = new JsonSettingsStore(_path);
        var saved = new AppSettings
        {
            Theme = ThemePreference.Dark,
            RefreshIntervalSeconds = 5,
            Protocol = ProtocolFilter.Udp,
            SortColumn = SortColumn.Pid,
            SortDescending = true,
            Window = new WindowPlacementSettings(10, 20, 1200, 800, true),
        };

        store.Save(saved);
        var loaded = store.Load();

        Assert.AreEqual(saved, loaded);
        StringAssert.Contains(File.ReadAllText(_path), "\"theme\": \"dark\"");
    }

    [TestMethod]
    public void Corrupt_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        Assert.AreEqual(new AppSettings(), new JsonSettingsStore(_path).Load());
    }

    [TestMethod]
    public void Out_of_range_values_are_normalised()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ \"refreshIntervalSeconds\": 0 }");

        Assert.AreEqual(1, new JsonSettingsStore(_path).Load().RefreshIntervalSeconds);
    }

    [TestMethod]
    public void Settings_from_an_older_version_keep_defaults_for_missing_keys()
    {
        // A v1.0.0 file has none of the keys added later. Those must fall back to their defaults, not null/false.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ \"theme\": \"dark\", \"hasShownTrayHint\": true }");

        var settings = new JsonSettingsStore(_path).Load();

        Assert.AreEqual(ThemePreference.Dark, settings.Theme);
        Assert.IsTrue(settings.OverviewMode);
        Assert.AreEqual(string.Empty, settings.CustomAgents);
        Assert.AreEqual(0, settings.CustomAgentNames.Count);
        Assert.IsTrue(settings.CloseToTray);
    }
}
