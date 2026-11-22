using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Model;

namespace PortPilot.Core.Discovery;

public sealed class SnapshotEventArgs(PortSnapshot snapshot, bool includesAllTcpStates) : EventArgs
{
    public PortSnapshot Snapshot { get; } = snapshot;

    public bool IncludesAllTcpStates { get; } = includesAllTcpStates;
}

/// <summary>
/// Runs scans on a background loop. Scans never overlap, and a manual refresh restarts the interval
/// so the UI never receives two snapshots in quick succession.
/// </summary>
public sealed class PortMonitor : IAsyncDisposable
{
    private readonly IPortScanner _scanner;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;
    private volatile bool _paused;
    private volatile bool _includeAllTcpStates;
    private TimeSpan _interval;

    public PortMonitor(IPortScanner scanner, TimeSpan interval, ILogger<PortMonitor>? logger = null)
    {
        _scanner = scanner;
        _interval = ClampInterval(interval);
        _log = logger ?? NullLogger<PortMonitor>.Instance;
    }

    /// <summary>Raised on a thread-pool thread after every scan.</summary>
    public event EventHandler<SnapshotEventArgs>? SnapshotAvailable;

    /// <summary>Raised on a thread-pool thread when a scan throws.</summary>
    public event EventHandler<Exception>? ScanFailed;

    public static TimeSpan MinimumInterval { get; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = ClampInterval(value);
            Wake();
        }
    }

    public bool IsPaused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value)
            {
                Wake();
            }
        }
    }

    public bool IncludeAllTcpStates
    {
        get => _includeAllTcpStates;
        set
        {
            if (_includeAllTcpStates != value)
            {
                _includeAllTcpStates = value;
                Wake();
            }
        }
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start() => _loop ??= Task.Run(() => RunAsync(_shutdown.Token));

    /// <summary>Requests an immediate scan (also while paused).</summary>
    public void RefreshNow() => Wake();

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var forced = true;
        while (!token.IsCancellationRequested)
        {
            if (!_paused || forced)
            {
                ScanOnce();
            }

            try
            {
                // Sleep for the interval or until someone asks for an immediate scan.
                forced = await _wake.WaitAsync(_paused ? Timeout.InfiniteTimeSpan : _interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ScanOnce()
    {
        var includeAll = _includeAllTcpStates;
        try
        {
            var snapshot = _scanner.Scan(includeAll);
            SnapshotAvailable?.Invoke(this, new SnapshotEventArgs(snapshot, includeAll));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Port scan failed");
            ScanFailed?.Invoke(this, ex);
        }
    }

    private static TimeSpan ClampInterval(TimeSpan interval) =>
        interval < MinimumInterval ? MinimumInterval : interval;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        _wake.Dispose();
    }
}
