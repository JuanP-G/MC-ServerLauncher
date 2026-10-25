using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// How long a toast stays, how much of that is left, and how opaque it is right now.
/// </summary>
/// <remarks>
/// <para>
/// The clock of one toast, with no window in it. The window used to carry this in a one-shot timer
/// that nothing could pause, read or test; a bar that shrinks, a pause while the pointer is over the
/// card and a fade at both ends all need the same three numbers, so they live here and the window
/// only draws them.
/// </para>
/// <para>
/// Frame by frame from a plain timer rather than a declared animation: the toast has a handful of
/// seconds to live and an animation that freezes halfway would leave a card on screen that nothing
/// ever closes.
/// </para>
/// </remarks>
public sealed class ToastLifetime
{
    /// <summary>How long an ordinary toast stays.</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromSeconds(6);

    /// <summary>How long a toast about something broken stays: long enough to read twice.</summary>
    public static readonly TimeSpan Serious = TimeSpan.FromSeconds(10);

    /// <summary>The fade at each end.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(150);

    private TimeSpan _elapsed;
    private bool _paused;

    public ToastLifetime(NotificationLevel level) => Duration = DurationFor(level);

    /// <summary>The time a toast of this level stays: longer when something is broken.</summary>
    public static TimeSpan DurationFor(NotificationLevel level) =>
        level == NotificationLevel.Error ? Serious : Normal;

    /// <summary>The whole time this toast stays, not counting pauses.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How much of <see cref="Duration"/> has gone.</summary>
    public TimeSpan Elapsed => _elapsed;

    /// <summary>True while the pointer is over the card: the clock does not move.</summary>
    /// <remarks>
    /// Pausing in the last moments steps the clock back to where the fade-out starts, so a card the
    /// pointer arrives on while it is already half faded comes back solid instead of stopping
    /// transparent.
    /// </remarks>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value)
            {
                var fadeStart = Duration - Fade;
                if (_elapsed > fadeStart) _elapsed = fadeStart;
            }
        }
    }

    /// <summary>True once the time is up and the toast should close.</summary>
    public bool Expired => _elapsed >= Duration;

    /// <summary>The share of the time still left, from 1 down to 0: the width of the bar.</summary>
    public double Remaining => Math.Clamp(1 - _elapsed / Duration, 0, 1);

    /// <summary>The opacity to draw with: fading in at the start and out at the end.</summary>
    public double Opacity
    {
        get
        {
            var fadeIn = Math.Min(1, _elapsed / Fade);
            var fadeOut = Math.Min(1, (Duration - _elapsed) / Fade);
            return Math.Clamp(Math.Min(fadeIn, fadeOut), 0, 1);
        }
    }

    /// <summary>Moves the clock on by <paramref name="step"/>, unless it is paused.</summary>
    public void Advance(TimeSpan step)
    {
        if (_paused || step <= TimeSpan.Zero) return;
        _elapsed += step;
        if (_elapsed > Duration) _elapsed = Duration;
    }
}
