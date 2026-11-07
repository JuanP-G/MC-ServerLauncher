using Avalonia;
using Avalonia.Controls;
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

    /// <summary>Which of the four styles to draw.</summary>
    public NewsBadgeStyle Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <summary>What the number style says ("3", "9+"): the count, already worded.</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private void Update()
    {
        Pill.IsVisible = Mode == NewsBadgeStyle.Number;
        Dot.IsVisible = Mode is NewsBadgeStyle.Dot or NewsBadgeStyle.Pulse;
        Halo.IsVisible = Mode == NewsBadgeStyle.Pulse;
        Number.Text = Text;
    }
}
