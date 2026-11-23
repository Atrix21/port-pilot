using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using PortPilot.Core;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Cli;

/// <summary>
/// Full-screen live view. Redraws only on a new snapshot, a key or a resize, and writes each
/// frame as a single buffer that overwrites the previous one in place, so there is no flicker.
/// </summary>
internal sealed class Tui
{
    private const string Esc = "\e[";
    private const int ChromeLines = 7; // title, header, separator, 3 detail lines, footer

    private enum Mode { Normal, Search, Confirm }

    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly PortChangeTracker _tracker = new();
    private readonly IProcessTerminator _terminator = PlatformServices.CreateProcessTerminator();
    private readonly StringBuilder _frame = new(16 * 1024);
    private readonly Stream _stdout = Console.OpenStandardOutput();

    private PortFilter _filter = PortFilter.Default;
    private List<TrackedPort> _rows = [];
    private PortSnapshot _snapshot = PortSnapshot.Empty;
    private string? _selectedKey;
    private int _selected;
    private int _scroll;
    private Mode _mode;
    private string _search = "";
    private string? _message;
    private DateTimeOffset _messageUntil;
    private bool _quit;

    private sealed record Resize;

    private sealed record Tick;

    public async Task<int> RunAsync(bool all, bool dev)
    {
        _filter = _filter with { ListeningOnly = !all, DevPortsOnly = dev };

        await using var monitor = new PortMonitor(PlatformServices.CreateScanner(), TimeSpan.FromSeconds(2)) { IncludeAllTcpStates = all };
        monitor.SnapshotAvailable += (_, e) => _events.Writer.TryWrite(e.Snapshot);
        monitor.ScanFailed += (_, ex) => _events.Writer.TryWrite(ex);

        using var winch = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ctx =>
        {
            ctx.Cancel = true;
            _events.Writer.TryWrite(new Resize());
        });

        Console.TreatControlCAsInput = true;
        Write($"{Esc}?1049h{Esc}?25l{Esc}H{Esc}2J");
        try
        {
            var keys = new Thread(ReadKeys) { IsBackground = true, Name = "keys" };
            keys.Start();
            monitor.Start();

            using var timer = new Timer(_ => _events.Writer.TryWrite(new Tick()), null, 1000, 1000);
            var reader = _events.Reader;
            while (!_quit && await reader.WaitToReadAsync())
            {
                // Coalesce everything queued since the last frame into one redraw.
                while (reader.TryRead(out var ev))
                {
                    Handle(ev, monitor);
                }

                if (!_quit)
                {
                    Render();
                }
            }
        }
        finally
        {
            Write($"{Esc}0m{Esc}?25h{Esc}?1049l");
            Console.TreatControlCAsInput = false;
        }

