using System.IO;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>Why a server got the memory ceiling it got.</summary>
public enum MemoryCeilingReason
{
    /// <summary>Manual mode: the owner's <c>MaxRamGb</c>.</summary>
    Manual,

    /// <summary>The estimate for its kind of server and number of mods.</summary>
    Estimated,

    /// <summary>A server from before Automatic mode keeps the larger ceiling it already had.</summary>
    KeptPrevious,

    /// <summary>Lowered to what is left of the memory set aside for servers.</summary>
    Budget,

    /// <summary>Not even the minimum fit in the budget; the minimum is used anyway.</summary>
    Floor,

    /// <summary>The estimate plus the one-off raise the Performance page applied for this start.</summary>
    Boosted,

    /// <summary>What the server needed over its last runs (see <see cref="MemoryLearner"/>).</summary>
    Learned,

    /// <summary>More than last time, because the last run was short of memory.</summary>
    RaisedAfterPressure
}

/// <summary>Everything the memory decision depends on, gathered before a server starts.</summary>
/// <param name="Config">The server.</param>
/// <param name="Java">The Java it starts on.</param>
/// <param name="ModCount">Jars in its <c>mods</c> folder; 0 for servers without mods.</param>
/// <param name="TotalRamMb">The machine's memory. 0 when unknown, which disables the budget.</param>
/// <param name="ReservedByOthersMb">Ceilings of the other servers running right now.</param>
/// <param name="Dedicated">The machine is only for servers ("Este PC es solo para servidores").</param>
/// <param name="BoostMb">A one-off raise for this start, asked for from the Performance page. Never saved as the server's ceiling.</param>
/// <param name="Learned">What the server's history says it needs, or null while it says nothing.</param>
public sealed record MemoryInputs(
    ServerConfig Config,
    JavaService.JavaRuntime Java,
    int ModCount,
    long TotalRamMb,
    long ReservedByOthersMb,
    bool Dedicated,
    long BoostMb = 0,
    LearnedCeiling? Learned = null);

/// <summary>What a server starts with.</summary>
/// <param name="Mode">The mode in effect, which can differ from the one saved (see <see cref="MemoryProfile.Effective"/>).</param>
/// <param name="CeilingMb">The heap ceiling, <c>-Xmx</c>, in MB.</param>
/// <param name="Flags">The memory flags, in command-line order.</param>
/// <param name="Reason">Why the ceiling is what it is.</param>
/// <param name="CappedTo32Bit">The ceiling was lowered because the Java is 32-bit.</param>
public sealed record MemoryPlan(
    MemoryMode Mode,
    long CeilingMb,
    IReadOnlyList<string> Flags,
    MemoryCeilingReason Reason,
    bool CappedTo32Bit);

/// <summary>
/// Decides how much memory a server may use and the JVM flags that make it use no more than it needs.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 2026-10-21/22 with bots on Minecraft 26.2: a fixed <c>-Xms</c>/<c>-Xmx</c> commits
/// whatever the heap ever reached and keeps it, so an empty server sat on 3.5-4.7 GB. With a small
/// initial heap, G1's periodic collection and low free-heap margins, the same servers came back to
/// 0.9-1.1 GB a few minutes after the last player left (−69 % to −81 %), with the same tick time.
/// </para>
/// <para>
/// The ceiling is a limit, not a reservation: with these flags the JVM only commits what it uses.
/// That is why it is generous. Too low costs much more than too high — at 768 MB the test server ran
/// out of memory, at 1 GB its tick suffered — while a high ceiling costs nothing until it is needed.
/// </para>
/// <para>
/// Every flag is given only from the Java release where it is final: a JVM refuses to start over an
/// option it does not know. Pure, so all of it is testable without a JVM.
/// </para>
/// </remarks>
public static class MemoryProfile
{
    /// <summary>The initial heap in Automatic mode, in MB: what an empty 26.x server holds.</summary>
    public const int InitialHeapMb = 128;

    /// <summary>The largest heap a 32-bit JVM can reliably reserve, in MB.</summary>
    public const long ThirtyTwoBitCeilingMb = 1024;

    /// <summary>
    /// The mode that applies: Manual when the extra JVM arguments already size the memory, otherwise
    /// the one saved, and Automatic for a server that never chose.
    /// </summary>
    public static MemoryMode Effective(ServerConfig config) =>
        JvmArgs.SetsMemory(config.ExtraJvmArgs) ? MemoryMode.Manual : config.MemoryMode ?? MemoryMode.Automatic;

    /// <summary>
    /// True for a server moved to Automatic by the update without its owner having chosen: it shows
    /// the one-line notice that offers its old numbers back.
    /// </summary>
    public static bool ShowsMigrationNotice(ServerConfig config) =>
        config.MemoryMode is null && Effective(config) == MemoryMode.Automatic;

    /// <summary>Manual mode's flags: the owner's two numbers, exactly as the app always passed them.</summary>
    public static IReadOnlyList<string> ManualFlags(ServerConfig config) =>
        [$"-Xms{config.MinRamGb}G", $"-Xmx{config.MaxRamGb}G"];

