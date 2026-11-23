using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace PortPilot.App.ViewModels;

/// <summary>
/// A filtered, sorted live view over the row collection, usable as an <c>ItemsSource</c> by any XAML
/// framework. Like WPF's <c>ListCollectionView</c> without live shaping: it tracks additions and removals
/// in the source, and re-filters or re-sorts only when asked. Changes are applied as minimal inserts,
/// moves and removals so item containers (and their animations) survive.
/// </summary>
public sealed class RowsView : ObservableCollection<PortRowViewModel>
{
    private readonly ObservableCollection<PortRowViewModel> _source;
    private readonly Func<PortRowViewModel, bool> _filter;
    private RowComparer _comparer;

    public RowsView(ObservableCollection<PortRowViewModel> source, Func<PortRowViewModel, bool> filter, RowComparer comparer)
    {
        _source = source;
        _filter = filter;
        _comparer = comparer;
        _source.CollectionChanged += OnSourceChanged;
        Refresh();
    }

    public RowComparer Comparer
    {
        get => _comparer;
        set
        {
            _comparer = value;
            Refresh();
        }
    }

    public bool IsEmpty => Count == 0;

    /// <summary>Re-applies the filter and sort order to every row.</summary>
    public void Refresh()
    {
        var desired = _source.Where(_filter).ToList();
        desired.Sort(_comparer);
        var wanted = desired.ToHashSet();

        for (var i = Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(this[i]))
            {
                RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            if (i < Count && ReferenceEquals(this[i], row))
            {
                continue;
            }

            var existing = IndexOf(row, i + 1);
            if (existing >= 0)
            {
                Move(existing, i);
            }
            else
            {
                Insert(i, row);
            }
        }
    }

    private int IndexOf(PortRowViewModel row, int start)
    {
        for (var i = start; i < Count; i++)
        {
            if (ReferenceEquals(this[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (PortRowViewModel row in e.NewItems!)
                {
                    InsertSorted(row);
                }

                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (PortRowViewModel row in e.OldItems!)
                {
                    Remove(row);
                }

                break;
            default:
                Refresh();
                break;
        }
    }

    private void InsertSorted(PortRowViewModel row)
    {
        if (!_filter(row))
        {
            return;
        }

        // Binary search for the insertion point.
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_comparer.Compare(this[mid], row) <= 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        Insert(lo, row);
    }
}
