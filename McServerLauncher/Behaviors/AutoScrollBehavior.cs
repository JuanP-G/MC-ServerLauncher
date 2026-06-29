using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Attached property that makes a ListBox auto-scroll to the end when items
/// are added (useful for the real-time console).
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.RegisterAttached(
            "AutoScroll",
            typeof(bool),
            typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnAutoScrollChanged));

    public static bool GetAutoScroll(DependencyObject obj) => (bool)obj.GetValue(AutoScrollProperty);
    public static void SetAutoScroll(DependencyObject obj, bool value) => obj.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox)
            return;

        if (e.NewValue is true)
        {
            listBox.Loaded += ListBoxOnLoaded;
            // Re-hook when the ItemsSource changes (when switching the selected server).
            ((INotifyCollectionChanged?)listBox.Items)!.CollectionChanged += (_, args) =>
                ScrollOnAdd(listBox, args);
        }
    }

    private static void ListBoxOnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.Items.Count > 0)
            ScrollToEnd(listBox);
    }

    private static void ScrollOnAdd(ListBox listBox, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            ScrollToEnd(listBox);
    }

    private static void ScrollToEnd(ListBox listBox)
    {
        if (listBox.Items.Count == 0)
            return;

        var scrollViewer = FindScrollViewer(listBox);
        scrollViewer?.ScrollToBottom();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
            return sv;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result is not null)
                return result;
        }
        return null;
    }
}