    /// <summary>The flags that let the heap follow what the server uses, for this Java.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Java 12 and later: G1's periodic collection (JEP 346) and low free-heap margins, which
    /// return memory when the server goes quiet. Before 12 there is no periodic collection, and the
    /// margins alone only act on a full collection, so they are not worth an option.</item>
    /// <item>Java 25 and later: compact object headers (JEP 519), final from 25 and experimental in 24,
    /// where they would need an unlock flag.</item>
    /// <item>OpenJ9 rejects HotSpot's <c>-XX:</c> options and gets only the heap size.</item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> AutomaticFlags(JavaService.JavaRuntime java, long ceilingMb, string? extraArgs)
    {
        var flags = new List<string> { $"-Xms{InitialHeapMb}M", $"-Xmx{ceilingMb}M" };
        if (java.IsOpenJ9) return flags;

        if (java.Major >= 12)
        {
            flags.Add("-XX:G1PeriodicGCInterval=15000");
            flags.Add("-XX:MinHeapFreeRatio=10");
            flags.Add("-XX:MaxHeapFreeRatio=30");
        }
        if (java.Major >= 25 && !JvmArgs.MentionsCompactHeaders(extraArgs))
            flags.Add("-XX:+UseCompactObjectHeaders");
        return flags;
    }

    /// <summary>True for the server types whose <c>mods</c> folder is read for the estimate.</summary>
    public static bool HasMods(ServerType type) =>
        type is ServerType.Fabric or ServerType.Forge or ServerType.NeoForge;

    /// <summary>Jars in the server's <c>mods</c> folder, not counting disabled ones. 0 for other types.</summary>
    public static int CountMods(ServerConfig config)
    {
        if (!HasMods(config.Type)) return 0;
        try
        {
            var mods = Path.Combine(config.FolderPath, "mods");
            return Directory.Exists(mods) ? Directory.EnumerateFiles(mods, "*.jar").Count() : 0;
        }
        catch
        {
            return 0; // an unreadable folder: estimated as a server without mods
        }
    }

    /// <summary>The ceiling a server starts with before anything has been learned about it, in MB.</summary>
    /// <remarks>
    /// Four players exploring new terrain peaked at 1.1-1.6 GB of heap on Fabric and Paper; the
    /// 33-mod test server at 1.7 GB with a lighter load. These leave more than twice that.
    /// </remarks>
    public static long EstimateCeilingMb(int modCount) => modCount switch
    {
        <= 30 => 4096,
        <= 100 => 6144,
        _ => 8192
    };

    /// <summary>The lowest ceiling the app gives a server, in MB, whatever the budget says.</summary>
    public static long FloorMb(int modCount) => modCount > 0 ? 2560 : 1536;

    /// <summary>The memory all running servers may share as ceilings, in MB. 0 when the machine's is unknown.</summary>
    /// <remarks>
    /// Half the machine by default, because the case that matters is a PC somebody also plays on:
    /// the game, the browser and the system need the other half. A machine kept only for servers
    /// gives them everything but 2 GB.
    /// </remarks>
    public static long BudgetMb(long totalRamMb, bool dedicated) =>
        totalRamMb <= 0 ? 0 : dedicated ? Math.Max(0, totalRamMb - 2048) : totalRamMb / 2;

    /// <summary>Decides the mode, the ceiling and the memory flags a server starts with.</summary>
    public static MemoryPlan Plan(MemoryInputs inputs)
    {
        var config = inputs.Config;
        if (Effective(config) == MemoryMode.Manual)
            return new MemoryPlan(MemoryMode.Manual, config.MaxRamGb * 1024L, ManualFlags(config),
                MemoryCeilingReason.Manual, false);

        var ceiling = EstimateCeilingMb(inputs.ModCount);
        var reason = MemoryCeilingReason.Estimated;

        // A server moved over by the update had a ceiling its owner chose; until the app knows better,
        // it is not given less than that.
        if (config.MemoryMode is null && config.MaxRamGb * 1024L > ceiling)
        {
            ceiling = config.MaxRamGb * 1024L;
            reason = MemoryCeilingReason.KeptPrevious;
        }

        // What the server has shown it needs replaces the estimate, and the owner's old numbers too:
        // those were kept only "until the app knows better".
        if (inputs.Learned is { } learned)
        {
            ceiling = learned.CeilingMb;
            reason = learned.Reason;
        }

        if (inputs.BoostMb > 0)
        {
            ceiling += inputs.BoostMb;
            reason = MemoryCeilingReason.Boosted;
        }

        var budget = BudgetMb(inputs.TotalRamMb, inputs.Dedicated);
        if (budget > 0)
        {
            var available = Math.Max(0, budget - inputs.ReservedByOthersMb);
            var floor = FloorMb(inputs.ModCount);
            if (ceiling > available)
            {
                // Never refused: a server that does not start is worse than one with a tight ceiling.
                ceiling = available >= floor ? available / 256 * 256 : floor;
                reason = available >= floor ? MemoryCeilingReason.Budget : MemoryCeilingReason.Floor;
            }
        }

        var capped = false;
        if (!inputs.Java.Is64Bit && ceiling > ThirtyTwoBitCeilingMb)
        {
            ceiling = ThirtyTwoBitCeilingMb;
            capped = true;
        }

        return new MemoryPlan(MemoryMode.Automatic, ceiling,
            AutomaticFlags(inputs.Java, ceiling, config.ExtraJvmArgs), reason, capped);
    }

    /// <summary>
    /// The same plan with fewer flags, for a Java that refused the full set: compact headers go
    /// first, then the G1 options. The heap size always stays.
    /// </summary>
    /// <returns>Null when there is nothing left to drop.</returns>
    public static MemoryPlan? WithoutLastOptionalFlag(MemoryPlan plan)
    {
        if (plan.Mode == MemoryMode.Manual) return null;
        var flags = plan.Flags.ToList();
        if (flags.Remove("-XX:+UseCompactObjectHeaders")) return plan with { Flags = flags };
        if (flags.RemoveAll(f => f.StartsWith("-XX:", StringComparison.Ordinal)) > 0) return plan with { Flags = flags };
        return null;
    }
}
