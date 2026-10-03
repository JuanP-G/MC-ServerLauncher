using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace McServerLauncher.Controls;

/// <summary>
/// One line of text that, when it does not fit, slides sideways under the pointer so the rest can be read.
/// </summary>
/// <remarks>
/// <para>
/// A server called <c>Java+Bedrock (paper) para amigos</c> was cut off at the edge of its list row,
/// and the only way to read it was to rename it. A tooltip would have answered the question but made
/// the reader wait and then look somewhere else; sliding the text answers it where the eye already is.
/// </para>
/// <para>
/// At rest it is an ordinary trimmed label, ellipsis included. The scrolling only happens while the
/// pointer is over the row it belongs to (the list item, when there is one), and only when something
/// is actually hidden — short names never move. It pauses at both ends and goes back, at a steady
/// speed rather than a fixed duration, so a long name is not a blur and a short overflow not a crawl.
/// </para>
/// <para>
/// Font size, weight and colour are set the usual way for text — <c>TextElement.FontWeight</c> and
/// friends on this control — and reach the label inside it by inheritance.
/// </para>
/// </remarks>
public class MarqueeText : Panel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeText, string?>(nameof(Text));

    /// <summary>
    /// Marks the element whose hover starts the slide: a card, a table row. Without one, the nearest
    /// list item is used, and failing that the text itself.
    /// </summary>
    /// <remarks>
    /// The text alone is too small a target. On the server card the name only moved with the pointer
    /// exactly on top of it, when the natural thing is to point at the card and expect to read it.
    /// </remarks>
    public static readonly AttachedProperty<bool> IsHoverScopeProperty =
        AvaloniaProperty.RegisterAttached<MarqueeText, Control, bool>("IsHoverScope");

    public static bool GetIsHoverScope(Control c) => c.GetValue(IsHoverScopeProperty);
    public static void SetIsHoverScope(Control c, bool value) => c.SetValue(IsHoverScopeProperty, value);

    /// <summary>Pixels per second. Slow enough to follow with the eye.</summary>
    internal const double Speed = 40;

    /// <summary>How long it rests at each end, so the start and the finish can actually be read.</summary>
    internal static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TranslateTransform _shift = new();
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;
    private Control? _scope;
    private double _fullWidth;
    private double _viewport;
    private bool _scrolling;

    static MarqueeText()
    {
        TextProperty.Changed.AddClassHandler<MarqueeText>((m, _) => m.OnTextChanged());
        AffectsMeasure<MarqueeText>(TextProperty);
    }

    public MarqueeText()
    {
        // Transparent, not null: a panel with no background is invisible to the pointer, and this is
        // the control the pointer has to find.
        Background = Brushes.Transparent;
        ClipToBounds = true;
        _label.RenderTransform = _shift;
        Children.Add(_label);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>True when the text is wider than the space it was given.</summary>
    public bool IsOverflowing { get; private set; }

    internal bool IsScrolling => _scrolling;

    /// <summary>How far the text has slid to the left, in pixels.</summary>
    internal double Offset => -_shift.X;

    private void OnTextChanged()
    {
        _label.Text = Text;
        StopScroll();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _label.TextTrimming = _scrolling ? TextTrimming.None : TextTrimming.CharacterEllipsis;

        // The text's own width, whatever room there is: that is what decides whether it overflows.
        _label.Measure(Size.Infinity);
        _fullWidth = _label.DesiredSize.Width;

        var width = double.IsInfinity(availableSize.Width) ? _fullWidth : Math.Min(_fullWidth, availableSize.Width);
        return new Size(width, _label.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_scrolling) _viewport = finalSize.Width;

        IsOverflowing = _fullWidth > finalSize.Width + 0.5;

        // At rest the label gets only the room there is, so it shows its ellipsis; while sliding it
        // gets its whole width, and the clip of this panel is the window it moves behind.
        var labelWidth = _scrolling ? Math.Max(_fullWidth, finalSize.Width) : finalSize.Width;
        _label.Arrange(new Rect(0, 0, labelWidth, finalSize.Height));

        // The full text, for anyone who does not hover slowly enough, or at all.
        ToolTip.SetTip(this, IsOverflowing ? Text : null);
        return finalSize;
    }

    // ---------------------------------------------------------------- the pointer

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _scope = FindScope(this);
        _scope.PointerEntered += OnEnter;
        _scope.PointerExited += OnExit;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scope is not null)
        {
            _scope.PointerEntered -= OnEnter;
            _scope.PointerExited -= OnExit;
            _scope = null;
        }
        StopScroll();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The element whose hover counts: the nearest one marked as a scope, else the nearest list item,
    /// else the text itself. The whole row or card, not just the letters — pointing at the status dot
    /// beside a name, or anywhere on its card, should be enough.
    /// </summary>
    internal static Control FindScope(MarqueeText text)
    {
        foreach (var ancestor in text.GetVisualAncestors())
        {
            if (ancestor is Control c && (GetIsHoverScope(c) || c is ListBoxItem)) return c;
        }
        return text;
    }

    private void OnEnter(object? sender, Avalonia.Input.PointerEventArgs e) => StartScroll();

    private void OnExit(object? sender, Avalonia.Input.PointerEventArgs e) => StopScroll();

    // ---------------------------------------------------------------- the motion

    /// <summary>Starts sliding, if there is anything hidden to slide to.</summary>
    internal void StartScroll()
    {
        if (_scrolling || !IsOverflowing || _fullWidth <= _viewport) return;

        _scrolling = true;
        InvalidateMeasure();      // the label must now be measured and arranged untrimmed
        _clock.Restart();

        _timer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Back to rest: the text where it began and its ellipsis back.</summary>
    internal void StopScroll()
    {
        _timer?.Stop();
        _clock.Reset();
        var was = _scrolling;
        _scrolling = false;
        _shift.X = 0;
        if (was) InvalidateMeasure();
    }

    private void OnTick(object? sender, EventArgs e) =>
        _shift.X = -OffsetAt(_clock.Elapsed, _fullWidth - _viewport);

    /// <summary>
    /// Where the text is, a given time after it started: rest, slide to the end, rest, slide back, repeat.
    /// </summary>
    internal static double OffsetAt(TimeSpan elapsed, double distance)
    {
        if (distance <= 0) return 0;

        var travel = distance / Speed;                       // seconds one way
        var cycle = 2 * (Pause.TotalSeconds + travel);
        var t = elapsed.TotalSeconds % cycle;

        var rest = Pause.TotalSeconds;
        if (t < rest) return 0;
        t -= rest;
        if (t < travel) return t * Speed;
        t -= travel;
        if (t < rest) return distance;
        t -= rest;
        return distance - t * Speed;
    }
}
