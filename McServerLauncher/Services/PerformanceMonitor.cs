namespace McServerLauncher.Services;

/// <summary>How a server is coping, in the three words the card shows.</summary>
public enum PerformanceLevel
{
    /// <summary>Not judged yet: the server is still settling after it started, or a backup is running.</summary>
    Unknown,

    /// <summary>Nothing sustained is wrong.</summary>
    Good,

    /// <summary>Close to the limit: players may start to notice.</summary>
    Tight,

    /// <summary>Over the limit: the server cannot keep up.</summary>
    Saturated
}

/// <summary>What the judgement rests on, most serious first.</summary>
public enum PerformanceCause
{
    None,

    /// <summary>The server ran out of memory.</summary>
    OutOfMemory,

    /// <summary>The ticks themselves are slow: TPS or MSPT.</summary>
    Ticks,

    /// <summary>The server's own "Can't keep up!" warnings.</summary>
    LagWarnings,

    /// <summary>The processor is busy for long stretches.</summary>
    Cpu,

    /// <summary>The garbage collector is working hard because the heap is nearly full.</summary>
    Memory
}

/// <summary>Where a tick reading came from.</summary>
public enum TickSource { Spark, TickQuery, PaperTps }

/// <summary>The monitor's verdict at one moment.</summary>
/// <param name="Level">How the server is coping.</param>
/// <param name="Cause">The signal behind <paramref name="Level"/>; none when it is good or not judged.</param>
/// <param name="Tps">The latest ticks per second, when the server could be asked.</param>
/// <param name="Mspt">The latest milliseconds per tick, when the server could be asked.</param>
/// <param name="LagWarnings">"Can't keep up!" warnings in the last five minutes.</param>
/// <param name="MemoryReason">Whether, and why, the server is short of memory: independent of which cause is named.</param>
public sealed record PerformanceStatus(
    PerformanceLevel Level, PerformanceCause Cause, double? Tps, double? Mspt, int LagWarnings,
    MemoryPressureReason MemoryReason = MemoryPressureReason.None);

/// <summary>
/// Decides from several signals whether a server is coping, and says which one convinced it.
/// </summary>
/// <remarks>
/// <para>
/// Pure and without a clock: every call says what time it is, so a test can run an hour in a few
/// lines. It only ever reports something <em>sustained</em> — the whole point is to warn before
/// players complain, not to flash red for the spike every server has while it loads a chunk.
/// </para>
/// <list type="bullet">
/// <item>No judgement for <see cref="Grace"/> after the server says it is ready, nor while a backup
/// runs and for <see cref="AfterBackup"/> after it: both are lag the owner asked for.</item>
/// <item>Ticks: TPS under 18 or MSPT over 50 is Saturated, TPS under 19.5 or MSPT over 40 is Tight.
/// It takes two bad readings in a row from spark (which already averages a minute) and three from the
/// others; two good ones clear it.</item>
/// <item>"Can't keep up!": three or more in five minutes is Saturated, one or two Tight. A delay over a
/// minute is a clock jump (the PC waking from sleep), not lag.</item>
/// <item>CPU: 90 % for two minutes is Saturated, 75 % for three is Tight. Measured in time, not in
/// samples, because a window in the tray samples every 20 s.</item>
/// <item>Memory: judged from the GC log by <see cref="MemoryPressure"/> (the heap nearly full after
/// collections, the collector taking over a tenth of the time, a full collection), and on its own only
/// Tight. The process's memory cannot say this: a healthy Java process looks full.</item>
/// <item>Out of memory is Saturated at once.</item>
/// </list>
/// </remarks>
public sealed class PerformanceMonitor
{
    /// <summary>How long after "Done" nothing is judged.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    /// <summary>How long after a backup ends nothing is judged.</summary>
    public static readonly TimeSpan AfterBackup = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LagWindow = TimeSpan.FromMinutes(5);

    /// <summary>A delay longer than this is the clock jumping, not the server lagging.</summary>
    private static readonly TimeSpan ClockJump = TimeSpan.FromSeconds(60);

    private DateTime? _readyAt;
    private DateTime? _backupEndedAt;
    private bool _backupRunning;
    private bool _outOfMemory;

    private readonly List<(PerformanceLevel Level, TickSource Source)> _ticks = [];
    private PerformanceLevel _tickLevel = PerformanceLevel.Good;
    private double? _tps;
    private double? _mspt;

    private readonly List<DateTime> _lag = [];

    private DateTime? _cpuTightSince;
    private DateTime? _cpuSaturatedSince;

    private readonly MemoryPressure _memory = new();

    /// <summary>The server finished starting. Resets everything the last run left.</summary>
    /// <param name="now">When.</param>
    /// <param name="ceilingMb">The heap ceiling it runs with, for judging how full the heap is. 0 when unknown.</param>
    public void Ready(DateTime now, long ceilingMb = 0)
    {
        _readyAt = now;
        _backupRunning = false;
        _backupEndedAt = null;
        _outOfMemory = false;
        _ticks.Clear();
        _tickLevel = PerformanceLevel.Good;
        _tps = _mspt = null;
        _lag.Clear();
        _cpuTightSince = _cpuSaturatedSince = null;
        _memory.Ready(now, ceilingMb);
    }

