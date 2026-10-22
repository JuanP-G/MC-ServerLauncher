namespace McServerLauncher.Services;

/// <summary>
/// The memory ceilings of the servers running right now, so the next one to start knows what is left.
/// </summary>
/// <remarks>
/// <para>
/// It adds up ceilings, not memory in use: a ceiling is what a server is allowed to grow to, and the
/// budget is about not letting every server grow to the whole machine at once. A server in Manual
/// mode reserves its own <c>MaxRamGb</c> and is never lowered; only Automatic ones adapt to what
/// is left.
/// </para>
/// <para>
/// App-wide state, like <see cref="NotificationPreferences.Global"/>: one machine, one budget.
/// </para>
/// </remarks>
public static class MemoryBudget
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Reserved = new(StringComparer.Ordinal);

    /// <summary>What the other running servers have reserved, in MB.</summary>
    public static long ReservedByOthersMb(string serverId)
    {
        lock (Gate)
            return Reserved.Where(r => r.Key != serverId).Sum(r => r.Value);
    }

    /// <summary>Records the ceiling a server has just started with. Replaces an earlier one.</summary>
    public static void Reserve(string serverId, long ceilingMb)
    {
        lock (Gate) Reserved[serverId] = ceilingMb;
    }

    /// <summary>Gives a stopped server's ceiling back. Harmless if it had none.</summary>
    public static void Release(string serverId)
    {
        lock (Gate) Reserved.Remove(serverId);
    }

    /// <summary>Forgets every reservation. For tests.</summary>
    internal static void Clear()
    {
        lock (Gate) Reserved.Clear();
    }
}
