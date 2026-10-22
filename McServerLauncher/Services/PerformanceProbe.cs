using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>The command the app asks a server for its speed with.</summary>
public enum ProbeKind
{
    /// <summary><c>spark tps</c>: the most complete answer, and it already averages a minute.</summary>
    Spark,

    /// <summary>Vanilla's <c>tick query</c> (1.20.3 and later), which works everywhere.</summary>
    TickQuery,

    /// <summary>Paper's own <c>tps</c>, for servers before <c>tick query</c> that have no spark.</summary>
    PaperTps
}

/// <summary>One answer from a server about its speed.</summary>
/// <param name="Source">Which command gave it.</param>
/// <param name="Tps">Ticks per second, when the answer has it.</param>
/// <param name="Mspt">Milliseconds per tick (the median for spark, the average for <c>tick query</c>).</param>
/// <param name="Mspt95">The slowest 5 % of ticks, when the answer has it.</param>
public sealed record TickReading(TickSource Source, double? Tps, double? Mspt, double? Mspt95);

/// <summary>
/// Asks a running server how fast it is, with the best command it understands, and keeps the answer
/// out of the console.
/// </summary>
/// <remarks>
/// <para>
/// The commands are typed into the server's console like anything the owner would type, so their
/// answers come back as ordinary console lines. Printing them every 30 seconds would bury the
/// console, so for the few seconds after asking, the lines that are the answer are recognised by their
/// exact shape (<see cref="PerformanceParsers"/>) and swallowed: they reach neither the screen, nor
/// the log on disk, nor the player history. Anything else that happens to arrive in those seconds
/// goes through untouched.
/// </para>
/// <para>
/// If a command is unknown, or three asks in a row get no answer, the next one is tried. If something
/// the owner typed arrives while a probe is waiting, <see cref="Disarm"/> is called, so what they
/// asked for is shown to them; the probe simply asks again later.
/// </para>
/// <para>
/// The output of <c>spark tps</c> and <c>tick query</c> goes to whoever ran the command, which here
/// is the console, so operators in the game do not see it (checked on 2026-10-22 with an operator
/// connected to a Paper server).
/// </para>
/// </remarks>
public sealed class PerformanceProbe
{
    /// <summary>How long after asking an answer is looked for.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private const int MissesBeforeNext = 3;

    private readonly IReadOnlyList<ProbeKind> _candidates;
    private readonly object _gate = new();
    private int _index;
    private int _misses;
    private DateTime? _armedAt;
    private bool _answered;
    private bool _failed;
    private double _target = 20;
    private double[]? _sparkTps;

    public PerformanceProbe(IReadOnlyList<ProbeKind> candidates) => _candidates = candidates;

    /// <summary>The command being tried, or null when none is left.</summary>
    public ProbeKind? Current => _index < _candidates.Count ? _candidates[_index] : null;

    /// <summary>True while the probe is waiting for an answer.</summary>
    public bool Armed
    {
        get { lock (_gate) return _armedAt is not null; }
    }

    /// <summary>The commands worth trying for a server, best first.</summary>
    /// <param name="type">The server type.</param>
    /// <param name="gameVersion">Its Minecraft version, such as <c>1.21.1</c> or <c>26.2</c>.</param>
    /// <param name="sparkInstalled">A spark jar is in its mods or plugins folder.</param>
    public static IReadOnlyList<ProbeKind> Candidates(ServerType type, string? gameVersion, bool sparkInstalled)
    {
        var paper = type is ServerType.Paper or ServerType.Purpur;
        var list = new List<ProbeKind>();

        // Paper and Purpur carry spark inside from 1.21.
        if (sparkInstalled || (paper && VersionAtLeast(gameVersion, 1, 21, 0)))
            list.Add(ProbeKind.Spark);
        if (VersionAtLeast(gameVersion, 1, 20, 3))
            list.Add(ProbeKind.TickQuery);
        if (paper)
            list.Add(ProbeKind.PaperTps);
        return list;
    }

    /// <summary>The text to type for a kind of probe.</summary>
    public static string Command(ProbeKind kind) => kind switch
    {
        ProbeKind.Spark => "spark tps",
        ProbeKind.TickQuery => "tick query",
        _ => "tps"
    };

