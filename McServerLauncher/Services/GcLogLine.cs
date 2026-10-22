using System.Globalization;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>What a collection was, as far as the memory history cares.</summary>
public enum GcKind
{
    /// <summary>A young collection, including the ones that start or prepare a mixed cycle.</summary>
    Young,

    /// <summary>A G1 mixed collection: young plus part of the old generation.</summary>
    Mixed,

    /// <summary>A full stop-the-world collection: the heap had run out of room.</summary>
    Full,

    /// <summary>ZGC's whole-heap cycle, the one that tells how much is really alive.</summary>
    Major,

    /// <summary>A G1 Remark pause. Costs time; says nothing about what is alive.</summary>
    Remark,

    /// <summary>A G1 Cleanup pause. Costs time; says nothing about what is alive.</summary>
    Cleanup
}

/// <summary>The collector a JVM reports it is using.</summary>
public enum GcCollector { Unknown, G1, Parallel, Serial, Z, Shenandoah, Other }

/// <summary>One collection read from the GC log.</summary>
/// <param name="UptimeSeconds">When it happened, counted from the JVM's start.</param>
/// <param name="Id">The JVM's own number for it.</param>
/// <param name="Kind">What kind of collection.</param>
/// <param name="BeforeBytes">Heap in use before.</param>
/// <param name="AfterBytes">Heap in use after: the nearest the log gets to what is alive.</param>
/// <param name="CommittedBytes">Heap the JVM holds from the system. 0 when the line does not say.</param>
/// <param name="PauseMs">How long the application was stopped.</param>
/// <param name="Explicit">Somebody called <c>System.gc()</c>: it says nothing about the server's need.</param>
/// <param name="EvacuationFailure">G1 ran out of room to move objects: the heap is too tight.</param>
public readonly record struct GcEvent(
    double UptimeSeconds,
    int Id,
    GcKind Kind,
    long BeforeBytes,
    long AfterBytes,
    long CommittedBytes,
    double PauseMs,
    bool Explicit,
    bool EvacuationFailure)
{
    /// <summary>True when <see cref="AfterBytes"/> is a measure of how much the server keeps alive.</summary>
    public bool ReportsLiveHeap => Kind is GcKind.Young or GcKind.Mixed or GcKind.Full or GcKind.Major;
}

/// <summary>
/// Reads one line of the unified GC log (<c>-Xlog:gc</c>) the app asks the JVM to write.
/// </summary>
/// <remarks>
/// Pure: the line in, the event out. Only the lines a pause or a whole-heap cycle ends with carry
/// sizes, and those are the only ones the app asks for, so anything else is simply not an event.
/// Unit-tested against lines copied from real runs of G1, Serial, Parallel and ZGC.
/// </remarks>
public static partial class GcLogLine
{
    /// <summary>Reads the event a line describes, or false for any other line.</summary>
    public static bool TryParse(string? line, out GcEvent gcEvent)
    {
        gcEvent = default;
        if (string.IsNullOrEmpty(line)) return false;

        var m = PauseLine().Match(line);
        if (m.Success)
        {
            var rest = m.Groups["rest"].Value;
            var kind = m.Groups["what"].Value switch
            {
                "Full" => GcKind.Full,
                "Remark" => GcKind.Remark,
                "Cleanup" => GcKind.Cleanup,
                _ => rest.Contains("(Mixed)", StringComparison.Ordinal) ? GcKind.Mixed : GcKind.Young
            };
            gcEvent = new GcEvent(
                Seconds(m.Groups["t"].Value), int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), kind,
                Bytes(m.Groups["b"].Value, m.Groups["bu"].Value),
                Bytes(m.Groups["a"].Value, m.Groups["au"].Value),
                Bytes(m.Groups["c"].Value, m.Groups["cu"].Value),
                Millis(m.Groups["ms"].Value, m.Groups["mu"].Value),
                rest.Contains("System.gc()", StringComparison.Ordinal),
                rest.Contains("Evacuation Failure", StringComparison.Ordinal)
                    || rest.Contains("To-space", StringComparison.Ordinal));
            return true;
        }

        m = MajorLine().Match(line);
        if (m.Success)
        {
            gcEvent = new GcEvent(
                Seconds(m.Groups["t"].Value), int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture),
                GcKind.Major,
                Bytes(m.Groups["b"].Value, m.Groups["bu"].Value),
                Bytes(m.Groups["a"].Value, m.Groups["au"].Value),
                0,
                Millis(m.Groups["ms"].Value, m.Groups["mu"].Value),
                m.Groups["why"].Value.Contains("System.gc()", StringComparison.Ordinal),
                false);
            return true;
        }

        return false;
    }

    /// <summary>Reads the collector from the line the JVM prints first ("Using G1").</summary>
    public static bool TryParseCollector(string? line, out GcCollector collector)
    {
        collector = GcCollector.Unknown;
        if (string.IsNullOrEmpty(line)) return false;
        var m = UsingLine().Match(line);
        if (!m.Success) return false;

        var name = m.Groups["name"].Value;
        collector = name switch
        {
            "G1" => GcCollector.G1,
            "Parallel" => GcCollector.Parallel,
            "Serial" => GcCollector.Serial,
            "Shenandoah" => GcCollector.Shenandoah,
            _ when name.Contains("Z Garbage", StringComparison.Ordinal) || name == "ZGC" => GcCollector.Z,
            _ => GcCollector.Other
        };
        return true;
    }

    private static double Seconds(string text) => double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static double Millis(string number, string unit) =>
        Seconds(number) * (unit == "s" ? 1000 : 1);

    private static long Bytes(string number, string unit) =>
        long.Parse(number, CultureInfo.InvariantCulture) * unit switch
        {
            "K" => 1024L,
            "M" => 1024L * 1024,
            "G" => 1024L * 1024 * 1024,
            _ => 1L
        };

    // [495.424s][info][gc          ] GC(720) Pause Young (Prepare Mixed) (G1 Periodic Collection) 482M->480M(692M) 14.307ms
    [GeneratedRegex(
        @"^\[(?<t>\d+(?:[.,]\d+)?)s\]\[\w+\s*\]\[gc\s*\] GC\((?<id>\d+)\) Pause (?<what>Young|Full|Remark|Cleanup)" +
        @"(?<rest>[^\r\n]*?) (?<b>\d+)(?<bu>[BKMG])->(?<a>\d+)(?<au>[BKMG])\((?<c>\d+)(?<cu>[BKMG])\) " +
        @"(?<ms>\d+(?:[.,]\d+)?)(?<mu>ms|s)\s*$")]
    private static partial Regex PauseLine();

    // [334.893s][info][gc          ] GC(90) Major Collection (Allocation Rate) 1332M(33%)->1322M(32%) 0.232s
    [GeneratedRegex(
        @"^\[(?<t>\d+(?:[.,]\d+)?)s\]\[\w+\s*\]\[gc\s*\] GC\((?<id>\d+)\) Major Collection \((?<why>.+?)\) " +
        @"(?<b>\d+)(?<bu>[BKMG])\(\d+%\)->(?<a>\d+)(?<au>[BKMG])\(\d+%\) (?<ms>\d+(?:[.,]\d+)?)(?<mu>ms|s)\s*$")]
    private static partial Regex MajorLine();

    // [0.011s][info][gc     ] Using G1
    [GeneratedRegex(@"^\[\d+(?:[.,]\d+)?s\]\[\w+\s*\]\[gc\s*\] Using (?<name>.+?)\s*$")]
    private static partial Regex UsingLine();
}
