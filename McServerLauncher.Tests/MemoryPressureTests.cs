using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// When a server counts as short of memory: judged from the collector, after the start-up burst, and
/// only for what lasts.
/// </summary>
public class MemoryPressureTests
{
    private const long MB = 1024 * 1024;
    private const long Ceiling = 4096;
    private static readonly DateTime Start = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A pressure monitor whose grace period is over, and the moment it ended.</summary>
    private static (MemoryPressure Pressure, DateTime Now) Settled(long ceiling = Ceiling)
    {
        var p = new MemoryPressure();
        p.Ready(Start, ceiling);
        return (p, Start + MemoryPressure.Grace);
    }

    private static GcEvent Gc(long liveMb, GcKind kind = GcKind.Young, double pauseMs = 10, bool forced = false) =>
        new(1, 0, kind, liveMb * 2 * MB, liveMb * MB, Ceiling * MB, pauseMs, forced, false);

    // --- grace ---

    [Fact]
    public void Nothing_is_judged_while_the_world_loads()
    {
        var p = new MemoryPressure();
        p.Ready(Start, Ceiling);
        p.AddGc(Start.AddMinutes(1), Gc(3900, GcKind.Full, 2000));

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(Start.AddMinutes(2)));
        Assert.Equal(MemoryPressureReason.None, p.Evaluate(Start.AddMinutes(10)));
    }

    [Fact]
    public void A_server_that_is_stopped_is_not_judged()
    {
        var (p, now) = Settled();
        p.AddGc(now, Gc(900, GcKind.Full));
        p.Stopped();

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(5)));
    }

    // --- the heap nearly full ---

    [Fact]
    public void Three_quarters_of_the_ceiling_alive_after_every_collection_for_two_minutes_is_pressure()
    {
        var (p, now) = Settled();
        for (var s = 0; s <= 120; s += 20) p.AddGc(now.AddSeconds(s), Gc(3200));   // 78 %

        Assert.Equal(MemoryPressureReason.HeapNearlyFull, p.Evaluate(now.AddSeconds(120)));
    }

    [Fact]
    public void Not_before_two_minutes_have_passed()
    {
        var (p, now) = Settled();
        for (var s = 0; s <= 100; s += 20) p.AddGc(now.AddSeconds(s), Gc(3200));

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(100)));
    }

    [Fact]
    public void Not_with_fewer_than_three_collections()
    {
        var (p, now) = Settled();
        p.AddGc(now, Gc(3200));
        p.AddGc(now.AddSeconds(125), Gc(3200));

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(125)));
    }

    [Fact]
    public void One_collection_that_leaves_the_heap_roomy_starts_the_count_again()
    {
        var (p, now) = Settled();
        for (var s = 0; s <= 80; s += 20) p.AddGc(now.AddSeconds(s), Gc(3200));
        p.AddGc(now.AddSeconds(100), Gc(1000));                                   // plenty of room
        for (var s = 120; s <= 200; s += 20) p.AddGc(now.AddSeconds(s), Gc(3200));

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(200)));
    }

    [Fact]
    public void Exactly_three_quarters_is_not_over_it()
    {
        var (p, now) = Settled();
        for (var s = 0; s <= 140; s += 20) p.AddGc(now.AddSeconds(s), Gc(3072));   // 75 %

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(140)));
    }

    [Fact]
    public void Without_a_known_ceiling_the_heap_rule_is_silent()
    {
        var (p, now) = Settled(ceiling: 0);
        for (var s = 0; s <= 140; s += 20) p.AddGc(now.AddSeconds(s), Gc(3900));

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(140)));
    }

    // --- the collector's cost ---

    [Fact]
    public void A_collector_pausing_the_server_for_over_a_tenth_of_two_minutes_is_pressure()
    {
        var (p, now) = Settled();
        // 13.2 s of pauses in 2 minutes, with plenty of heap left.
        for (var s = 0; s <= 120; s += 10) p.AddGc(now.AddSeconds(s), Gc(800, pauseMs: 1100));

        Assert.Equal(MemoryPressureReason.GcOverhead, p.Evaluate(now.AddSeconds(125)));
    }

    [Fact]
    public void A_busy_but_cheap_collector_is_fine()
    {
        var (p, now) = Settled();
        for (var s = 0; s <= 120; s += 1) p.AddGc(now.AddSeconds(s), Gc(800, pauseMs: 20));   // 2.4 s in 2 min

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(125)));
    }

    [Fact]
    public void The_pause_rule_waits_for_a_window_that_has_been_watched_whole()
    {
        var (p, now) = Settled();
        p.AddGc(now.AddSeconds(5), Gc(800, pauseMs: 20_000));      // one long pause right after the grace

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(30)));
    }

    // --- full collections ---

    [Fact]
    public void A_full_collection_nobody_asked_for_is_pressure_for_five_minutes()
    {
        var (p, now) = Settled();
        p.AddGc(now, Gc(900, GcKind.Full, 900));

        Assert.Equal(MemoryPressureReason.FullGc, p.Evaluate(now.AddSeconds(1)));
        Assert.Equal(MemoryPressureReason.FullGc, p.Evaluate(now.AddMinutes(4)));
        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddMinutes(6)));
    }

    [Fact]
    public void A_full_collection_somebody_forced_says_nothing()
    {
        var (p, now) = Settled();
        p.AddGc(now, Gc(900, GcKind.Full, 900, forced: true));

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddSeconds(1)));
    }

    // --- out of memory ---

    [Fact]
    public void Running_out_of_memory_is_pressure_at_once_even_during_the_grace()
    {
        var p = new MemoryPressure();
        p.Ready(Start, Ceiling);
        p.OutOfMemory();

        Assert.Equal(MemoryPressureReason.OutOfMemory, p.Evaluate(Start.AddSeconds(10)));
    }

    [Fact]
    public void A_new_run_forgets_the_last_ones_trouble()
    {
        var (p, now) = Settled();
        p.OutOfMemory();
        p.AddGc(now, Gc(900, GcKind.Full));

        p.Ready(now.AddHours(1), Ceiling);

        Assert.Equal(MemoryPressureReason.None, p.Evaluate(now.AddHours(1).AddMinutes(10)));
    }
}
