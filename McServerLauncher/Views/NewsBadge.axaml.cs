using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;

namespace McServerLauncher.Views;

/// <summary>
/// The mark on Novedades: a number, a dot, a dot with a halo that pulses, or nothing.
/// </summary>
/// <remarks>
/// Shown only through <see cref="Mode"/> and <see cref="Text"/>, and it never decides whether there
/// is anything unread: whoever places it hides it when there is not. The rail binds both to
/// <c>NewsViewModel</c>; the Settings page sets <see cref="Text"/> to a sample so the style can be
/// judged before choosing it.
/// </remarks>
public partial class NewsBadge : UserControl
{
    public static readonly StyledProperty<NewsBadgeStyle> ModeProperty =
        AvaloniaProperty.Register<NewsBadge, NewsBadgeStyle>(nameof(Mode));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NewsBadge, string?>(nameof(Text));

    // The mark and every visual above it while it is in the visual tree, so that one of them being
    // hidden is noticed. Avalonia does not publish "effectively visible" as something to listen to, and a
    // badge inside a panel that was never shown is never attached at all.
    private readonly List<Visual> _watched = [];

    static NewsBadge()
    {
        ModeProperty.Changed.AddClassHandler<NewsBadge>((badge, _) => badge.Update());
        TextProperty.Changed.AddClassHandler<NewsBadge>((badge, _) => badge.Update());
    }

    public NewsBadge()
    {
        InitializeComponent();
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var visual in this.GetSelfAndVisualAncestors())
        {
            visual.PropertyChanged += OnWatchedPropertyChanged;
            _watched.Add(visual);
        }
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var visual in _watched) visual.PropertyChanged -= OnWatchedPropertyChanged;
        _watched.Clear();
        Update();
    }

    private void OnWatchedPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) Update();
    }

    /// <summary>Whether the mark is in a window and nothing from it up to the window is hidden.</summary>
    private bool OnScreen => _watched.Count > 0 && _watched.All(visual => visual.IsVisible);

    /// <summary>Which of the four styles to draw.</summary>
    public NewsBadgeStyle Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <summary>What the number style says ("3", "9+"): the count, already worded.</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private void Update()
    {
        Pill.IsVisible = Mode == NewsBadgeStyle.Number;
        Dot.IsVisible = Mode is NewsBadgeStyle.Dot or NewsBadgeStyle.Pulse;
        // The pulse is an animation that never ends, and its style starts on the halo's own IsVisible,
        // which does not follow a parent being hidden. So the halo is only "visible" while the mark is
        // really on screen: hidden (nothing unread) or on a Settings page nobody is looking at, it
        // stops costing frames.
        Halo.IsVisible = Mode == NewsBadgeStyle.Pulse && OnScreen;
        Number.Text = Text;
    }
}