    /// <summary>
    /// True when the game version is at least the one given. Versions from 26 on, which dropped the
    /// leading 1, are later than any 1.x.
    /// </summary>
    public static bool VersionAtLeast(string? version, int major, int minor, int patch)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version.Split('.', '-', ' ').Take(3).Select(p => int.TryParse(p, out var n) ? n : -1).ToList();
        if (parts.Count == 0 || parts[0] < 0) return false;
        if (parts[0] > major) return true;
        if (parts[0] < major) return false;
        var m = parts.Count > 1 && parts[1] >= 0 ? parts[1] : 0;
        if (m != minor) return m > minor;
        var p = parts.Count > 2 && parts[2] >= 0 ? parts[2] : 0;
        return p >= patch;
    }

    /// <summary>
    /// Starts asking. Returns the command to type, or null when there is nothing left to try. A
    /// previous ask that got no answer counts as a miss.
    /// </summary>
    public string? Ask(DateTime now)
    {
        lock (_gate)
        {
            if (_armedAt is not null && !_answered && !_failed)
            {
                if (++_misses >= MissesBeforeNext) Advance();
            }

            if (Current is not { } kind) return null;
            _armedAt = now;
            _answered = false;
            _failed = false;
            _sparkTps = null;
            return Command(kind);
        }
    }

    /// <summary>Stops waiting, so the next lines are shown. Called when the owner types something.</summary>
    public void Disarm()
    {
        lock (_gate) _armedAt = null;
    }

    /// <summary>
    /// Offers a console line to the probe. Returns true when the line is part of the answer and must
    /// not be shown; <paramref name="reading"/> is set on the line that completes it.
    /// </summary>
    public bool TryConsume(string line, DateTime now, out TickReading? reading)
    {
        reading = null;
        lock (_gate)
        {
            if (_armedAt is not { } at || now - at > Window || Current is not { } kind) return false;

            var parsed = PerformanceParsers.Parse(line);
            if (parsed.Kind == PerfLineKind.UnknownCommand)
            {
                if (!_failed)
                {
                    _failed = true;
                    Advance();
                }
                return true;
            }
            if (_failed) return false;

            switch (kind)
            {
                case ProbeKind.Spark:
                    return ConsumeSpark(parsed, out reading);
                case ProbeKind.TickQuery:
                    return ConsumeTickQuery(parsed, out reading);
                default:
                    if (parsed.Kind != PerfLineKind.PaperTps) return false;
                    reading = Answer(new TickReading(TickSource.PaperTps, parsed.Numbers[0], null, null));
                    return true;
            }
        }
    }

    private bool ConsumeSpark(PerfLine parsed, out TickReading? reading)
    {
        reading = null;
        switch (parsed.Kind)
        {
            case PerfLineKind.SparkTpsHeader:
            case PerfLineKind.SparkTickHeader:
            case PerfLineKind.SparkOther:
                return true;
            case PerfLineKind.SparkTpsValues:
                _sparkTps = parsed.Numbers;
                return true;
            case PerfLineKind.SparkTickValues when parsed.Numbers.Length == 8:
                // min/median/95th/max for ten seconds, then the same for a minute: the minute is read.
                reading = Answer(new TickReading(TickSource.Spark,
                    _sparkTps is { Length: 5 } t ? t[2] : null, parsed.Numbers[5], parsed.Numbers[6]));
                return true;
            default:
                return false;
        }
    }

    private bool ConsumeTickQuery(PerfLine parsed, out TickReading? reading)
    {
        reading = null;
        switch (parsed.Kind)
        {
            case PerfLineKind.TickQueryState:
            case PerfLineKind.TickQueryPercentiles:
                return true;
            case PerfLineKind.TickQueryTarget:
                return true;
            case PerfLineKind.TickQueryAverage:
                // The server runs as fast as it can up to its target rate: slower ticks than the
                // budget lower the rate.
                var avg = parsed.Numbers[0];
                var tps = avg > 0 ? Math.Min(_target, 1000.0 / avg) : _target;
                reading = Answer(new TickReading(TickSource.TickQuery, Math.Round(tps, 1), avg, null));
                return true;
            default:
                return false;
        }
    }

    private TickReading Answer(TickReading reading)
    {
        _answered = true;
        _misses = 0;
        return reading;
    }

    private void Advance()
    {
        _index++;
        _misses = 0;
        _target = 20;
    }
}
