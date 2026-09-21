using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Attached property that makes a ListBox auto-scroll to the end when items are
/// added (useful for the real-time console).
/// </summary>
/// <remarks>
/// Every scroll is deferred and folded together (see <see cref="ScrollCoalescer"/>). That matters
/// most when the bound collection is swapped, which is what selecting another server does: scrolling
/// a freshly bound list of two thousand wrapped lines forces the virtualizer to measure its way to
/// the bottom, and doing it inline made switching to a running server visibly slower than switching
/// to a stopped one. Queued at <see cref="DispatcherPriority.Background"/>, the new server's console
/// is on screen first and the scroll happens once the frame is done.
/// </remarks>
public static class AutoScrollBehavior
{
    public static readonly AttachedProperty<bool> AutoScrollProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("AutoScroll", typeof(AutoScrollBehavior));

    public static bool GetAutoScroll(ListBox o) => o.GetValue(AutoScrollProperty);
    public static void SetAutoScroll(ListBox o, bool value) => o.SetValue(AutoScrollProperty, value);

    static AutoScrollBehavior()
    {
        AutoScrollProperty.Changed.AddClassHandler<ListBox>((lb, e) => OnChanged(lb, e));
    }

    private static void OnChanged(ListBox listBox, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        // Re-hook whenever the bound collection (ItemsSource) changes (e.g. switching server).
        listBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ItemsControl.ItemsSourceProperty)
                Hook(listBox, args.OldValue as INotifyCollectionChanged, args.NewValue as INotifyCollectionChanged);
        };
        Hook(listBox, null, listBox.ItemsSource as INotifyCollectionChanged);
    }

    private static void Hook(ListBox listBox, INotifyCollectionChanged? old, INotifyCollectionChanged? @new)
    {
        if (old is not null) old.CollectionChanged -= OnCollectionChanged;
        if (@new is not null)
        {
            @new.CollectionChanged += OnCollectionChanged;
            ListBoxes.AddOrUpdate(@new, listBox);
        }
        RequestScroll(listBox);
    }

    // Maps a watched collection to its ListBox so the static handler can find it.
    private static readonly ConditionalWeakTable<INotifyCollectionChanged, ListBox> ListBoxes = new();

    // One coalescer per list: two consoles on screen must not swallow each other's scrolls.
    private static readonly ConditionalWeakTable<ListBox, ScrollCoalescer> Coalescers = new();

    private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)) return;
        if (sender is INotifyCollectionChanged c && ListBoxes.TryGetValue(c, out var lb))
            RequestScroll(lb);
    }

    private static void RequestScroll(ListBox listBox)
    {
        // An empty list has no end to scroll to, which is the common case for a server that has
        // never been started — and the reason those switch instantly today.
        if (listBox.ItemCount == 0) return;

        var coalescer = Coalescers.GetValue(listBox,
            lb => new ScrollCoalescer(
                action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
                () => ScrollToEnd(lb)));
        coalescer.Request();
    }

    private static void ScrollToEnd(ListBox listBox)
    {
        var count = listBox.ItemCount;
        if (count > 0)
            listBox.ScrollIntoView(count - 1);
    }
}
