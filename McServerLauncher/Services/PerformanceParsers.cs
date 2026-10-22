using System.Globalization;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>What a console line says about the server's speed.</summary>
public enum PerfLineKind
{
    None,

    /// <summary>"Can't keep up! Is the server overloaded? Running 5029ms or 100 ticks behind".</summary>
    LagWarning,

    /// <summary>spark: "TPS from last 5s, 10s, 1m, 5m, 15m:".</summary>
    SparkTpsHeader,

    /// <summary>spark: the five TPS figures.</summary>
    SparkTpsValues,

    /// <summary>spark: "Tick durations (min/med/95%ile/max ms) from last 10s, 1m:".</summary>
    SparkTickHeader,

    /// <summary>spark: the eight tick durations, ten seconds then a minute.</summary>
    SparkTickValues,

    /// <summary>Any other line spark prints in answer to <c>spark tps</c> (blank lines, CPU).</summary>
    SparkOther,

    /// <summary>Vanilla <c>tick query</c>: "The game is running normally".</summary>
    TickQueryState,

    /// <summary>Vanilla <c>tick query</c>: "Target tick rate: 20.0 per second."</summary>
    TickQueryTarget,

    /// <summary>Vanilla <c>tick query</c>: "Average time per tick: 0.4ms (Target: 50.0ms)".</summary>
    TickQueryAverage,

    /// <summary>Vanilla <c>tick query</c>: "Percentiles: P50: 0.3ms P95: 0.4ms P99: 14.8ms. Sample: 100".</summary>
    TickQueryPercentiles,

    /// <summary>Paper: "TPS from last 1m, 5m, 15m: 20.0, 20.0, 20.0".</summary>
    PaperTps,

    /// <summary>"Unknown or incomplete command" or "Unknown command".</summary>
    UnknownCommand
}

/// <summary>A console line read for speed, with the numbers it carries.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Numbers">The figures, in the order the line gives them. Empty when it carries none.</param>
public readonly record struct PerfLine(PerfLineKind Kind, double[] Numbers)
{
    public static readonly PerfLine None = new(PerfLineKind.None, []);
}

/// <summary>
/// Reads the lines that tell how fast a server is running: its own lag warnings and the answers of
/// <c>spark tps</c>, vanilla's <c>tick query</c> and Paper's <c>tps</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything is anchored on the start of the message (<see cref="ConsoleLineClassifier.MessageBody"/>),
/// the way <see cref="SaveConfirmation"/> is, because a player can type any text into the chat and
/// chat passes through the console too. Chat always begins with the player's name in angle brackets,
/// so a line that begins with these words is not chat.
/// </para>
/// <para>
/// The formats were fixed on 2026-10-22 from the output of real servers (Fabric with spark 1.10 and
/// Paper 26.2 on Minecraft 26.2), and three things in them are traps:
/// </para>
/// <list type="bullet">
/// <item>spark tags its lines with <c>[⚡]</c>. On Windows the symbol arrives as <c>?</c> or as
/// scrambled bytes, so the tag is any run of up to four non-ASCII-letter characters, never the
/// symbol itself.</item>
/// <item>Paper colours its console with ANSI escapes inside the line, and servers may use <c>§</c>
/// codes; both are removed first.</item>
/// <item>Numbers use the machine's decimal separator: Paper printed <c>20,0, 20,0, 20,0</c> on a
/// Spanish system. Figures are read with either, and the commas that separate values are told apart
/// because a decimal comma always has one digit on each side without a space.</item>
/// <item>Vanilla prints the later lines of <c>tick query</c> without a log prefix at all.</item>
/// </list>
/// </remarks>
public static partial class PerformanceParsers
{
    /// <summary>Reads one console line.</summary>
    public static PerfLine Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return PerfLine.None;

        var clean = Escapes().Replace(line, "");
        var body = ConsoleLineClassifier.MessageBody(clean);

        // Vanilla's multi-line replies: the continuation lines carry no prefix. Only the shapes that
        // exist as continuations are read that way.
        if (body is null)
            return TickQueryContinuation(clean.Trim());

        if (Lag().Match(body) is { Success: true } lag)
            return new PerfLine(PerfLineKind.LagWarning, [Number(lag.Groups["ms"].Value)]);

        if (UnknownLine().IsMatch(body)) return new PerfLine(PerfLineKind.UnknownCommand, []);

        if (SparkTag().Match(body) is { Success: true } tag)
            return Spark(body[tag.Length..].Trim());

        if (PaperTpsLine().Match(body) is { Success: true } paper)
            return new PerfLine(PerfLineKind.PaperTps, Numbers(paper.Groups["n"].Value));

        if (TickQueryState().IsMatch(body)) return new PerfLine(PerfLineKind.TickQueryState, []);

