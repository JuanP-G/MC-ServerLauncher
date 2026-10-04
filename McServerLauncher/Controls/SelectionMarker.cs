using Avalonia;
using Avalonia.Controls;

namespace McServerLauncher.Controls;

/// <summary>How a <see cref="SelectionMarker"/> marks the current item.</summary>
public enum MarkerShape
{
    /// <summary>A thin accent bar down the item's left edge (the side rail).</summary>
    Bar,

    /// <summary>A card behind the whole item (the settings pages).</summary>
    Fill,
}

/// <summary>
/// Marks the current item of a menu built from buttons, and slides to the next one when it changes.
/// </summary>
/// <remarks>
/// The current item is the child of <see cref="Host"/> carrying the class <c>on</c>, which is how the
/// rail and the settings pages already say it (<c>Classes.on="{Binding Is…}"</c>), so the menus need
/// no extra state. Put it over the menu for a <see cref="MarkerShape.Bar"/> and under it for a
/// <see cref="MarkerShape.Fill"/>, in the same cell.
/// </remarks>
public class SelectionMarker : SlidingIndicator
{
    public static readonly StyledProperty<Panel?> HostProperty =
        AvaloniaProperty.Register<SelectionMarker, Panel?>(nameof(Host));

    public static readonly StyledProperty<MarkerShape> ShapeProperty =
        AvaloniaProperty.Register<SelectionMarker, MarkerShape>(nameof(Shape));

    /// <summary>The panel whose children are the menu's items.</summary>
    public Panel? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public MarkerShape Shape
    {
        get => GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    /// <summary>The bar's width, and how much shorter than its item it is at each end.</summary>
    internal const double BarWidth = 3;
    private const double BarInset = 2;

    public SelectionMarker() => ApplyShape();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShapeProperty) ApplyShape();
        if (change.Property != HostProperty) return;

        if (change.OldValue is Panel old)
            foreach (var child in old.Children) child.Classes.CollectionChanged -= OnItemClassesChanged;
        if (change.NewValue is Panel host)
            foreach (var child in host.Children) child.Classes.CollectionChanged += OnItemClassesChanged;
        Follow(change.OldValue as Panel, change.NewValue as Panel);
    }

    private void OnItemClassesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Place();

    private void ApplyShape()
    {
        if (Shape == MarkerShape.Bar)
        {
            Mark.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentSelected"));
            Mark.ClearValue(Border.BorderBrushProperty);
            Mark.BorderThickness = default;
            Mark.CornerRadius = new CornerRadius(2);
        }
        else
        {
            Mark.Bind(Border.BackgroundProperty, this.GetResourceObservable("SurfaceCard"));
            Mark.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SurfaceEdge"));
            Mark.BorderThickness = new Thickness(1);
            Mark.CornerRadius = new CornerRadius(4);
        }
        Place();
    }

    /// <summary>The current item's box, or the bar down its left edge.</summary>
    protected override Rect? Measure()
    {
        if (Host is not { IsEffectivelyVisible: true } host) return null;
        var item = host.Children.FirstOrDefault(c => c.IsVisible && c.Classes.Contains("on"));
        if (item?.TranslatePoint(default, this) is not { } at) return null;

        var box = new Rect(at, item.Bounds.Size);
        return Shape == MarkerShape.Bar
            ? new Rect(box.X, box.Y + BarInset, BarWidth, Math.Max(0, box.Height - 2 * BarInset))
            : box;
    }
}
