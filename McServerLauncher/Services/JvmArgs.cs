using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Reads a server's extra JVM arguments for the decisions the automatic memory profile must leave
/// to their owner.
/// </summary>
/// <remarks>
/// <para>
/// The extra arguments come after the app's own on the command line, and the JVM keeps the last
/// value of an option, so whatever the user wrote already wins. That is not enough: an automatic
/// profile laid under somebody's <c>-Xmx6G</c> would still pick its own collector and margins around
/// numbers it did not choose, and the result would be neither theirs nor the app's. A server whose
/// arguments size the heap, pick a collector or tune how memory is given back is treated as Manual.
/// </para>
/// <para>
/// <c>-XX:+UseG1GC</c> alone does not count. It names the collector the profile uses anyway, and it
/// is in half the start scripts on the internet; treating it as a choice would leave most imported
/// servers on fixed memory for a flag that changes nothing.
/// </para>
/// <para>
/// The arguments are cut with <see cref="JavaCommandLine.Split"/>, the same reader that builds the
/// command the server is started with, so what is looked for here is exactly what the JVM receives.
/// This class used to carry a splitter of its own that ignored backslashes and dropped empty arguments.
/// </para>
/// </remarks>
public static partial class JvmArgs
{
    /// <summary>
    /// True if the arguments size the heap, choose a collector other than G1, or tune how memory is
    /// returned — anything the automatic profile would otherwise decide.
    /// </summary>
    public static bool SetsMemory(string? args) =>
        JavaCommandLine.Split(args).Any(t => MemoryToken().IsMatch(t));

    /// <summary>
    /// True if the arguments already say something about compact object headers, which the profile
    /// then leaves out rather than contradict.
    /// </summary>
    /// <remarks>
    /// Turning compressed class pointers off counts too: compact headers depend on them, and a JVM
    /// given both refuses to start.
    /// </remarks>
    public static bool MentionsCompactHeaders(string? args) =>
        JavaCommandLine.Split(args).Any(t => t.Contains("UseCompactObjectHeaders", StringComparison.OrdinalIgnoreCase)
                             || t.Equals("-XX:-UseCompressedClassPointers", StringComparison.OrdinalIgnoreCase));

    // Heap size, collectors other than G1, the options the profile tunes, and OpenJ9's own versions.
    [GeneratedRegex(
        @"^(?:-Xm[sxn]\d" +
        @"|-XX:(?:Max|Min|Initial)RAMPercentage=" +
        @"|-XX:MaxRAM=" +
        @"|-XX:(?:Initial|Max|Min)HeapSize=" +
        @"|-XX:\+Use(?:Z|Shenandoah|Parallel|ParallelOld|Serial|ConcMarkSweep|Epsilon)GC$" +
        @"|-XX:(?:Min|Max)HeapFreeRatio=" +
        @"|-XX:G1PeriodicGCInterval=" +
        @"|-XX:SoftMaxHeapSize=" +
        @"|-XX:\+AlwaysPreTouch$" +
        @"|-Xgcpolicy:" +
        @"|-Xminf" +
        @"|-Xmaxf" +
        @"|-Xsoftmx)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemoryToken();
}
