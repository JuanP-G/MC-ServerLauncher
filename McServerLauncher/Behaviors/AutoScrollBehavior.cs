using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Attached property that makes a ListBox auto-scroll to the end when items are
/// added (useful for the real-time console).
/// </summary>
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
            _listBoxes.AddOrUpdate(@new, listBox);
        }
        ScrollToEnd(listBox);
    }

    // Maps a watched collection to its ListBox so the static handler can find it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<INotifyCollectionChanged, ListBox> _listBoxes = new();

    private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)) return;
        if (sender is INotifyCollectionChanged c && _listBoxes.TryGetValue(c, out var lb))
            Dispatcher.UIThread.Post(() => ScrollToEnd(lb));
    }

    private static void ScrollToEnd(ListBox listBox)
    {
        var count = listBox.ItemCount;
        if (count > 0)
            listBox.ScrollIntoView(count - 1);
    }
}