        return TickQueryContinuation(body);
    }

    private static PerfLine Spark(string text)
    {
        if (text.StartsWith("TPS from last", StringComparison.Ordinal))
            return new PerfLine(PerfLineKind.SparkTpsHeader, []);
        if (text.StartsWith("Tick durations", StringComparison.Ordinal))
            return new PerfLine(PerfLineKind.SparkTickHeader, []);

        if (SparkTpsFigures().IsMatch(text))
            return new PerfLine(PerfLineKind.SparkTpsValues, Numbers(text));

        // "0.1/0.3/0.4/14.8;  0.1/0.3/0.7/38.9": ten seconds, then a minute.
        if (SparkTickFigures().IsMatch(text))
            return new PerfLine(PerfLineKind.SparkTickValues, Numbers(text.Replace('/', ' ').Replace(';', ' ')));

        // Blank lines and the CPU block that spark prints after the ticks. Nothing else that happens
        // to start with a symbol in brackets.
        return text.Length == 0 || text.StartsWith("CPU usage from", StringComparison.Ordinal)
               || SparkCpuFigures().IsMatch(text)
            ? new PerfLine(PerfLineKind.SparkOther, [])
            : PerfLine.None;
    }

    private static PerfLine TickQueryContinuation(string text)
    {
        if (TickQueryTargetLine().IsMatch(text)) return new PerfLine(PerfLineKind.TickQueryTarget, []);

        if (TickQueryAverageLine().Match(text) is { Success: true } avg)
            return new PerfLine(PerfLineKind.TickQueryAverage, [Number(avg.Groups["ms"].Value)]);

        if (TickQueryPercentileLine().Match(text) is { Success: true } p)
            return new PerfLine(PerfLineKind.TickQueryPercentiles,
                [Number(p.Groups["p50"].Value), Number(p.Groups["p95"].Value), Number(p.Groups["p99"].Value)]);

        return PerfLine.None;
    }

    /// <summary>The numbers in a text, read with a dot or a comma for the decimal point.</summary>
    private static double[] Numbers(string text) =>
        Figure().Matches(text).Select(m => Number(m.Value)).ToArray();

    private static double Number(string text) =>
        double.Parse(text.TrimStart('*').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    // Colour and formatting that a console adds: ANSI sequences, and § codes (including the two
    // characters of §x§R§R§G§G§B§B, which are each a § and one more).
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|§.")]
    private static partial Regex Escapes();

    // spark's tag, however the symbol arrived: ⚡, ?, or a few scrambled bytes. Never an ASCII word,
    // so "[Server]" and "[Rank]" are not it.
    [GeneratedRegex(@"^\[[?\u0080-\uFFFF]{1,4}\]\s?")]
    private static partial Regex SparkTag();

    // *20.0, *20.0, 19.8, 20.0, 20.0   (the asterisk marks a figure above the target, clamped)
    [GeneratedRegex(@"^\*?\d+[.,]\d(?:\s*,\s*\*?\d+[.,]\d){4}\s*$")]
    private static partial Regex SparkTpsFigures();

    // 12%, 13%, 13%  (system)      0%, 1%, 1%  (process)
    [GeneratedRegex(@"^\d+%, \d+%, \d+%\s+\((?:system|process)\)\s*$")]
    private static partial Regex SparkCpuFigures();

    // 0.1/0.3/0.4/14.8;  0.1/0.3/0.7/38.9
    [GeneratedRegex(@"^\d+[.,]\d(?:/\d+[.,]\d){3};\s*\d+[.,]\d(?:/\d+[.,]\d){3}\s*$")]
    private static partial Regex SparkTickFigures();

    // A figure with one decimal. A comma between two figures is followed by a space, so "20,0, 20,0"
    // is two numbers and not "20", "0", "20", "0".
    [GeneratedRegex(@"\*?\d+(?:[.,]\d+)?(?=\D|$)")]
    private static partial Regex Figure();

    // Can't keep up! Is the server overloaded? Running 5029ms or 100 ticks behind     (vanilla, Paper)
    // Can't keep up! Is the server overloaded? Running 5029ms behind, skipping 100 tick(s)   (older)
    [GeneratedRegex(@"^Can't keep up! Is the server overloaded\? Running (?<ms>\d+)ms\b")]
    private static partial Regex Lag();

    // The second line of vanilla's answer repeats the command with a marker: "tps<--[HERE]".
    [GeneratedRegex(@"^Unknown (?:or incomplete )?command\b|<--\[HERE\]\s*$")]
    private static partial Regex UnknownLine();

    // TPS from last 1m, 5m, 15m: 20.0, 20.0, 20.0
    [GeneratedRegex(@"^TPS from last 1m, 5m, 15m:\s*(?<n>\*?\d+[.,]\d+\s*,\s*\*?\d+[.,]\d+\s*,\s*\*?\d+[.,]\d+)\s*$")]
    private static partial Regex PaperTpsLine();

    // The game is running normally / running slow / frozen / sprinting / stepping
    [GeneratedRegex(@"^The game is (?:running (?:normally|slow)|frozen|sprinting|stepping)\b")]
    private static partial Regex TickQueryState();

    [GeneratedRegex(@"^Target tick rate: \d+(?:[.,]\d+)? per second\.?$")]
    private static partial Regex TickQueryTargetLine();

    [GeneratedRegex(@"^Average time per tick: (?<ms>\d+(?:[.,]\d+)?)ms\b")]
    private static partial Regex TickQueryAverageLine();

    [GeneratedRegex(@"^Percentiles: P50: (?<p50>\d+(?:[.,]\d+)?)ms P95: (?<p95>\d+(?:[.,]\d+)?)ms P99: (?<p99>\d+(?:[.,]\d+)?)ms")]
    private static partial Regex TickQueryPercentileLine();
}
