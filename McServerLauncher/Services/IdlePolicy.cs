namespace McServerLauncher.Services;

/// <summary>
/// The rules for stopping an empty server and for leaving it alone after it was woken, as plain
/// functions of their inputs.
/// </summary>
/// <remarks>
/// Kept apart from the controller that applies them so each rule can be checked directly: getting
/// one wrong means either a server that never stops or — far worse — one that stops while people are
/// playing.
/// </remarks>
public static class IdlePolicy
{
    /// <summary>
    /// How long after waking the idle timer is ignored, so a player has time to actually get in.
    /// </summary>
    /// <remarks>
    /// Without this, a server set to stop after a minute would wake, find itself still empty while
    /// the world loads, and shut down before the player finished connecting — over and over.
    /// </remarks>
    public static readonly TimeSpan WakeGrace = TimeSpan.FromMinutes(5);

    /// <summary>Whether the empty-server clock should be running at all.</summary>
    public static bool ShouldCountIdle(int minutes, bool running, int playerCount) =>
        minutes > 0 && running && playerCount == 0;

    /// <summary>Whether it has been empty long enough to stop.</summary>
    public static bool IsIdleLongEnough(int minutes, DateTime emptySinceUtc, DateTime nowUtc) =>
        nowUtc - emptySinceUtc >= TimeSpan.FromMinutes(minutes);

    /// <summary>Whether the server was woken so recently that its emptiness does not count yet.</summary>
    public static bool IsWithinWakeGrace(DateTime? wokeAtUtc, DateTime nowUtc) =>
        wokeAtUtc is not null && nowUtc - wokeAtUtc.Value < WakeGrace;

    /// <summary>How long is left before an empty server stops itself. Never negative.</summary>
    public static TimeSpan IdleRemaining(int minutes, DateTime emptySinceUtc, DateTime nowUtc)
    {
        var left = TimeSpan.FromMinutes(minutes) - (nowUtc - emptySinceUtc);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// Formats the countdown, rounding seconds <em>up</em>.
    /// </summary>
    /// <remarks>
    /// Truncating would park the display on 0:00 for up to a second while the server is still
    /// perfectly alive, which reads as a stuck clock. Rounding up means it shows 0:01 until the
    /// moment it really is zero.
    /// </remarks>
    public static string FormatCountdown(TimeSpan left)
    {
        var t = TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
