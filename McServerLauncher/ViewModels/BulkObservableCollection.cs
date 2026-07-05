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
    /// <summary>Removes the first <paramref name="count"/> items, raising one Reset.</summary>
    public void RemoveFromStart(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, Items.Count);

        if (Items is List<T> list)
            list.RemoveRange(0, count); // single memmove instead of count shifts
        else
            for (var i = 0; i < count; i++) Items.RemoveAt(0);

        RaiseReset();
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
