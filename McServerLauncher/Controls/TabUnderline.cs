using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;

namespace McServerLauncher.Controls;

/// <summary>
/// One underline for a whole tab strip, which slides to the picked tab instead of each tab blinking
/// its own on and off.
/// </summary>
/// <remarks>
/// <para>
/// It sits over the <see cref="TabControl"/> it follows (same cell, no hit testing) and measures the
/// picked tab's title every time the layout settles, so the line always matches the text it is under.
/// The Fluent theme's own per-tab line is hidden for strips marked <c>sliding</c>; the slide itself,
/// and the fade of the page that comes in, are in <c>Styles/Motion.axaml</c> with the rest of the motion.
/// </para>
/// <para>
/// The page fade is started by swapping a class on the page host, because a style animation only
/// runs when its selector starts to match: alternating two classes is what lets it run again on
/// every change, not just the first.
/// </para>
/// </remarks>
public class TabUnderline : Panel
{
    public static readonly StyledProperty<TabControl?> TabsProperty =
        AvaloniaProperty.Register<TabUnderline, TabControl?>(nameof(Tabs));

    /// <summary>The strip to follow.</summary>
    public TabControl? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    /// <summary>Same thickness and gap as the Fluent line it replaces.</summary>
    internal const double Thickness = 2;
    private const double BottomGap = 2;

    private readonly Border _line = new()
    {
        Height = Thickness,
        CornerRadius = new CornerRadius(3),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        IsVisible = false,
    };

    private bool _placed;
    private Rect _target;
    private bool _flip;

    public TabUnderline()
    {
        IsHitTestVisible = false;
        _line.Classes.Add("underline");
        _line.Bind(Border.BackgroundProperty, this.GetResourceObservable("TabItemHeaderSelectedPipeFill"));
        Children.Add(_line);
    }

    /// <summary>Where the line is, in this control's coordinates; empty while it is hidden.</summary>
    internal Rect Target => _line.IsVisible ? _target : default;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TabsProperty) return;

        if (change.OldValue is TabControl old)
        {
            old.SelectionChanged -= OnSelectionChanged;
            old.LayoutUpdated -= OnLayoutUpdated;
        }
        if (change.NewValue is TabControl tabs)
        {
            tabs.Classes.Add("sliding");
            tabs.SelectionChanged += OnSelectionChanged;
            tabs.LayoutUpdated += OnLayoutUpdated;
        }
        Place();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Place();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Selection events bubble: a list inside a page (the console, the players) changing its own
        // selection arrives here too, and must not move the line or fade the page.
        if (!ReferenceEquals(e.Source, Tabs)) return;
        Place();
        FadeInPage();
    }

    /// <summary>Puts the line under the picked tab's title, sliding when it was already somewhere.</summary>
    internal void Place()
    {
        var title = Tabs is { } tabs ? TitleOf(tabs) : null;
        var origin = title?.TranslatePoint(default, this);
        var tab = title?.FindAncestorOfType<TabItem>();
        var bottom = tab?.TranslatePoint(new Point(0, tab.Bounds.Height), this);

        if (title is null || origin is null || bottom is null || title.Bounds.Width <= 0)
        {
            _line.IsVisible = false;
            return;
        }

        var target = new Rect(origin.Value.X, bottom.Value.Y - BottomGap - Thickness, title.Bounds.Width, Thickness);
        if (_placed && target == _target && _line.IsVisible) return;
        _target = target;

        // The first time it appears it goes straight to its tab: sliding in from the corner of the
        // window would be motion that says nothing.
        if (!_placed) _line.Transitions = null;
        _line.Width = target.Width;
        _line.RenderTransform = TransformOperations.Parse(
            FormattableString.Invariant($"translate({target.X:0.##}px, {target.Y:0.##}px)"));
        _line.IsVisible = true;
        if (!_placed) _line.ClearValue(Avalonia.Animation.Animatable.TransitionsProperty);
        _placed = true;
    }

    /// <summary>
    /// The text of the picked tab: the first visible one in its title, so a title that keeps its
    /// width with hidden copies of other words (Mods / Plugins) is measured by the word on show.
    /// </summary>
    private static Control? TitleOf(TabControl tabs)
    {
        if (!tabs.IsEffectivelyVisible || tabs.SelectedIndex < 0) return null;
        if (tabs.ContainerFromIndex(tabs.SelectedIndex) is not TabItem { IsEffectivelyVisible: true } tab) return null;

        var presenter = tab.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter");
        if (presenter is null) return null;

        return presenter.GetVisualDescendants().OfType<TextBlock>()
                   .FirstOrDefault(t => t.IsEffectivelyVisible && t.Opacity > 0)
               ?? (Control)presenter;
    }

    private void FadeInPage()
    {
        if (Tabs?.GetVisualDescendants().OfType<ContentPresenter>()
                .FirstOrDefault(p => p.Name == "PART_SelectedContentHost") is not { } host) return;
        _flip = !_flip;
        host.Classes.Set("pagein-a", _flip);
        host.Classes.Set("pagein-b", !_flip);
    }
}
