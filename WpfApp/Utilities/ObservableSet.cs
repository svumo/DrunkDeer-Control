using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WpfApp.Utilities;

// A dedup-by-default set that raises INotifyCollectionChanged when mutated and
// INotifyPropertyChanged for "Count". Surface is intentionally narrow — just
// what the keyboard multi-select model needs (see KeyboardCanvasViewModel).
//
// ReplaceAll is the important bit: marquee-drag mutates the selection on every
// mouse-move tick. A naive "Clear() then UnionWith()" pattern would fire two
// events per tick (Reset + Add) which is enough to make WPF observers thrash.
// ReplaceAll computes the symmetric diff and fires exactly one Reset event
// when something actually changed.
public sealed class ObservableSet<T> : INotifyCollectionChanged, INotifyPropertyChanged, IEnumerable<T>
{
    private readonly HashSet<T> items;

    public ObservableSet()
    {
        items = new HashSet<T>();
    }

    public ObservableSet(IEqualityComparer<T> comparer)
    {
        items = new HashSet<T>(comparer);
    }

    public int Count => items.Count;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Contains(T item) => items.Contains(item);

    public bool Add(T item)
    {
        if (!items.Add(item)) return false;
        RaiseAdd(item);
        RaiseCountChanged();
        return true;
    }

    public bool Remove(T item)
    {
        if (!items.Remove(item)) return false;
        RaiseRemove(item);
        RaiseCountChanged();
        return true;
    }

    // Adds if absent, removes if present. Returns the new membership state.
    public bool Toggle(T item)
    {
        if (items.Contains(item))
        {
            Remove(item);
            return false;
        }
        Add(item);
        return true;
    }

    public void Clear()
    {
        if (items.Count == 0) return;
        items.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        RaiseCountChanged();
    }

    // Single Reset event covering the whole replacement. We deliberately use
    // Reset (not Add/Remove) so subscribers don't have to consume two events
    // and so we don't have to materialize potentially-large diff lists.
    public void ReplaceAll(IEnumerable<T> newItems)
    {
        var incoming = newItems as ICollection<T> ?? newItems.ToArray();
        var toAdd = new List<T>();
        foreach (var x in incoming)
        {
            if (!items.Contains(x)) toAdd.Add(x);
        }
        var incomingSet = new HashSet<T>(incoming, items.Comparer);
        var toRemove = new List<T>();
        foreach (var x in items)
        {
            if (!incomingSet.Contains(x)) toRemove.Add(x);
        }

        if (toAdd.Count == 0 && toRemove.Count == 0) return;

        var hadCount = items.Count;
        foreach (var x in toRemove) items.Remove(x);
        foreach (var x in toAdd) items.Add(x);

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        if (items.Count != hadCount) RaiseCountChanged();
    }

    public void UnionWith(IEnumerable<T> other)
    {
        var added = new List<T>();
        foreach (var x in other)
        {
            if (items.Add(x)) added.Add(x);
        }
        if (added.Count == 0) return;
        // Use Reset rather than emitting N Add events — keeps marquee cheap.
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        RaiseCountChanged();
    }

    public void ExceptWith(IEnumerable<T> other)
    {
        var removed = new List<T>();
        foreach (var x in other)
        {
            if (items.Remove(x)) removed.Add(x);
        }
        if (removed.Count == 0) return;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        RaiseCountChanged();
    }

    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

    private void RaiseAdd(T item) =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item));

    private void RaiseRemove(T item) =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item));

    private void RaiseCountChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
}
