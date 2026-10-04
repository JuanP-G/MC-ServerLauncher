using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Transformation;

namespace McServerLauncher.Controls;

/// <summary>
/// The mark that shows which item of a menu is the current one, drawn once and slid from item to
/// item instead of each item switching its own mark on and off.
/// </summary>
/// <remarks>
/// <para>
/// It sits over (or under) the menu it follows, in the same cell and without hit testing, and asks
/// its subclass where the mark goes every time the layout settles. The slide itself is a transition
/// on <c>Border.indicator</c> in <c>Styles/Motion.axaml</c>, with the rest of the motion.
/// </para>
/// <para>
/// The first time the mark appears it goes straight to its place: sliding in from the corner of the
/// window would be motion that says nothing.
/// </para>
/// </remarks>
public abstract class SlidingIndicator : Panel
{
    /// <summary>The mark. Subclasses give it its colour and shape.</summary>
    protected Border Mark { get; } = new()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        IsVisible = false,
    };

    private bool _placed;
    private Rect _target;

    protected SlidingIndicator()
    {
        IsHitTestVisible = false;
        Mark.Classes.Add("indicator");
        Children.Add(Mark);
    }

    /// <summary>Where the mark is, in this control's coordinates; empty while it is hidden.</summary>
    internal Rect Target => Mark.IsVisible ? _target : default;

    /// <summary>Where the mark should be now, in this control's coordinates, or null to hide it.</summary>
    protected abstract Rect? Measure();

    /// <summary>Moves the mark to where <see cref="Measure"/> says, sliding when it was already somewhere.</summary>
    internal void Place()
    {
        if (Measure() is not { Width: > 0, Height: > 0 } target)
        {
            Mark.IsVisible = false;
            return;
        }
        if (_placed && target == _target && Mark.IsVisible) return;
        _target = target;

        if (!_placed) Mark.Transitions = null;
        Mark.Width = target.Width;
        Mark.Height = target.Height;
        Mark.RenderTransform = TransformOperations.Parse(
            FormattableString.Invariant($"translate({target.X:0.##}px, {target.Y:0.##}px)"));
        Mark.IsVisible = true;
        if (!_placed) Mark.ClearValue(Avalonia.Animation.Animatable.TransitionsProperty);
        _placed = true;
    }

    /// <summary>Hooks a control whose layout passes should re-place the mark.</summary>
    protected void Follow(Avalonia.Layout.Layoutable? old, Avalonia.Layout.Layoutable? now)
    {
        if (old is not null) old.LayoutUpdated -= OnLayoutUpdated;
        if (now is not null) now.LayoutUpdated += OnLayoutUpdated;
        Place();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Place();
}
