using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading the GC log. Every line here was copied from a real run of the lab's 26.2 test server, so a
/// change in what a JVM prints shows up as a failure rather than as a card that quietly stays empty.
/// </summary>
public class GcLogLineTests
{
    private const long MB = 1024 * 1024;

    private static GcEvent Parse(string line)
    {
        Assert.True(GcLogLine.TryParse(line, out var e), line);
        return e;
    }

    [Fact]
    public void A_G1_young_pause_gives_what_is_left_and_what_is_held()
    {
        var e = Parse("[2.201s][info][gc          ] GC(1) Pause Young (Normal) (G1 Evacuation Pause) 75M->17M(130M) 7.405ms");

        Assert.Equal(GcKind.Young, e.Kind);
        Assert.Equal(1, e.Id);
        Assert.Equal(2.201, e.UptimeSeconds, 3);
        Assert.Equal(75 * MB, e.BeforeBytes);
        Assert.Equal(17 * MB, e.AfterBytes);
        Assert.Equal(130 * MB, e.CommittedBytes);
        Assert.Equal(7.405, e.PauseMs, 3);
        Assert.True(e.ReportsLiveHeap);
        Assert.False(e.Explicit);
        Assert.False(e.EvacuationFailure);
    }

    [Theory]
    [InlineData("[495.424s][info][gc          ] GC(720) Pause Young (Prepare Mixed) (G1 Periodic Collection) 482M->480M(692M) 14.307ms", GcKind.Young)]
    [InlineData("[2.867s][info][gc          ] GC(5) Pause Young (Concurrent Start) (Metadata GC Threshold) 45M->37M(130M) 6.662ms", GcKind.Young)]
    [InlineData("[510.441s][info][gc          ] GC(721) Pause Young (Mixed) (G1 Periodic Collection) 482M->427M(692M) 15.961ms", GcKind.Mixed)]
    [InlineData("[3.570s][info][gc          ] GC(1) Pause Remark 40M->40M(4096M) 1.962ms", GcKind.Remark)]
    [InlineData("[3.571s][info][gc          ] GC(1) Pause Cleanup 40M->40M(4096M) 0.041ms", GcKind.Cleanup)]
    [InlineData("[331.658s][info][gc             ] GC(793) Pause Young (Allocation Failure) 2473M->1718M(2934M) 219.408ms", GcKind.Young)]
    public void The_kind_follows_the_pause_name(string line, GcKind expected) =>
        Assert.Equal(expected, Parse(line).Kind);

    [Fact]
    public void Only_collections_that_empty_the_heap_say_what_is_alive()
    {
        Assert.False(Parse("[3.570s][info][gc          ] GC(1) Pause Remark 40M->40M(4096M) 1.962ms").ReportsLiveHeap);
        Assert.False(Parse("[3.571s][info][gc          ] GC(1) Pause Cleanup 40M->40M(4096M) 0.041ms").ReportsLiveHeap);
        Assert.True(Parse("[510.441s][info][gc          ] GC(721) Pause Young (Mixed) (G1 Periodic Collection) 482M->427M(692M) 15.961ms").ReportsLiveHeap);
    }

    [Fact]
    public void A_full_collection_is_one_and_so_is_one_somebody_forced()
    {
        var full = Parse("[40.1s][info][gc          ] GC(30) Pause Full (G1 Compaction Pause) 1800M->900M(2048M) 650.5ms");
        Assert.Equal(GcKind.Full, full.Kind);
        Assert.False(full.Explicit);

        var forced = Parse("[41.0s][info][gc          ] GC(31) Pause Full (System.gc()) 900M->310M(2048M) 120.0ms");
        Assert.Equal(GcKind.Full, forced.Kind);
        Assert.True(forced.Explicit);
    }

    [Fact]
    public void An_evacuation_failure_in_the_annotation_is_noticed()
    {
        var e = Parse("[12.0s][info][gc          ] GC(9) Pause Young (Normal) (G1 Evacuation Pause) (Evacuation Failure: Pinned) 90M->88M(130M) 40.2ms");
        Assert.True(e.EvacuationFailure);
    }

