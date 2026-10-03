namespace McServerLauncher.Services;

/// <summary>What the clock says about the next automatic backup.</summary>
public enum BackupDue
{
    /// <summary>Nothing to do: switched off, or the interval has not run out yet.</summary>
    NotYet,

    /// <summary>The interval ran out, but nobody has played since the last one, so it is not worth making.</summary>
    Skip,

    /// <summary>Time to back up.</summary>
    Now
}

/// <summary>
/// Decides when a running server is due for an automatic backup.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the timer that asks it so that the rule can be tested at any hour of any day,
/// rather than by waiting an hour and hoping.
/// </para>
/// <para>
/// <see cref="BackupDue.Skip"/> is the reason this is not just a subtraction. A server left running
/// overnight with nobody on it barely changes — Minecraft only ticks chunks that somebody is near —
/// so backing it up every hour would fill the disk with copies of the same world. Skipping moves the
/// clock on instead, so the next backup lands an interval after somebody actually turns up rather
/// than the moment they do.
/// </para>
/// </remarks>
public static class BackupSchedule
{
    public const int MinMinutes = 5;
    public const int MaxMinutes = 24 * 60;
    public const int DefaultMinutes = 60;

    /// <summary>
    /// Brings a stored interval back into range: settings.json is a file people can edit, and a
    /// zero there would otherwise mean "back up on every tick".
    /// </summary>
    public static int Clamp(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);

    /// <summary>Whether a backup is due, given when the last one was and whether anyone has played since.</summary>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="lastUtc">When the last automatic backup was made, or the server started.</param>
    /// <param name="intervalMinutes">The configured interval; clamped here, so an odd stored value is harmless.</param>
    /// <param name="playedSince">Whether anybody has been connected since <paramref name="lastUtc"/>.</param>
    /// <param name="enabled">Whether automatic backups are switched on for this server at all.</param>
    public static BackupDue Due(DateTime nowUtc, DateTime lastUtc, int intervalMinutes, bool playedSince,
        bool enabled = true)
    {
        if (!enabled) return BackupDue.NotYet;
        if (nowUtc - lastUtc < TimeSpan.FromMinutes(Clamp(intervalMinutes))) return BackupDue.NotYet;
        return playedSince ? BackupDue.Now : BackupDue.Skip;
    }
}