        return 0;
    }

    private void ReadKeys()
    {
        while (true)
        {
            _events.Writer.TryWrite(Console.ReadKey(intercept: true));
        }
    }

    private void Handle(object ev, PortMonitor monitor)
    {
        switch (ev)
        {
            case PortSnapshot snapshot:
                _snapshot = snapshot;
                _tracker.Apply(snapshot);
                Rebuild();
                break;
            case ConsoleKeyInfo key:
                OnKey(key, monitor);
                break;
            case Exception ex:
                Flash($"Scan failed: {ex.Message}");
                break;
            case Tick:
                // Ages, "new" highlights and lingering closed rows change with time.
                Rebuild();
                break;
        }
    }

    private void Rebuild()
    {
        var filter = _mode == Mode.Search || _search.Length > 0 ? _filter with { Query = _search } : _filter;
        _rows = _tracker.Ports.Where(t => filter.Matches(t.Entry)).ToList();
        _rows.Sort((a, b) => Format.Compare(a.Entry, b.Entry));

        var index = _selectedKey is null ? -1 : _rows.FindIndex(r => r.Key == _selectedKey);
        _selected = index >= 0 ? index : Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        _selectedKey = _rows.Count > 0 ? _rows[_selected].Key : null;
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, _rows.Count - 1);
        _selectedKey = _rows[_selected].Key;
    }

    private TrackedPort? Selected => _rows.Count > 0 ? _rows[_selected] : null;

    private void OnKey(ConsoleKeyInfo key, PortMonitor monitor)
    {
        var ctrlC = key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (ctrlC)
        {
            _quit = true;
            return;
        }

        switch (_mode)
        {
            case Mode.Search:
                OnSearchKey(key);
                return;
            case Mode.Confirm:
                _mode = Mode.Normal;
                if (key.KeyChar is 'y' or 'Y' && Selected is { } target)
                {
                    _ = TerminateAsync(target.Entry, monitor);
                }
                else
                {
                    Flash("Left it alone.");
                }

                return;
        }

        var page = Math.Max(1, Console.WindowHeight - ChromeLines);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: Move(-1); return;
            case ConsoleKey.DownArrow: Move(1); return;
            case ConsoleKey.PageUp: Move(-page); return;
            case ConsoleKey.PageDown: Move(page); return;
            case ConsoleKey.Home: Move(int.MinValue / 2); return;
            case ConsoleKey.End: Move(int.MaxValue / 2); return;
            case ConsoleKey.Delete: Confirm(); return;
            case ConsoleKey.Escape:
                _search = "";
                Rebuild();
                return;
        }

        switch (key.KeyChar)
        {
            case 'k': Move(-1); break;
            case 'j': Move(1); break;
            case 'g': Move(int.MinValue / 2); break;
            case 'G': Move(int.MaxValue / 2); break;
            case 'q': _quit = true; break;
            case '/':
                _mode = Mode.Search;
                break;
            case 'x': Confirm(); break;
            case 'r':
                monitor.RefreshNow();
                Flash("Refreshing…");
                break;
            case 'a':
                monitor.IncludeAllTcpStates = !monitor.IncludeAllTcpStates;
                _filter = _filter with { ListeningOnly = !monitor.IncludeAllTcpStates };
                Flash(monitor.IncludeAllTcpStates ? "Showing every TCP connection" : "Showing listeners only");
                Rebuild();
                break;
            case 'd':
                _filter = _filter with { DevPortsOnly = !_filter.DevPortsOnly };
                Rebuild();
                break;
            case 't':
                _filter = _filter with { Protocol = _filter.Protocol switch { ProtocolFilter.All => ProtocolFilter.Tcp, ProtocolFilter.Tcp => ProtocolFilter.Udp, _ => ProtocolFilter.All } };
                Rebuild();
                break;
            case 'o':
                Open();
                break;
        }
    }

    private void OnSearchKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                _mode = Mode.Normal;
                break;
            case ConsoleKey.Escape:
                _mode = Mode.Normal;
                _search = "";
                break;
            case ConsoleKey.Backspace:
                if (_search.Length > 0)
                {
                    _search = _search[..^1];
                }

                break;
            case ConsoleKey.UpArrow: Move(-1); return;
            case ConsoleKey.DownArrow: Move(1); return;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    _search += key.KeyChar;
                }

                break;
        }

        Rebuild();
    }

    private void Confirm()
    {
        if (Selected is not { IsClosed: false } row)
        {
            return;
        }

        var access = _terminator.ProbeAccess(row.Entry.ProcessId);
        switch (access)
        {
            case TerminationAccess.Protected:
                Flash($"{Format.Process(row.Entry.Process)} is protected. PortPilot will not stop it.");
                break;
            case TerminationAccess.NotRunning:
                Flash("That process has already exited.");
                break;
            case TerminationAccess.RequiresElevation when row.Entry.ProcessId == 0 || !OperatingSystem.IsWindows():
                Flash(OperatingSystem.IsWindows() ? "Needs administrator access." : "That process belongs to another user. Run portpilot with sudo.");
                break;
            default:
                _mode = Mode.Confirm;
                break;
        }
    }

    private async Task TerminateAsync(PortEntry entry, PortMonitor monitor)
    {
        var label = $"{Format.Process(entry.Process)} ({entry.ProcessId})";
        Flash($"Stopping {label}…");
        var result = await _terminator.TerminateAsync(entry.ProcessId, entry.Process.StartTime).ConfigureAwait(false);
        Flash(result.Outcome switch
        {
            TerminationOutcome.Terminated => $"Stopped {label}.",
            TerminationOutcome.AlreadyExited => $"{label} had already exited.",
            TerminationOutcome.ProcessChanged => "The PID now belongs to a different process; nothing was stopped.",
            TerminationOutcome.AccessDenied => $"Not allowed to stop {label}.",
            TerminationOutcome.TimedOut => $"{label} is still shutting down.",
            _ => $"Could not stop {label}: {result.Detail}",
        });
        monitor.RefreshNow();
    }

    private void Open()
    {
        if (Selected is not { } row || row.Entry.Binding.Protocol != TransportProtocol.Tcp)
        {
            return;
        }

        var url = Format.Url(row.Entry);
        try
        {
            using var _ = OperatingSystem.IsWindows()
                ? Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })
                : Process.Start(new ProcessStartInfo("xdg-open", [url]) { RedirectStandardOutput = true, RedirectStandardError = true });
            Flash($"Opened {url}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Flash($"Could not open {url}: {ex.Message}");
        }
    }

    private void Flash(string message)
    {
        _message = message;
        _messageUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        _events.Writer.TryWrite(new Tick());
    }

    // ---- rendering ------------------------------------------------------------------------------

    private void Render()
    {
        var width = Math.Max(40, Console.WindowWidth);
        var height = Math.Max(ChromeLines + 1, Console.WindowHeight);
        var body = height - ChromeLines;
        var now = DateTimeOffset.UtcNow;

        if (_selected < _scroll)
        {
            _scroll = _selected;
        }
        else if (_selected >= _scroll + body)
        {
            _scroll = _selected - body + 1;
        }

        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rows.Count - body));

        var sb = _frame.Clear();
        sb.Append(Esc).Append('H');

        // Title.
        var listening = _rows.Count(r => !r.IsClosed);
        var tags = new StringBuilder();
        if (_filter.DevPortsOnly)
        {
            tags.Append("  [dev]");
        }

        if (!_filter.ListeningOnly)
        {
            tags.Append("  [all states]");
        }

        if (_filter.Protocol != ProtocolFilter.All)
        {
            tags.Append("  [").Append(_filter.Protocol.ToString().ToLowerInvariant()).Append(']');
        }

        var title = $" PortPilot  ·  {listening} endpoints  ·  scan {_snapshot.Duration.TotalMilliseconds:0.0} ms{tags}";
        sb.Append(Esc).Append("7m");
        Format.Cell(sb, title, width);
        Eol(sb);

        // Column header.
        sb.Append(Esc).Append("2m");
        Format.Cell(sb, "  PORT  PROTO ADDRESS         PID     PROCESS           SERVICE        COMMAND", width);
        Eol(sb);

        for (var i = 0; i < body; i++)
        {
            var index = _scroll + i;
            if (index < _rows.Count)
            {
                Row(sb, _rows[index], index == _selected, width, now);
            }

            Eol(sb);
        }

        // Details of the selected row.
        sb.Append(Esc).Append("2m").Append('─', width);
        Eol(sb);
        Details(sb, Selected?.Entry, width);

        // Footer.
        if (_mode == Mode.Search)
        {
            sb.Append(" / ").Append(_search).Append(Esc).Append("7m ").Append(Esc).Append("0m");
            sb.Append(Esc).Append("2m   Enter keep · Esc clear").Append(Esc).Append("0m");
        }
        else if (_mode == Mode.Confirm && Selected is { } target)
        {
            sb.Append(Esc).Append("1;33m");
            Format.Cell(sb, $" Stop {Format.Process(target.Entry.Process)} (PID {target.Entry.ProcessId})? y / N", width);
        }
        else if (_message is not null && now < _messageUntil)
        {
            Format.Cell(sb, " " + _message, width);
        }
        else
        {
            sb.Append(Esc).Append("2m");
            var hint = _search.Length > 0 ? $" filter \"{_search}\" · Esc clear · " : " ";
            Format.Cell(sb, hint + "↑↓ move · / search · x stop · o open · a all · d dev · t proto · q quit", width);
        }

        sb.Append(Esc).Append("0m").Append(Esc).Append('K').Append(Esc).Append('J');
        Write(sb);
    }

    private static void Row(StringBuilder sb, TrackedPort row, bool selected, int width, DateTimeOffset now)
    {
        var e = row.Entry;
        if (selected)
        {
            sb.Append(Esc).Append("7m");
        }

        if (row.IsClosed)
        {
            sb.Append(Esc).Append("2;9m");
        }

        sb.Append(row.IsClosed ? "  " : row.IsNew(now, PortChangeTracker.DefaultNewWindow) ? $"{Esc}32m+ {Esc}39m" : "  ");
        Format.Cell(sb, e.Port.ToString(CultureInfo.InvariantCulture), 6);
        Format.Cell(sb, Format.Protocol(e.Binding), 6);
        Format.Cell(sb, Format.Address(e.Binding.LocalAddress), 16);
        Format.Cell(sb, Format.Pid(e), 8);
        Format.Cell(sb, Format.Process(e.Process), 18);
        if (e.Known is { } known && !row.IsClosed)
        {
            sb.Append(Esc).Append("33m");
            Format.Cell(sb, known.Name, 15);
            sb.Append(Esc).Append("39m");
        }
        else
        {
            Format.Cell(sb, e.Known?.Name, 15);
        }

        Format.Cell(sb, e.Process.CommandLine ?? e.Process.ExecutablePath, width - 71, padRight: selected);
        sb.Append(Esc).Append("0m");
    }

    private static void Details(StringBuilder sb, PortEntry? e, int width)
    {
        if (e is null)
        {
            Eol(sb);
            Eol(sb);
            Eol(sb);
            return;
        }

        var p = e.Process;
        var started = p.StartTime is { } s ? $" · started {s.ToLocalTime():HH:mm:ss} ({Format.Age(s)} ago)" : "";
        var parent = p.ParentProcessId is { } pp ? $" · parent {pp}" : "";
        sb.Append(Esc).Append('1').Append('m');
        Format.Cell(sb, $" {Format.Process(p)} · PID {Format.Pid(e)}{parent}{started} · {Format.Url(e)}", width);
        sb.Append(Esc).Append("0m");
        Eol(sb);

        var restricted = p.Access == ProcessAccessLevel.Restricted
            ? OperatingSystem.IsWindows() ? "Details need administrator access." : "Details need root; run portpilot with sudo."
            : null;
        Format.Cell(sb, " exe  " + (p.ExecutablePath ?? restricted ?? "—"), width);
        Eol(sb);
        Format.Cell(sb, " cmd  " + (p.CommandLine ?? "—"), width);
        Eol(sb);
    }

    private static void Eol(StringBuilder sb) => sb.Append(Esc).Append("0m").Append(Esc).Append("K\r\n");

    private void Write(StringBuilder sb) => Write(sb.ToString());

    private void Write(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        _stdout.Write(bytes);
        _stdout.Flush();
    }
}