    [Fact]
    public void The_ordinary_G1_pause_name_is_not_a_failure()
    {
        Assert.False(Parse("[2.201s][info][gc          ] GC(1) Pause Young (Normal) (G1 Evacuation Pause) 75M->17M(130M) 7.405ms").EvacuationFailure);
    }

    [Theory]
    [InlineData("1024K->512K(2048K)", 1024L * 1024, 512L * 1024, 2048L * 1024)]
    [InlineData("1G->512M(2G)", 1024L * MB, 512 * MB, 2048L * MB)]
    public void Sizes_in_K_M_and_G_are_all_understood(string sizes, long before, long after, long committed)
    {
        var e = Parse($"[1.000s][info][gc          ] GC(0) Pause Young (Normal) (G1 Evacuation Pause) {sizes} 1.000ms");
        Assert.Equal(before, e.BeforeBytes);
        Assert.Equal(after, e.AfterBytes);
        Assert.Equal(committed, e.CommittedBytes);
    }

    [Fact]
    public void A_pause_in_seconds_is_read_in_milliseconds()
    {
        Assert.Equal(1500, Parse("[1.000s][info][gc          ] GC(0) Pause Full (Ergonomics) 100M->50M(200M) 1.5s").PauseMs, 3);
    }

    [Fact]
    public void A_comma_for_a_decimal_point_is_read_too()
    {
        var e = Parse("[2,201s][info][gc          ] GC(1) Pause Young (Normal) (G1 Evacuation Pause) 75M->17M(130M) 7,405ms");
        Assert.Equal(2.201, e.UptimeSeconds, 3);
        Assert.Equal(7.405, e.PauseMs, 3);
    }

    [Fact]
    public void A_ZGC_major_collection_gives_what_is_in_use_after_it()
    {
        var e = Parse("[5.272s][info][gc          ] GC(1) Major Collection (Warmup) 410M(10%)->230M(6%) 0.197s");
        Assert.Equal(GcKind.Major, e.Kind);
        Assert.Equal(230 * MB, e.AfterBytes);
        Assert.Equal(0, e.CommittedBytes);
        Assert.Equal(197, e.PauseMs, 3);
        Assert.True(e.ReportsLiveHeap);
    }

    [Theory]
    [InlineData("[5.076s][info][gc          ] GC(1) Major Collection (Warmup)")]
    [InlineData("[334.660s][info][gc          ] GC(90) Minor Collection (Allocation Rate)")]
    [InlineData("[3.098s][info][gc          ] GC(2) Concurrent Mark Cycle")]
    [InlineData("[3.117s][info][gc          ] GC(2) Concurrent Mark Cycle 18.098ms")]
    [InlineData("[3.116s][info][gc,marking  ] GC(2) Concurrent Cleanup for Next Mark 5.964ms")]
    [InlineData("[0.011s][info][gc          ] Using G1")]
    [InlineData("[Server thread/INFO]: Done (3.2s)! For help, type \"help\"")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_line_is_not_an_event(string? line) =>
        Assert.False(GcLogLine.TryParse(line, out _));

    [Theory]
    [InlineData("[0.011s][info][gc     ] Using G1", GcCollector.G1)]
    [InlineData("[0.011s][info][gc     ] Using Serial", GcCollector.Serial)]
    [InlineData("[0.011s][info][gc     ] Using Parallel", GcCollector.Parallel)]
    [InlineData("[0.016s][info][gc     ] Using Shenandoah", GcCollector.Shenandoah)]
    [InlineData("[0.022s][info][gc     ] Using The Z Garbage Collector", GcCollector.Z)]
    [InlineData("[0.022s][info][gc     ] Using Epsilon", GcCollector.Other)]
    public void The_collector_is_read_from_the_first_line(string line, GcCollector expected)
    {
        Assert.True(GcLogLine.TryParseCollector(line, out var collector));
        Assert.Equal(expected, collector);
    }

    [Fact]
    public void A_line_that_is_not_the_collector_is_not_one()
    {
        Assert.False(GcLogLine.TryParseCollector("[0.012s][info][gc,init] Version: 25.0.3+9-LTS (release)", out _));
        Assert.False(GcLogLine.TryParseCollector("[0.015s][info][gc     ] Heuristics ergonomically sets -XX:GCTimeRatio=70", out _));
    }
}
