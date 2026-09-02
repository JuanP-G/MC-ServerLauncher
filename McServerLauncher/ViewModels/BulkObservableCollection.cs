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

    /// <summary>
    /// Removes the first <paramref name="count"/> items raising one <b>Remove</b>, not a Reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Reset tells a bound <c>ListBox</c> "everything you knew is gone", and it responds by
    /// throwing away every realised container and building new ones — including for the lines that
    /// did not change. That is the flicker, and it is what makes anything animated per item replay
    /// on rows nobody touched. In the console it happened every couple of hundred lines, on its own,
    /// while somebody was reading.
    /// </para>
    /// <para>
    /// A Remove says what actually happened, so the survivors keep their containers. Measured in
    /// <c>ConsoleTrimTests</c>, which asks the control for the container of a given line before and
    /// after: same object with Remove, a different one with Reset.
    /// </para>
    /// <para>
    /// The selection is <b>not</b> the difference, and it is worth writing down because it was the
    /// first thing assumed: Avalonia re-resolves the selected item by value, so it survives a Reset
    /// too. The cost is the rebuilding, not the selection.
    /// </para>
    /// </remarks>
    public void RemoveFromStartKeepingSelection(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, Items.Count);

        var removed = new List<T>(count);
        for (var i = 0; i < count; i++) removed.Add(Items[i]);

        if (Items is List<T> list) list.RemoveRange(0, count);
        else for (var i = 0; i < count; i++) Items.RemoveAt(0);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, 0));
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
