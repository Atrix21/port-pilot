using System.Globalization;
using PortPilot.App.Mvvm;

namespace PortPilot.App.ViewModels;

/// <summary>
/// One project (a git repository and its worktrees) and what it is running right now: ports, branches,
/// and whether an agent session started any of it, or left any of it behind.
/// </summary>
public sealed class ProjectGroupViewModel(string name) : ObservableObject
{
    private string _branches = "";
    private int _portCount;
    private int _processCount;
    private int _agentCount;
    private int _orphanCount;
    private bool _isActive;
    private string _portsText = "";

    public string Name { get; } = name;

    public string Branches
    {
        get => _branches;
        private set => Set(ref _branches, value);
    }

    public bool HasBranches => _branches.Length > 0;

    public int PortCount
    {
        get => _portCount;
        private set => Set(ref _portCount, value);
    }

    public int ProcessCount
    {
        get => _processCount;
        private set => Set(ref _processCount, value);
    }

    /// <summary>Processes started by a running agent session.</summary>
    public int AgentCount
    {
        get => _agentCount;
        private set
        {
            if (Set(ref _agentCount, value))
            {
                OnPropertyChanged(nameof(HasAgent));
            }
        }
    }

    public bool HasAgent => _agentCount > 0;

    private string _agentName = "Agent";

    /// <summary>The short name of the agent working here, e.g. "Claude", or "2 agents".</summary>
    public string AgentName
    {
        get => _agentName;
        private set => Set(ref _agentName, value);
    }

    /// <summary>Processes still running after the agent session that started them ended.</summary>
    public int OrphanCount
    {
        get => _orphanCount;
        private set
        {
            if (Set(ref _orphanCount, value))
            {
                OnPropertiesChanged(nameof(HasOrphans), nameof(OrphanText));
            }
        }
    }

    public bool HasOrphans => _orphanCount > 0;

    public string OrphanText => $"{_orphanCount.ToString(CultureInfo.InvariantCulture)} left behind";

    /// <summary>":5173 :24678" – the ports, compactly.</summary>
    public string PortsText
    {
        get => _portsText;
        private set => Set(ref _portsText, value);
    }

    /// <summary>True while the table is filtered to this project.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public string Summary => $"{PortCount.ToString(CultureInfo.InvariantCulture)} {(PortCount == 1 ? "port" : "ports")} · {ProcessCount.ToString(CultureInfo.InvariantCulture)} {(ProcessCount == 1 ? "process" : "processes")}";

    internal void Update(IReadOnlyList<PortRowViewModel> rows)
    {
        Branches = string.Join(", ", rows.Select(r => r.ProjectBranch).Where(b => b is not null).Distinct().OrderBy(b => b, StringComparer.Ordinal));
        OnPropertyChanged(nameof(HasBranches));
        PortCount = rows.Select(r => (r.Port, r.ProtocolText)).Distinct().Count();
        ProcessCount = rows.Select(r => r.Pid).Distinct().Count();
        AgentCount = rows.Where(r => r.Agent is { IsRunning: true }).Select(r => r.Pid).Distinct().Count();
        var agents = rows.Where(r => r.Agent is { IsRunning: true }).Select(r => r.AgentShortName).Distinct().ToList();
        AgentName = agents.Count == 1 ? agents[0]! : $"{agents.Count} agents";
        OrphanCount = rows.Where(r => r.IsOrphaned).Select(r => r.Pid).Distinct().Count();
        PortsText = string.Join(" ", rows.Select(r => r.Port).Distinct().Order().Take(5).Select(p => ":" + p.ToString(CultureInfo.InvariantCulture)))
            + (rows.Select(r => r.Port).Distinct().Count() > 5 ? " …" : "");
        OnPropertyChanged(nameof(Summary));
    }
}
