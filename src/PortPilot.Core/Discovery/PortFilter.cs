using System.Globalization;
using PortPilot.Core.Model;

namespace PortPilot.Core.Discovery;

public enum ProtocolFilter
{
    All,
    Tcp,
    Udp,
}

/// <summary>
/// Search and filter criteria for the port list.
/// </summary>
/// <remarks>
/// The query matches process names, PIDs, ports and addresses. A leading colon (<c>:5173</c>)
/// restricts matching to port numbers; <c>pid:1234</c> restricts matching to process IDs; <c>project:atlas</c>
/// restricts matching to the git project (name or branch) a process is working in.
/// </remarks>
public sealed record PortFilter(string Query = "", ProtocolFilter Protocol = ProtocolFilter.All, bool ListeningOnly = true, bool DevPortsOnly = false, bool OrphansOnly = false)
{
    public static PortFilter Default { get; } = new();

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    public bool Matches(PortEntry entry)
    {
        var binding = entry.Binding;

        if (Protocol == ProtocolFilter.Tcp && binding.Protocol != TransportProtocol.Tcp)
        {
            return false;
        }

        if (Protocol == ProtocolFilter.Udp && binding.Protocol != TransportProtocol.Udp)
        {
            return false;
        }

        if (ListeningOnly && !binding.IsListening)
        {
            return false;
        }

        if (DevPortsOnly && entry.Known is null)
        {
            return false;
        }

        if (OrphansOnly && !entry.Process.IsOrphaned)
        {
            return false;
        }

        return !HasQuery || MatchesQuery(entry, Query.Trim());
    }

    private static bool MatchesQuery(PortEntry entry, string query)
    {
        if (query.StartsWith(':'))
        {
            return MatchesNumber(entry.Port, query[1..]);
        }

        if (query.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesNumber(entry.ProcessId, query[4..].Trim());
        }

        if (query.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
        {
            var project = query[8..].Trim();
            return entry.Process.Project is { } p && (project.Length == 0 || Contains(p.Name, project) || Contains(p.Branch, project));
        }

        if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            // Numbers match ports by prefix and PIDs exactly, so "30" finds 3000 and 3001.
            return entry.Port.ToString(CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal)
                || entry.ProcessId.ToString(CultureInfo.InvariantCulture) == query;
        }

        return Contains(entry.Process.ImageName, query)
            || Contains(entry.Known?.Name, query)
            || Contains(entry.Binding.LocalAddress.ToString(), query)
            || Contains(entry.Process.ExecutablePath, query)
            || Contains(entry.Binding.Protocol.ToString(), query)
            || Contains(entry.Process.Project?.Name, query)
            || Contains(entry.Process.Project?.Branch, query);
    }

    private static bool MatchesNumber(int value, string query) =>
        query.Length == 0 || value.ToString(CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal);

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
