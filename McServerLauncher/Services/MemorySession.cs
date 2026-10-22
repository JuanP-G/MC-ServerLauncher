using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>How a run ended, for the history.</summary>
public enum SessionEnd
{
    /// <summary>The server was stopped on purpose.</summary>
    Stopped = 0,

    /// <summary>The process ended by itself, or never got to run.</summary>
    Crashed = 1,

    /// <summary>The app was closed with the server running; the end was not seen.</summary>
    AppClosed = 2
}

/// <summary>What the app learned about memory during one run of a server.</summary>
/// <remarks>
/// Stored as JSON, so: enums as numbers, appended to and never reordered, and every property has a
/// default for a file written before it existed.
/// </remarks>
public sealed record MemorySession
{
    public DateTime StartedUtc { get; init; }
    public int DurationSeconds { get; init; }
    public MemoryMode Mode { get; init; }
    public long CeilingMb { get; init; }
    public int JavaMajor { get; init; }
    public GcCollector Collector { get; init; }
    public int ModCount { get; init; }
    public int PeakPlayers { get; init; }
    public int ViewDistance { get; init; }
    public int SimulationDistance { get; init; }

    /// <summary>The most heap in use after a collection: the nearest to what the server needed at once.</summary>
    public long PeakLiveMb { get; init; }

    /// <summary>95 % of the collections left less than this in use.</summary>
    public long P95LiveMb { get; init; }

    /// <summary>How many collections <see cref="PeakLiveMb"/> and <see cref="P95LiveMb"/> come from.</summary>
    public int LiveSamples { get; init; }

    /// <summary>The most heap the JVM held from the system.</summary>
    public long PeakCommittedMb { get; init; }

    /// <summary>The most private memory the process held.</summary>
    public long PeakPrivateMb { get; init; }

    /// <summary>Share of the run the application was stopped for collection, in percent.</summary>
    public double GcTimePercent { get; init; }

    public int GcCount { get; init; }
    public int FullGcs { get; init; }

    /// <summary>Collections somebody asked for with <c>System.gc()</c>.</summary>
    public int ExplicitGcs { get; init; }

    /// <summary>The heap was too tight for G1 to move objects at least once.</summary>
    public bool EvacuationFailures { get; init; }

    /// <summary>The server reported running out of memory.</summary>
    public bool OutOfMemory { get; init; }

    public SessionEnd End { get; init; }
}

/// <summary>What is known about a run when it starts.</summary>
public sealed record MemorySessionStart(
    DateTime StartedUtc,
    MemoryMode Mode,
    long CeilingMb,
    int JavaMajor,
    int ModCount,
    int ViewDistance,
    int SimulationDistance);

/// <summary>
/// Gathers a run's memory measurements as they arrive, and turns them into one
/// <see cref="MemorySession"/> when it ends.
/// </summary>
/// <remarks>
/// Pure and without a clock: times come in as arguments, so a test can say exactly when things
/// happened. It does no I/O and keeps no list that grows with the run: the live-heap readings go into
/// a histogram of one-megabyte steps, so a month-long run costs what an hour does.
/// </remarks>
public sealed class MemorySessionTracker
{
    private readonly MemorySessionStart _start;
    private readonly SortedDictionary<int, int> _live = new();
    private int _liveSamples;
    private long _peakLiveMb;
    private long _peakCommittedMb;
    private long _peakPrivateMb;
    private double _pauseMs;
    private int _gcCount;
    private int _fullGcs;
    private int _explicitGcs;
    private int _players;
    private bool _evacuationFailures;
    private bool _outOfMemory;

    public MemorySessionTracker(MemorySessionStart start) => _start = start;

    /// <summary>The collector the JVM reported; unknown until its first log line.</summary>
    public GcCollector Collector { get; private set; }

    /// <summary>Heap in use after the latest collection that tells, in MB. 0 before the first.</summary>
    public long LiveMb { get; private set; }

    /// <summary>Heap the JVM held at the latest collection that says, in MB. 0 before the first.</summary>
    public long CommittedMb { get; private set; }

    /// <summary>True once the server has reported running out of memory.</summary>
    public bool OutOfMemory => _outOfMemory;

    /// <summary>True when there is anything worth keeping about this run.</summary>
    public bool HasData => _gcCount > 0 || _outOfMemory;

    /// <summary>Notes the collector the JVM says it uses.</summary>
    public void NoteCollector(GcCollector collector) => Collector = collector;

    /// <summary>Takes in one collection from the log.</summary>
    public void Add(GcEvent e)
    {
        _gcCount++;
        _pauseMs += e.PauseMs;
        if (e.Kind == GcKind.Full) _fullGcs++;
        if (e.Explicit) _explicitGcs++;
        if (e.EvacuationFailure) _evacuationFailures = true;

        if (e.CommittedBytes > 0)
        {
            CommittedMb = ToMb(e.CommittedBytes);
            _peakCommittedMb = Math.Max(_peakCommittedMb, CommittedMb);
        }

        // A collection somebody forced says what was alive at that moment, not what the server
        // needs, and one that only paused (Remark, Cleanup) says nothing about the heap at all.
        if (!e.ReportsLiveHeap || e.Explicit) return;

        LiveMb = ToMb(e.AfterBytes);
        _peakLiveMb = Math.Max(_peakLiveMb, LiveMb);
        _live[(int)LiveMb] = _live.GetValueOrDefault((int)LiveMb) + 1;
        _liveSamples++;
    }

    /// <summary>Takes in one reading of the process from outside.</summary>
    public void AddSample(long privateMb, int players)
    {
        _peakPrivateMb = Math.Max(_peakPrivateMb, privateMb);
        _players = Math.Max(_players, players);
    }

    /// <summary>Notes that the server reported running out of memory.</summary>
    public void NoteOutOfMemory() => _outOfMemory = true;

    /// <summary>The run, as it will be remembered.</summary>
    public MemorySession Finish(DateTime endUtc, SessionEnd end)
    {
        var duration = Math.Max(0, (endUtc - _start.StartedUtc).TotalSeconds);
        return new MemorySession
        {
            StartedUtc = _start.StartedUtc,
            DurationSeconds = (int)duration,
            Mode = _start.Mode,
            CeilingMb = _start.CeilingMb,
            JavaMajor = _start.JavaMajor,
            Collector = Collector,
            ModCount = _start.ModCount,
            PeakPlayers = _players,
            ViewDistance = _start.ViewDistance,
            SimulationDistance = _start.SimulationDistance,
            PeakLiveMb = _peakLiveMb,
            P95LiveMb = Percentile(0.95),
            LiveSamples = _liveSamples,
            PeakCommittedMb = _peakCommittedMb,
            PeakPrivateMb = _peakPrivateMb,
            GcTimePercent = duration > 0 ? Math.Round(_pauseMs / (duration * 1000) * 100, 2) : 0,
            GcCount = _gcCount,
            FullGcs = _fullGcs,
            ExplicitGcs = _explicitGcs,
            EvacuationFailures = _evacuationFailures,
            OutOfMemory = _outOfMemory,
            End = end
        };
    }

    /// <summary>Nearest-rank percentile of the live-heap readings, in MB.</summary>
    private long Percentile(double p)
    {
        if (_liveSamples == 0) return 0;
        var rank = (int)Math.Ceiling(p * _liveSamples);
        var seen = 0;
        foreach (var (mb, count) in _live)
        {
            seen += count;
            if (seen >= rank) return mb;
        }
        return _peakLiveMb;
    }

    private static long ToMb(long bytes) => (bytes + 512 * 1024) / (1024 * 1024);
}