    /// <summary>The server stopped. It is not judged until the next <see cref="Ready"/>.</summary>
    public void Stopped()
    {
        _readyAt = null;
        _memory.Stopped();
    }

    /// <summary>A backup began.</summary>
    public void BackupStarted() => _backupRunning = true;

    /// <summary>A backup ended; lag that follows is still the backup's.</summary>
    public void BackupEnded(DateTime now)
    {
        _backupRunning = false;
        _backupEndedAt = now;
    }

    /// <summary>The server ran out of memory.</summary>
    public void OutOfMemory()
    {
        _outOfMemory = true;
        _memory.OutOfMemory();
    }

    /// <summary>A reading from the server itself. Either figure may be missing.</summary>
    public void AddTickReading(DateTime now, TickSource source, double? tps, double? mspt)
    {
        if (!Judging(now)) return;
        if (tps is not null) _tps = tps;
        if (mspt is not null) _mspt = mspt;

        var level = PerformanceLevel.Good;
        if (tps < 18 || mspt > 50) level = PerformanceLevel.Saturated;
        else if (tps < 19.5 || mspt > 40) level = PerformanceLevel.Tight;

        _ticks.Add((level, source));
        if (_ticks.Count > 6) _ticks.RemoveAt(0);

        var need = source == TickSource.Spark ? 2 : 3;
        if (_ticks.Count >= 2 && _ticks[^2..].All(t => t.Level == PerformanceLevel.Good))
            _tickLevel = PerformanceLevel.Good;
        else if (_ticks.Count >= need)
        {
            var worst = _ticks[^need..].Min(t => t.Level);
            if (worst >= PerformanceLevel.Tight) _tickLevel = worst;
        }
    }

    /// <summary>The server warned that it is behind by <paramref name="behind"/>.</summary>
    public void AddLagWarning(DateTime now, TimeSpan behind)
    {
        if (!Judging(now) || behind > ClockJump) return;
        _lag.Add(now);
    }

    /// <summary>One reading of the processor, 0–100 across all cores.</summary>
    public void AddCpu(DateTime now, double percent)
    {
        if (!Judging(now))
        {
            _cpuTightSince = _cpuSaturatedSince = null;
            return;
        }
        _cpuSaturatedSince = percent >= 90 ? _cpuSaturatedSince ?? now : null;
        _cpuTightSince = percent >= 75 ? _cpuTightSince ?? now : null;
    }

    /// <summary>One collection from the GC log. See <see cref="MemoryPressure"/> for what counts.</summary>
    public void AddGc(DateTime now, GcEvent e)
    {
        if (Judging(now)) _memory.AddGc(now, e);
    }

    /// <summary>The verdict at <paramref name="now"/>.</summary>
    public PerformanceStatus Evaluate(DateTime now)
    {
        _lag.RemoveAll(t => now - t > LagWindow);
        var lag = _lag.Count;

        if (_outOfMemory)
            return new PerformanceStatus(PerformanceLevel.Saturated, PerformanceCause.OutOfMemory, _tps, _mspt, lag,
                MemoryPressureReason.OutOfMemory);
        if (_readyAt is null || !Judging(now))
            return new PerformanceStatus(PerformanceLevel.Unknown, PerformanceCause.None, _tps, _mspt, lag);

        var level = PerformanceLevel.Good;
        var cause = PerformanceCause.None;
        void Consider(PerformanceLevel l, PerformanceCause c)
        {
            if (l > level) { level = l; cause = c; }
        }

        Consider(_tickLevel, PerformanceCause.Ticks);
        Consider(lag >= 3 ? PerformanceLevel.Saturated : lag > 0 ? PerformanceLevel.Tight : PerformanceLevel.Good,
            PerformanceCause.LagWarnings);
        Consider(_cpuSaturatedSince is { } s && now - s >= TimeSpan.FromMinutes(2) ? PerformanceLevel.Saturated
            : _cpuTightSince is { } t && now - t >= TimeSpan.FromMinutes(3) ? PerformanceLevel.Tight
            : PerformanceLevel.Good, PerformanceCause.Cpu);
        var memory = _memory.Evaluate(now);
        Consider(memory != MemoryPressureReason.None ? PerformanceLevel.Tight : PerformanceLevel.Good,
            PerformanceCause.Memory);

        return new PerformanceStatus(level, level == PerformanceLevel.Good ? PerformanceCause.None : cause,
            _tps, _mspt, lag, memory);
    }

    /// <summary>False while the server is settling, stopped or being backed up.</summary>
    public bool IsJudging(DateTime now) => Judging(now);

    private bool Judging(DateTime now) =>
        _readyAt is { } ready
        && now - ready >= Grace
        && !_backupRunning
        && (_backupEndedAt is not { } ended || now - ended >= AfterBackup);
}
