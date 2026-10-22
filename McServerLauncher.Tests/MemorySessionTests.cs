using System.IO;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What a run's measurements become: the peak and the p95 of what stayed alive, the time spent
/// collecting, and the runs the history keeps.
/// </summary>
public sealed class MemorySessionTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private static readonly DateTime Start = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "memhist-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = MemoryHistoryStore.RootDirectory;

    public MemorySessionTests() => MemoryHistoryStore.RootDirectory = _root;

    public void Dispose()
    {
        MemoryHistoryStore.RootDirectory = _previousRoot;
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static MemorySessionTracker NewTracker() =>
        new(new MemorySessionStart(Start, MemoryMode.Automatic, 4096, 25, 12, 10, 8));

    private static GcEvent Gc(long afterMb, long committedMb = 1000, GcKind kind = GcKind.Young,
        double pauseMs = 10, bool forced = false, bool failure = false) =>
        new(1, 0, kind, afterMb * MB * 2, afterMb * MB, committedMb * MB, pauseMs, forced, failure);

    [Fact]
    public void Peak_and_p95_come_from_what_was_left_after_each_collection()
    {
        var t = NewTracker();
        for (var mb = 1; mb <= 100; mb++) t.Add(Gc(mb));

        var s = t.Finish(Start.AddMinutes(10), SessionEnd.Stopped);

        Assert.Equal(100, s.PeakLiveMb);
        Assert.Equal(95, s.P95LiveMb);
        Assert.Equal(100, s.LiveSamples);
    }

    [Fact]
    public void One_spike_among_many_does_not_set_the_p95()
    {
        var t = NewTracker();
        for (var i = 0; i < 99; i++) t.Add(Gc(400));
        t.Add(Gc(3000));

        var s = t.Finish(Start.AddMinutes(10), SessionEnd.Stopped);

        Assert.Equal(3000, s.PeakLiveMb);
        Assert.Equal(400, s.P95LiveMb);
    }

    [Fact]
    public void Pauses_that_say_nothing_about_the_heap_only_cost_time()
    {
        var t = NewTracker();
        t.Add(Gc(300));
        t.Add(Gc(9000, kind: GcKind.Remark, pauseMs: 50));
        t.Add(Gc(9000, kind: GcKind.Cleanup, pauseMs: 50));

        var s = t.Finish(Start.AddMinutes(1), SessionEnd.Stopped);

        Assert.Equal(300, s.PeakLiveMb);
        Assert.Equal(1, s.LiveSamples);
        Assert.Equal(3, s.GcCount);
    }

    [Fact]
    public void A_collection_somebody_forced_says_nothing_about_what_the_server_needs()
    {
        var t = NewTracker();
        t.Add(Gc(300));
        t.Add(Gc(2500, kind: GcKind.Full, forced: true));

        var s = t.Finish(Start.AddMinutes(1), SessionEnd.Stopped);

        Assert.Equal(300, s.PeakLiveMb);
        Assert.Equal(1, s.ExplicitGcs);
        Assert.Equal(1, s.FullGcs);
    }

    [Fact]
    public void Time_in_collection_is_a_share_of_the_run()
    {
        var t = NewTracker();
        t.Add(Gc(300, pauseMs: 600));
        t.Add(Gc(300, pauseMs: 600));

        var s = t.Finish(Start.AddSeconds(120), SessionEnd.Stopped);

        Assert.Equal(1.0, s.GcTimePercent, 2);
        Assert.Equal(120, s.DurationSeconds);
    }

    [Fact]
    public void The_most_committed_and_the_most_private_are_remembered_and_the_latest_is_shown()
    {
        var t = NewTracker();
        t.Add(Gc(300, committedMb: 900));
        t.Add(Gc(200, committedMb: 600));
        t.AddSample(1500, players: 2);
        t.AddSample(1100, players: 4);
        t.AddSample(1000, players: 1);

        Assert.Equal(200, t.LiveMb);
        Assert.Equal(600, t.CommittedMb);

        var s = t.Finish(Start.AddMinutes(5), SessionEnd.Stopped);
        Assert.Equal(900, s.PeakCommittedMb);
        Assert.Equal(1500, s.PeakPrivateMb);
        Assert.Equal(4, s.PeakPlayers);
    }

    [Fact]
    public void What_the_run_started_with_is_kept_with_it()
    {
        var t = NewTracker();
        t.NoteCollector(GcCollector.G1);
        t.Add(Gc(300, failure: true));

        var s = t.Finish(Start.AddMinutes(1), SessionEnd.Crashed);

        Assert.Equal(Start, s.StartedUtc);
        Assert.Equal(MemoryMode.Automatic, s.Mode);
        Assert.Equal(4096, s.CeilingMb);
        Assert.Equal(25, s.JavaMajor);
        Assert.Equal(12, s.ModCount);
        Assert.Equal(10, s.ViewDistance);
        Assert.Equal(8, s.SimulationDistance);
        Assert.Equal(GcCollector.G1, s.Collector);
        Assert.True(s.EvacuationFailures);
        Assert.Equal(SessionEnd.Crashed, s.End);
    }

    [Fact]
    public void Running_out_of_memory_is_enough_to_be_worth_keeping()
    {
        var t = NewTracker();
        Assert.False(t.HasData);

        t.NoteOutOfMemory();

        Assert.True(t.HasData);
        Assert.True(t.Finish(Start.AddSeconds(30), SessionEnd.Crashed).OutOfMemory);
    }

    [Fact]
    public void A_run_with_nothing_measured_is_not_worth_keeping()
    {
        Assert.False(NewTracker().HasData);
    }

    [Fact]
    public void A_run_with_no_collections_has_zeros_rather_than_an_error()
    {
        var s = NewTracker().Finish(Start, SessionEnd.Stopped);
        Assert.Equal(0, s.PeakLiveMb);
        Assert.Equal(0, s.P95LiveMb);
        Assert.Equal(0, s.GcTimePercent);
    }

    // --- the store ---

    [Fact]
    public void A_stored_run_comes_back_after_the_store_is_opened_again()
    {
        var t = NewTracker();
        t.NoteCollector(GcCollector.G1);
        t.Add(Gc(420));
        var session = t.Finish(Start.AddMinutes(8), SessionEnd.Stopped);

        new MemoryHistoryStore(Path.Combine(_root, "srv")).Add(session);
        var back = new MemoryHistoryStore(Path.Combine(_root, "srv")).Sessions;

        Assert.Equal([session], back);
    }

    [Fact]
    public void Only_the_latest_runs_are_kept()
    {
        var store = new MemoryHistoryStore(Path.Combine(_root, "srv"));
        for (var i = 1; i <= MemoryHistoryStore.MaxSessions + 5; i++)
            store.Add(new MemorySession { PeakLiveMb = i });

        var kept = store.Sessions;

        Assert.Equal(MemoryHistoryStore.MaxSessions, kept.Count);
        Assert.Equal(6, kept[0].PeakLiveMb);
        Assert.Equal(MemoryHistoryStore.MaxSessions + 5, kept[^1].PeakLiveMb);
    }

    [Fact]
    public void A_file_written_before_a_field_existed_reads_with_its_default()
    {
        var dir = Path.Combine(_root, "old");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "history.json"),
            """{"Version":1,"Sessions":[{"PeakLiveMb":700,"CeilingMb":4096}]}""");

        var s = Assert.Single(new MemoryHistoryStore(dir).Sessions);

        Assert.Equal(700, s.PeakLiveMb);
        Assert.False(s.OutOfMemory);
        Assert.Equal(SessionEnd.Stopped, s.End);
    }

    [Fact]
    public void A_corrupt_file_is_an_empty_history()
    {
        var dir = Path.Combine(_root, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "history.json"), "{ not json");

        Assert.Empty(new MemoryHistoryStore(dir).Sessions);
    }

    [Fact]
    public void The_history_of_a_removed_server_is_deleted_and_the_rest_is_kept()
    {
        MemoryHistoryStore.For("kept").Add(new MemorySession { PeakLiveMb = 1 });
        MemoryHistoryStore.For("gone").Add(new MemorySession { PeakLiveMb = 2 });

        MemoryHistoryStore.PruneOrphans(["kept"]);

        Assert.True(Directory.Exists(Path.Combine(_root, "kept")));
        Assert.False(Directory.Exists(Path.Combine(_root, "gone")));
    }
}
