using System.Globalization;
using System.Net;
using System.Net.Sockets;
using PortPilot.App.Mvvm;
using PortPilot.App.Services;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.App.ViewModels;

public enum RowStatus
{
    Listening,
    Bound,
    Connected,
    Transitional,
    New,
    Orphaned,
    Detached,
    Closed,
}

/// <summary>
/// One endpoint in the port table. Instances are long-lived and updated in place so the list never
/// rebuilds its containers on refresh.
/// </summary>
public sealed class PortRowViewModel : ObservableObject
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private PortEntry _entry;
    private bool _isNew;
    private bool _isClosed;
    private bool _isRemoving;

    public PortRowViewModel(PortEntry entry, bool isNew)
    {
        _entry = entry;
        _isNew = isNew;
        Key = entry.Key;
    }

    public string Key { get; }

    public PortEntry Entry => _entry;

    // ── Port ──
    public int Port => _entry.Port;

    public string PortText => Port.ToString(CultureInfo.InvariantCulture);

    public KnownPort? Known => _entry.Known;

    public string? KnownName => _entry.Known?.Name;

    public bool IsDevPort => _entry.Known is not null;

    // ── Process ──
    public int Pid => _entry.ProcessId;

    /// <summary>On Linux PID 0 means the owner couldn't be determined, not a real process.</summary>
    public string PidText => Pid == 0 && !OperatingSystem.IsWindows() ? "—" : Pid.ToString(CultureInfo.InvariantCulture);

    public string ProcessName => _entry.Process.DisplayName;

    public string ImageName => _entry.Process.ImageName;

    public string? ExecutablePath => _entry.Process.ExecutablePath;

    public string? CommandLine => _entry.Process.CommandLine;

    public DateTimeOffset? StartTime => _entry.Process.StartTime;

    public ProcessAccessLevel Access => _entry.Process.Access;

    public bool IsRestricted => Access == ProcessAccessLevel.Restricted;

    public bool IsSystem => Access == ProcessAccessLevel.System;

    public bool HasExecutablePath => !string.IsNullOrEmpty(ExecutablePath);

    /// <summary>A short line under the process name: its arguments, location, or why we can't see them.</summary>
    public string ProcessDetail
    {
        get
        {
            switch (Access)
            {
                case ProcessAccessLevel.System:
                    return !OperatingSystem.IsWindows() ? "Kernel thread" : Pid == 4 ? "Windows kernel · HTTP.sys and drivers" : "Windows kernel";
                case ProcessAccessLevel.Restricted:
                    return Pid == 0 && !OperatingSystem.IsWindows() ? "Owned by another user" : $"Details need {PlatformText.Admin} access";
                case ProcessAccessLevel.Exited:
                    return "Process has exited";
            }

            var arguments = ExtractArguments(CommandLine);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                return arguments.Length > 160 ? arguments[..160] + "…" : arguments;
            }

            return ShortenPath(ExecutablePath) ?? ImageName;
        }
    }

    // ── Project & agent ──
    public ProjectInfo? Project => _entry.Process.Project;

    public bool HasProject => Project is not null;

    public string? ProjectName => Project?.Name;

    public string? ProjectBranch => Project?.Branch;

    /// <summary><c>atlas-web · feature/auth</c>.</summary>
    public string? ProjectText => Project?.DisplayText;

    public string? ProjectRoot => Project?.Root;

    public bool IsWorktree => Project?.IsWorktree == true;

    public string? WorkingDirectory => _entry.Process.WorkingDirectory;

    public AgentSession? Agent => _entry.Process.Agent;

    public bool HasAgent => Agent is not null;

    /// <summary>"Claude", "Codex", "Gemini", …</summary>
    public string? AgentShortName => Agent is { } a ? AgentDetector.ShortNameFor(a.Name) : null;

    /// <summary>The row tag: "Claude", or "Claude · ended" once its session has gone.</summary>
    public string? AgentTagText => AgentShortName is { } name ? (Agent!.IsRunning ? name : $"{name} · ended") : null;

    /// <summary>
    /// No agent history, but it outlived whatever started it and is running inside a project: very often a
    /// dev server someone forgot. Shown, never stopped automatically.
    /// </summary>
    public bool IsPossiblyLeftBehind => !HasAgent && _entry.Process.IsDetached && HasProject && !IsClosed;

    public string? ProjectTooltip => Project is { } p ? $"{p.DisplayText}{(p.IsWorktree ? " (worktree)" : "")}\n{p.Root}" : null;

    /// <summary>Still running after the agent session that started it has ended.</summary>
    public bool IsOrphaned => _entry.Process.IsOrphaned && !IsClosed;

    public string? AgentText => Agent switch
    {
        null => null,
        { IsRunning: true } a => $"Started by {a.Name} (PID {a.ProcessId.ToString(CultureInfo.InvariantCulture)})",
        { } a => $"Left behind by a {a.Name} session that has ended",
    } ?? (IsPossiblyLeftBehind ? "Its parent process has exited, so it may have been left behind. PortPilot has no record of what started it." : null);

    // ── Endpoint ──
    public bool IsTcp => _entry.Binding.Protocol == TransportProtocol.Tcp;

    public string ProtocolText => IsTcp ? "TCP" : "UDP";

    public bool IsIPv6 => _entry.Binding.IsIPv6;

    public string FamilyText => IsIPv6 ? "IPv6" : "IPv4";

    public string AddressText => FormatAddress(_entry.Binding.LocalAddress);

    public string AddressScope
    {
        get
        {
            var binding = _entry.Binding;
            if (binding.RemoteAddress is { } remote)
            {
                return $"→ {FormatAddress(remote)}:{binding.RemotePort.ToString(CultureInfo.InvariantCulture)}";
            }

            var address = binding.LocalAddress;
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return "All interfaces";
            }

            if (IPAddress.IsLoopback(address))
            {
                return "Loopback only";
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
            {
                return "Link-local";
            }

            return "Network interface";
        }
    }

    public string EndpointText => IsIPv6 ? $"[{AddressText}]:{PortText}" : $"{AddressText}:{PortText}";

    public ConnectionState State => _entry.Binding.State;

    public bool IsListening => _entry.Binding.IsListening;

    public RowStatus Status => IsClosed ? RowStatus.Closed
        : IsNew ? RowStatus.New
        : IsOrphaned ? RowStatus.Orphaned
        : IsPossiblyLeftBehind ? RowStatus.Detached
        : State switch
        {
            ConnectionState.Listening => RowStatus.Listening,
            ConnectionState.Bound => RowStatus.Bound,
            ConnectionState.Established => RowStatus.Connected,
            _ => RowStatus.Transitional,
        };

    public string StatusText => Status switch
    {
        RowStatus.Closed => "Closed",
        RowStatus.New => "New",
        RowStatus.Orphaned => "Left behind",
        RowStatus.Detached => "Detached",
        _ => StateText,
    };

    public string StateText => IsClosed ? "Closed" : State switch
    {
        ConnectionState.Listening => "Listening",
        ConnectionState.Bound => "Bound",
        ConnectionState.Established => "Connected",
        ConnectionState.SynSent => "Connecting",
        ConnectionState.SynReceived => "Accepting",
        ConnectionState.FinWait1 or ConnectionState.FinWait2 => "Finishing",
        ConnectionState.CloseWait => "Close wait",
        ConnectionState.Closing => "Closing",
        ConnectionState.LastAck => "Last ack",
        ConnectionState.TimeWait => "Time wait",
        ConnectionState.Closed => "Closed",
        _ => "Unknown",
    };

    // ── Lifecycle ──
    public bool IsNew
    {
        get => _isNew;
        set
        {
            if (Set(ref _isNew, value))
            {
                OnPropertiesChanged(nameof(Status), nameof(StatusText));
            }
        }
    }

    public bool IsClosed
    {
        get => _isClosed;
        set
        {
            if (Set(ref _isClosed, value))
            {
                OnPropertiesChanged(nameof(Status), nameof(StatusText), nameof(StateText), nameof(CanOpenInBrowser), nameof(IsOrphaned), nameof(IsPossiblyLeftBehind));
            }
        }
    }

    public bool IsRemoving
    {
        get => _isRemoving;
        set => Set(ref _isRemoving, value);
    }

    // ── Actions ──
    /// <summary>Listening TCP endpoints, except well-known databases and brokers that do not speak HTTP.</summary>
    public bool CanOpenInBrowser => IsTcp && State == ConnectionState.Listening && !IsClosed && _entry.Known?.Kind != KnownPortKind.DataStore;

    public string ProtocolFamilyText => $"{ProtocolText} · {FamilyText}";

    public string Url => $"{(_entry.Known?.PrefersHttps == true ? "https" : "http")}://localhost:{PortText}/";

    public string UrlDisplay => $"localhost:{PortText}";

    public string ClipboardSummary
    {
        get
        {
            var lines = new List<string>
            {
                $"{ProtocolText} {EndpointText} · {StateText}",
                $"{ImageName} (PID {PidText})",
            };
            if (ExecutablePath is not null)
            {
                lines.Add(ExecutablePath);
            }

            if (CommandLine is not null)
            {
                lines.Add(CommandLine);
            }

            if (Project is { } project)
            {
                lines.Add($"Project: {project.DisplayText} ({project.Root})");
            }

            if (AgentText is { } agent)
            {
                lines.Add(agent);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Replaces the underlying data, raising change notifications only for what moved.</summary>
    public bool Update(PortEntry entry)
    {
        if (Equals(_entry, entry))
        {
            return false;
        }

        _entry = entry;
        OnPropertyChanged(string.Empty);
        return true;
    }

    private static string FormatAddress(IPAddress address) => address.ToString();

    internal static string? ExtractArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var span = commandLine.AsSpan().TrimStart();
        if (span.Length > 0 && span[0] == '"')
        {
            var close = span[1..].IndexOf('"');
            span = close < 0 ? [] : span[(close + 2)..];
        }
        else
        {
            var space = span.IndexOf(' ');
            span = space < 0 ? [] : span[space..];
        }

        return span.Trim().ToString();
    }

    internal static string? ShortenPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return path.StartsWith(UserProfile, StringComparison.OrdinalIgnoreCase) ? "~" + path[UserProfile.Length..] : path;
    }
}
