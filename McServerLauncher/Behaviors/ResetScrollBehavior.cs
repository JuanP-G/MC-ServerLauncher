using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Attached property that sends a list back to the top when its contents are replaced, and leaves
/// it alone when items are merely appended.
/// <para>
/// The distinction is the point. Changing a filter or the search produces a *different* set of
/// results, where the position the user was at means nothing — the twentieth optimisation mod has
/// no relation to the twentieth magic mod — so the list starts at the top. "Load more" produces
/// the *same* set with more at the end, so the position must survive; anything else would throw
/// the user back to the start of a list they were halfway through reading.
/// </para>
/// <para>
/// The view model already draws that line: it swaps the collection in one go (a Reset) for a new
/// set and appends (an Add) for more of the same. This just follows it, which is why no extra
/// state is needed on either side.
/// </para>
/// </summary>
public static class ResetScrollBehavior
{
    public static readonly AttachedProperty<bool> ResetOnResetProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("ResetOnReset", typeof(ResetScrollBehavior));

    public static bool GetResetOnReset(ListBox o) => o.GetValue(ResetOnResetProperty);
    public static void SetResetOnReset(ListBox o, bool value) => o.SetValue(ResetOnResetProperty, value);

    static ResetScrollBehavior()
    {
        ResetOnResetProperty.Changed.AddClassHandler<ListBox>((lb, e) => OnChanged(lb, e));
    }

    private static void OnChanged(ListBox listBox, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        // Re-hook whenever the bound collection changes (e.g. switching server).
        listBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ItemsControl.ItemsSourceProperty)
                Hook(listBox, args.OldValue as INotifyCollectionChanged, args.NewValue as INotifyCollectionChanged);
        };
        Hook(listBox, null, listBox.ItemsSource as INotifyCollectionChanged);
    }

    // Maps a watched collection to its ListBox so the static handler can find it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<INotifyCollectionChanged, ListBox> ListBoxes = new();

    private static void Hook(ListBox listBox, INotifyCollectionChanged? old, INotifyCollectionChanged? @new)
    {
        if (old is not null) old.CollectionChanged -= OnCollectionChanged;
        if (@new is null) return;
        @new.CollectionChanged += OnCollectionChanged;
        ListBoxes.AddOrUpdate(@new, listBox);
    }

    private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset) return;
        if (sender is not INotifyCollectionChanged c || !ListBoxes.TryGetValue(c, out var listBox)) return;

        // After the layout pass that follows the swap: scrolling before the new items are measured
        // would be undone by the re-measure.
        Dispatcher.UIThread.Post(() => ScrollToTop(listBox), DispatcherPriority.Loaded);
    }

    private static void ScrollToTop(ListBox listBox)
    {
        var scroller = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is not null) scroller.Offset = default;
        else if (listBox.ItemCount > 0) listBox.ScrollIntoView(0);
    }
}
