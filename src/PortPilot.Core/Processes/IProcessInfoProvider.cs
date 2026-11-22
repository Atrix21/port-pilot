using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Resolves process identifiers into <see cref="ProcessDetails"/>.
/// </summary>
public interface IProcessInfoProvider
{
    /// <summary>
    /// Resolves the given set of PIDs. Implementations may cache aggressively, but must detect
    /// PID reuse and drop entries for processes that are no longer referenced.
    /// </summary>
    IReadOnlyDictionary<int, ProcessDetails> Resolve(IReadOnlyCollection<int> processIds);
}
