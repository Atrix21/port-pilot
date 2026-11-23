using System.Collections;
using PortPilot.Core.Settings;

namespace PortPilot.App.ViewModels;

/// <summary>
/// Orders rows by the chosen column with stable tie-breakers so rows never shuffle between refreshes.
/// </summary>
public sealed class RowComparer(SortColumn column, bool descending) : IComparer, IComparer<PortRowViewModel>
{
    public SortColumn Column { get; } = column;

    public bool Descending { get; } = descending;

    public int Compare(object? x, object? y) => Compare(x as PortRowViewModel, y as PortRowViewModel);

    public int Compare(PortRowViewModel? x, PortRowViewModel? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var primary = Column switch
        {
            SortColumn.Port => x.Port.CompareTo(y.Port),
            SortColumn.Process => string.Compare(x.ProcessName, y.ProcessName, StringComparison.OrdinalIgnoreCase),
            SortColumn.Pid => x.Pid.CompareTo(y.Pid),
            SortColumn.Protocol => string.CompareOrdinal(x.ProtocolText, y.ProtocolText),
            SortColumn.Address => string.CompareOrdinal(x.AddressText, y.AddressText),
            SortColumn.Status => x.State.CompareTo(y.State),
            _ => 0,
        };

        if (Descending)
        {
            primary = -primary;
        }

        if (primary != 0)
        {
            return primary;
        }

        // Tie-breakers are always ascending.
        var result = x.Port.CompareTo(y.Port);
        if (result == 0)
        {
            result = string.CompareOrdinal(x.ProtocolText, y.ProtocolText);
        }

        if (result == 0)
        {
            result = x.IsIPv6.CompareTo(y.IsIPv6);
        }

        if (result == 0)
        {
            result = string.CompareOrdinal(x.AddressText, y.AddressText);
        }

        if (result == 0)
        {
            result = x.Pid.CompareTo(y.Pid);
        }

        return result == 0 ? string.CompareOrdinal(x.Key, y.Key) : result;
    }
}
