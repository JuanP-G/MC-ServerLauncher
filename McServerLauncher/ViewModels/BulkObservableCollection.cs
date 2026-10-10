using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace McServerLauncher.ViewModels;

/// <summary>
/// ObservableCollection with bulk operations that raise a single Reset event instead of one
/// CollectionChanged per element. Used by the console lists (EFI-4): a verbose server (e.g. Forge
/// booting) used to pay one RemoveAt(0) — an O(n) array shift plus a UI notification — for every
/// line beyond the cap; trimming in blocks makes that a single cheap Reset every couple hundred
/// lines, which the virtualized ListBox absorbs by re-rendering only the visible viewport.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Removes the first <paramref name="count"/> items, raising one notification.</summary>
    /// <remarks>
    /// The notification says what actually happened — these items, from the front — rather than
    /// "everything changed". A Reset tells a virtualized list nothing about what it still has, so
    /// it throws away every container it had realized and builds them all again; saying which items
    /// left lets it keep the ones the user is looking at. On the console that is the difference
    /// between a visible hitch every couple of hundred lines and nothing at all.
    /// </remarks>
    public void RemoveFromStart(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, Items.Count);

        var removed = new List<T>(count);
        for (var i = 0; i < count; i++) removed.Add(Items[i]);

        if (Items is List<T> list)
            list.RemoveRange(0, count); // single memmove instead of count shifts
        else
            for (var i = 0; i < count; i++) Items.RemoveAt(0);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, 0));
    }

    /// <summary>Appends <paramref name="items"/>, raising one Add for all of them.</summary>
    /// <remarks>
    /// An Add, not a Reset, for the same reason as <see cref="RemoveFromStart"/>: the list keeps the
    /// containers it has and only builds the new ones. Used by the console, which takes the lines a
    /// server printed in one go instead of one notification per line.
    /// </remarks>
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        var start = Items.Count;
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, new List<T>(items), start));
    }

    /// <summary>Replaces the whole content with <paramref name="items"/>, raising one Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
