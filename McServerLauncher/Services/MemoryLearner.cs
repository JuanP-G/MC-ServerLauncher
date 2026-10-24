using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>A ceiling worked out from how a server was really used, and why.</summary>
/// <param name="CeilingMb">The heap ceiling for the next start, in MB.</param>
/// <param name="Reason">What it rests on: what the server needed, or what it lacked last time.</param>
public sealed record LearnedCeiling(long CeilingMb, MemoryCeilingReason Reason);

/// <summary>
/// Works out a server's next heap ceiling from the runs the app has seen of it. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The estimate a server starts with is generous on purpose (a ceiling is a limit, not a reservation),
/// but a generous ceiling still counts against the machine's budget, and a server that only ever holds
/// a gigabyte alive does not need four. What it keeps alive after a collection, over the last runs, is
/// what it needs; 1.6 times that leaves room for the load it has not met yet.
/// </para>
/// <list type="bullet">
/// <item><b>A run counts</b> when it lasted 20 minutes, had a player on, and its live heap stayed under
/// 85 % of its ceiling. A run that reached 85 % was held back by the ceiling, so what it shows is the
/// ceiling and not the need: it is treated as pressure.</item>
/// <item><b>The next ceiling</b> is 1.6 × the 95th percentile of the peak live heap of the last ten runs
/// that count, rounded up to 512 MB, never below the floor.</item>
/// <item><b>It goes down slowly</b>: only with at least three runs that count, and by at most 1 GB per
/// start. Underestimating costs far more than overestimating (out of memory kills the server; a high
/// ceiling costs nothing until it is used).</item>
/// <item><b>It goes up at once</b>: after running out of memory, ×1.5; after any other pressure, 1 GB
/// more or ×1.25, whichever is more. This also covers the restart after a crash.</item>
/// </list>
/// </remarks>
public static class MemoryLearner
{
    /// <summary>The shortest run that counts.</summary>
    public static readonly TimeSpan MinimumRun = TimeSpan.FromMinutes(20);

    /// <summary>The share of the ceiling from which a run was held back by it.</summary>
    public const double LimitedShare = 0.85;

    /// <summary>How many of the latest counting runs are looked at.</summary>
    public const int RunsLookedAt = 10;

    /// <summary>The fewest counting runs for the ceiling to go down.</summary>
    public const int RunsToLower = 3;

    /// <summary>The most the ceiling goes down at one start, in MB.</summary>
    public const long MaxLowerMb = 1024;

    private const double Headroom = 1.6;
    private const long StepMb = 512;

    /// <summary>True when a run says what the server needs.</summary>
    public static bool Counts(MemorySession s) =>
        s.DurationSeconds >= MinimumRun.TotalSeconds
        && s.PeakPlayers >= 1
        && s.LiveSamples > 0
        && !WasLimited(s);

    /// <summary>True when a run was short of memory: it ran out, was held back by its ceiling, or the app judged it so.</summary>
    public static bool WasLimited(MemorySession s) =>
        s.OutOfMemory || s.Pressured
        || (s.CeilingMb > 0 && s.PeakLiveMb >= s.CeilingMb * LimitedShare);

    /// <summary>
    /// The ceiling to start with, or null when the history says nothing yet and the estimate stands.
    /// </summary>
    /// <param name="sessions">The server's runs, oldest first.</param>
    /// <param name="floorMb">The lowest ceiling the app ever gives this server.</param>
    /// <param name="estimateMb">What it would start with with no history.</param>
    public static LearnedCeiling? Next(IReadOnlyList<MemorySession> sessions, long floorMb, long estimateMb)
    {
        var automatic = sessions.Where(s => s.Mode == MemoryMode.Automatic).ToList();
        if (automatic.Count == 0) return null;

        var last = automatic[^1];
        var previous = last.CeilingMb > 0 ? last.CeilingMb : estimateMb;

        // Short of memory the last time: more, at once, whatever else the history says.
        if (WasLimited(last))
        {
            var raised = last.OutOfMemory
                ? previous * 3 / 2
                : Math.Max(previous + 1024, previous * 5 / 4);
            return new LearnedCeiling(RoundUp(raised), MemoryCeilingReason.RaisedAfterPressure);
        }

        var counting = automatic.Where(Counts).TakeLast(RunsLookedAt).ToList();
        if (counting.Count == 0) return null;

        var needed = RoundUp((long)Math.Ceiling(Headroom * Percentile95(counting.Select(s => s.PeakLiveMb))));
        var target = Math.Max(needed, floorMb);

        if (target >= previous) return new LearnedCeiling(target, MemoryCeilingReason.Learned);

        // Down: slowly, and only once there is enough to go on. Until then it keeps what it had.
        if (counting.Count < RunsToLower)
            return previous == estimateMb ? null : new LearnedCeiling(previous, MemoryCeilingReason.Learned);
        var lowered = Math.Max(target, previous - MaxLowerMb);
        return new LearnedCeiling(Math.Max(RoundUp(lowered), floorMb), MemoryCeilingReason.Learned);
    }

    /// <summary>
    /// The ceiling a running server would get at its next start if it were found short of memory now:
    /// what the band says it will have.
    /// </summary>
    public static long AfterPressure(long ceilingMb, bool outOfMemory) =>
        RoundUp(outOfMemory ? ceilingMb * 3 / 2 : Math.Max(ceilingMb + 1024, ceilingMb * 5 / 4));

    private static long RoundUp(long mb) => (mb + StepMb - 1) / StepMb * StepMb;

    /// <summary>Nearest-rank 95th percentile.</summary>
    private static long Percentile95(IEnumerable<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}
