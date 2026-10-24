namespace McServerLauncher.Services;

/// <summary>How tight the agent keeps a server's heap.</summary>
public enum HeapMargins
{
    /// <summary>The margins the server started with: 10 % to 30 % free, a periodic collection every 15 s.</summary>
    Relaxed,

    /// <summary>Hand memory back sooner: 5 % to 15 % free, a periodic collection every 5 s.</summary>
    Tight
}

/// <summary>
/// Decides, from how much memory the machine has free, whether the agent should hold a server's heap
/// tighter. Pure: the app decides, the agent only carries it out.
/// </summary>
/// <remarks>
/// <para>
/// With the machine's free memory under 15 %, servers with nobody on tighten first: giving back what
/// an empty server holds costs nobody anything. A server with players waits until it is under 10 %,
/// when the machine is about to start swapping and a slightly busier collector is the lesser evil.
/// Everything goes back to the relaxed margins only above 25 %: the gap is what keeps it from flapping
/// when free memory hovers around one threshold.
/// </para>
/// <para>
/// The margins are the ones the lab found to give back the most without costing tick time; they are
/// applied at run time because they cost a little collection time, which is worth paying only while the
/// machine needs the memory.
/// </para>
/// </remarks>
public static class MemoryAgentPolicy
{
    /// <summary>Below this share of free memory a server with nobody on is tightened.</summary>
    public const double TightenEmpty = 0.15;

    /// <summary>Below this share a server with players is tightened too.</summary>
    public const double TightenWithPlayers = 0.10;

    /// <summary>Above this share every server goes back to the relaxed margins.</summary>
    public const double Relax = 0.25;

    /// <summary>The margins the app gives a server at start (they match <see cref="MemoryProfile"/>).</summary>
    public static readonly (int MinFree, int MaxFree, int PeriodicMs) RelaxedValues = (10, 30, 15000);

    /// <summary>The tight margins.</summary>
    public static readonly (int MinFree, int MaxFree, int PeriodicMs) TightValues = (5, 15, 5000);

    /// <summary>The margins to hold now.</summary>
    /// <param name="current">What the server has now.</param>
    /// <param name="freeShare">The machine's free memory as a share of its total, 0 to 1; negative when unknown.</param>
    /// <param name="players">Players on the server.</param>
    public static HeapMargins Decide(HeapMargins current, double freeShare, int players)
    {
        if (freeShare < 0) return current;   // not knowing is not a reason to change anything
        if (freeShare > Relax) return HeapMargins.Relaxed;

        var tightenBelow = players > 0 ? TightenWithPlayers : TightenEmpty;
        return freeShare < tightenBelow ? HeapMargins.Tight : current;
    }

    /// <summary>The text of the control file for a set of margins, as the agent reads it.</summary>
    public static string ControlText(HeapMargins margins)
    {
        var (min, max, ms) = margins == HeapMargins.Tight ? TightValues : RelaxedValues;
        return $"MinHeapFreeRatio={min}\nMaxHeapFreeRatio={max}\nG1PeriodicGCInterval={ms}\n";
    }
}
