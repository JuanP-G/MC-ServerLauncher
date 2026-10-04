using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Every so often, gives a control a short "I'm here" animation: the styles for it are
/// <c>.beacon-a</c> / <c>.beacon-b</c> in <c>Styles/Motion.axaml</c>, and this only decides when.
/// </summary>
/// <remarks>
/// <para>
/// A timer that starts a two-second animation, rather than one long animation that sits still for
/// most of its length. The long one was tried first and does not hold up: Avalonia only advances an
/// animation while the clock is being driven, and with nothing else on screen moving, a 25-second
/// timeline spent 23 seconds frozen on its first frame and never reached the part that moves.
/// </para>
/// <para>
/// It keeps quiet while the pointer is on it (it has been found) and while the control carries the
/// class <c>quiet</c> (what it points to is already open). Either one ending starts the wait again
/// from zero, so it never fires the moment the pointer leaves.
/// </para>
/// </remarks>
public static class BeaconBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(BeaconBehavior));

    public static bool GetIsEnabled(Control c) => c.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control c, bool value) => c.SetValue(IsEnabledProperty, value);

    /// <summary>How long between two reminders.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(25);

    private static readonly AttachedProperty<DispatcherTimer?> TimerProperty =
        AvaloniaProperty.RegisterAttached<Control, DispatcherTimer?>("Timer", typeof(BeaconBehavior));

    static BeaconBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is true) Attach(c);
            else Detach(c);
        });
    }

    private static void Attach(Control c)
    {
        var timer = new DispatcherTimer { Interval = Interval };
        timer.Tick += (_, _) => Fire(c);
        c.SetValue(TimerProperty, timer);

        c.AttachedToVisualTree += OnAttached;
        c.DetachedFromVisualTree += OnDetached;
        c.PointerEntered += OnPointerEntered;
        c.PointerExited += OnPointerExited;
        c.Classes.CollectionChanged += (_, _) => Restart(c);
        if (c.IsInTree()) Restart(c);
    }

    private static void Detach(Control c)
    {
        c.GetValue(TimerProperty)?.Stop();
        c.AttachedToVisualTree -= OnAttached;
        c.DetachedFromVisualTree -= OnDetached;
        c.PointerEntered -= OnPointerEntered;
        c.PointerExited -= OnPointerExited;
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Restart((Control)sender!);
    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => ((Control)sender!).GetValue(TimerProperty)?.Stop();
    private static void OnPointerEntered(object? sender, PointerEventArgs e) => ((Control)sender!).GetValue(TimerProperty)?.Stop();
    private static void OnPointerExited(object? sender, PointerEventArgs e) => Restart((Control)sender!);

    /// <summary>Starts the wait from zero, unless the control should keep quiet right now.</summary>
    private static void Restart(Control c)
    {
        if (c.GetValue(TimerProperty) is not { } timer) return;
        timer.Stop();
        if (IsQuiet(c) || !c.IsInTree()) return;
        timer.Start();
    }

    private static bool IsQuiet(Control c) => c.IsPointerOver || c.Classes.Contains("quiet");

    /// <summary>Plays the reminder now (the timer's tick; also what tests call).</summary>
    internal static void Fire(Control c)
    {
        if (IsQuiet(c) || !c.IsEffectivelyVisible) return;

        // A style animation runs when its selector starts to match, so the two classes take turns:
        // with only one, the reminder would play the first time and never again.
        var next = c.Classes.Contains("beacon-a") ? "beacon-b" : "beacon-a";
        c.Classes.Remove("beacon-a");
        c.Classes.Remove("beacon-b");
        c.Classes.Add(next);
    }

    private static bool IsInTree(this Control c) => c.GetVisualRoot() is not null;
}
