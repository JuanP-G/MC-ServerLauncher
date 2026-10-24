namespace McServerLauncher.Services;

/// <summary>Why a server counts as short of memory.</summary>
public enum MemoryPressureReason
{
    None,

    /// <summary>After every collection for two minutes, three quarters of the ceiling was still alive.</summary>
    HeapNearlyFull,

    /// <summary>The collector had the server paused for over a tenth of the last two minutes.</summary>
    GcOverhead,

    /// <summary>A full collection: the heap ran out of room and everything stopped to make some.</summary>
    FullGc,

    /// <summary>The server reported running out of memory.</summary>
    OutOfMemory
}

/// <summary>
/// Decides from the GC log whether a server is short of memory. Pure, with the time given by the caller.
/// </summary>
/// <remarks>
/// <para>
/// What the process holds says nothing here: a healthy Java process looks full. What matters is what
/// survives a collection and how much of the time the collector costs, which is what the GC log gives.
/// Nothing is judged for <see cref="Grace"/> after the server is ready (loading a world is a burst
/// every server has), except running out of memory, which is judged at once.
/// </para>
/// <para>Pressure is any of these, once the grace has passed:</para>
/// <list type="bullet">
/// <item>for two minutes, with at least three collections, what is alive after each is over 75 % of the
/// ceiling;</item>
/// <item>the paused time is over 10 % of the last two minutes;</item>
/// <item>a full collection nobody asked for, which counts for <see cref="FullGcHold"/>;</item>
/// <item>the server reports running out of memory, which counts until the next <see cref="Ready"/>.</item>
/// </list>
/// A collection somebody forced (<c>System.gc()</c>) says nothing about need and is ignored.
/// </remarks>
public sealed class MemoryPressure
{
    /// <summary>How long after "Done" nothing is judged.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    /// <summary>How long a condition has to hold.</summary>
    public static readonly TimeSpan Sustained = TimeSpan.FromMinutes(2);

    /// <summary>How long a full collection keeps the pressure on.</summary>
    public static readonly TimeSpan FullGcHold = TimeSpan.FromMinutes(5);

    private const double HeapShare = 0.75;
    private const double PauseShare = 0.10;
    private const int MinCollections = 3;

    private long _ceilingMb;
    private DateTime? _readyAt;
    private bool _outOfMemory;
    private DateTime? _heapFullSince;
    private int _heapFullCount;
    private DateTime? _fullGcAt;
    private readonly Queue<(DateTime At, double PauseMs)> _pauses = new();

    /// <summary>The server is ready: starts the grace period and forgets the last run.</summary>
    public void Ready(DateTime now, long ceilingMb)
    {
        _readyAt = now;
        _ceilingMb = ceilingMb;
        _outOfMemory = false;
        _heapFullSince = null;
        _heapFullCount = 0;
        _fullGcAt = null;
        _pauses.Clear();
    }

    /// <summary>The server stopped.</summary>
    public void Stopped() => _readyAt = null;

    /// <summary>The server reported running out of memory.</summary>
    public void OutOfMemory() => _outOfMemory = true;

    /// <summary>Takes in one collection from the GC log.</summary>
    public void AddGc(DateTime now, GcEvent e)
    {
        if (_readyAt is not { } ready || now - ready < Grace || e.Explicit) return;

        _pauses.Enqueue((now, e.PauseMs));
        while (_pauses.Count > 0 && now - _pauses.Peek().At > Sustained) _pauses.Dequeue();

        if (e.Kind == GcKind.Full) _fullGcAt = now;

        if (!e.ReportsLiveHeap) return;
        if (_ceilingMb > 0 && e.AfterBytes > _ceilingMb * 1024L * 1024 * HeapShare)
        {
            _heapFullSince ??= now;
            _heapFullCount++;
        }
        else
        {
            _heapFullSince = null;
            _heapFullCount = 0;
        }
    }

    /// <summary>Why the server counts as short of memory at <paramref name="now"/>, or none.</summary>
    public MemoryPressureReason Evaluate(DateTime now)
    {
        if (_outOfMemory) return MemoryPressureReason.OutOfMemory;
        if (_readyAt is not { } ready || now - ready < Grace) return MemoryPressureReason.None;

        if (_fullGcAt is { } full && now - full <= FullGcHold) return MemoryPressureReason.FullGc;

        if (_heapFullSince is { } since && now - since >= Sustained && _heapFullCount >= MinCollections)
            return MemoryPressureReason.HeapNearlyFull;

        // Judged only over a window that has been watched whole, or a single long pause at the start
        // of it would look like a tenth of nothing.
        if (now - ready >= Grace + Sustained)
        {
            var paused = _pauses.Where(p => now - p.At <= Sustained).Sum(p => p.PauseMs);
            if (paused > Sustained.TotalMilliseconds * PauseShare) return MemoryPressureReason.GcOverhead;
        }

        return MemoryPressureReason.None;
    }
}
