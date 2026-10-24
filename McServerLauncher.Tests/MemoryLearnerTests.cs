using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What a server's own history says its next ceiling should be. The figures follow the lab's runs
/// (2026-10-21/22): four players exploring new terrain kept 1.1-1.6 GB alive on Fabric and Paper, and
/// the test server ran out of memory at 768 MB.
/// </summary>
public class MemoryLearnerTests
{
    private const long Floor = 1536;
    private const long Estimate = 4096;

    private static MemorySession Run(long peakLiveMb, long ceilingMb = 4096, int minutes = 45, int players = 4,
        bool oom = false, bool pressured = false, MemoryMode mode = MemoryMode.Automatic) =>
        new()
        {
            Mode = mode, CeilingMb = ceilingMb, PeakLiveMb = peakLiveMb, LiveSamples = 200,
            DurationSeconds = minutes * 60, PeakPlayers = players, OutOfMemory = oom, Pressured = pressured
        };

    private static LearnedCeiling? Next(params MemorySession[] runs) => MemoryLearner.Next(runs, Floor, Estimate);

    // --- which runs count ---

    [Fact]
    public void A_run_that_was_long_enough_with_players_and_room_to_spare_counts()
    {
        Assert.True(MemoryLearner.Counts(Run(1200)));
    }

    [Theory]
    [InlineData(19, 4, false)]   // too short
    [InlineData(45, 0, false)]   // nobody was on
    [InlineData(20, 1, true)]    // the shortest that counts
    public void A_run_needs_twenty_minutes_and_a_player(int minutes, int players, bool counts) =>
        Assert.Equal(counts, MemoryLearner.Counts(Run(1200, minutes: minutes, players: players)));

    [Fact]
    public void A_run_with_no_collections_says_nothing()
    {
        Assert.False(MemoryLearner.Counts(Run(0) with { LiveSamples = 0 }));
    }

    [Fact]
    public void A_run_that_reached_85_percent_of_its_ceiling_was_held_back_and_does_not_count()
    {
        var held = Run(peakLiveMb: 3500, ceilingMb: 4096);   // 85.4 %
        Assert.True(MemoryLearner.WasLimited(held));
        Assert.False(MemoryLearner.Counts(held));

        Assert.False(MemoryLearner.WasLimited(Run(peakLiveMb: 3400, ceilingMb: 4096)));   // 83 %
    }

    [Fact]
    public void Running_out_of_memory_or_being_found_short_is_being_limited_whatever_the_peak()
    {
        Assert.True(MemoryLearner.WasLimited(Run(500, oom: true)));
        Assert.True(MemoryLearner.WasLimited(Run(500, pressured: true)));
    }

    // --- nothing yet ---

    [Fact]
    public void With_no_history_the_estimate_stands()
    {
        Assert.Null(Next());
    }

    [Fact]
    public void Runs_that_do_not_count_teach_nothing()
    {
        Assert.Null(Next(Run(1200, minutes: 5), Run(1300, players: 0)));
    }

    [Fact]
    public void Manual_runs_are_not_learned_from_because_their_ceiling_was_the_owners()
    {
        Assert.Null(Next(Run(1200, mode: MemoryMode.Manual), Run(1200, mode: MemoryMode.Manual)));
    }

    // --- going down ---

    [Fact]
    public void One_or_two_good_runs_do_not_lower_the_ceiling()
    {
        Assert.Null(Next(Run(1200)));
        Assert.Null(Next(Run(1200), Run(1300)));
    }

    [Fact]
    public void Three_runs_lower_it_by_at_most_a_gigabyte()
    {
        // 1.6 x 1.3 GB = 2.1 GB -> 2.5 GB wanted, but 4 GB goes to 3 GB first.
        var next = Next(Run(1200), Run(1250), Run(1300));

        Assert.Equal(3072, next!.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Learned, next.Reason);
    }

    [Fact]
    public void Four_runs_reach_the_target_by_two_steps_as_in_the_lab()
    {
        var third = Next(Run(1200), Run(1250), Run(1300))!;
        var fourth = Next(Run(1200), Run(1250), Run(1300), Run(1280, ceilingMb: third.CeilingMb))!;

        // 1.6 x 1.3 GB = 2080 MB -> 2560 MB, which is 2.5 GB.
        Assert.Equal(2560, fourth.CeilingMb);
    }

    [Fact]
    public void The_ceiling_never_goes_below_the_floor()
    {
        var next = Next(Run(300), Run(320), Run(310));

        Assert.Equal(3072, next!.CeilingMb);                      // one step down from 4 GB...
        var later = Next(Run(300), Run(320), Run(310), Run(310, ceilingMb: 3072), Run(300, ceilingMb: 2560),
            Run(300, ceilingMb: 2048));
        Assert.Equal(Floor, later!.CeilingMb);                    // ...and never under the floor
    }

    [Fact]
    public void The_target_is_rounded_up_to_half_a_gigabyte()
    {
        // 1.6 x 1000 = 1600 -> 2048.
        var next = Next(Run(1000, ceilingMb: 2560), Run(1000, ceilingMb: 2560), Run(1000, ceilingMb: 2560));

        Assert.Equal(2048, next!.CeilingMb);
    }

    [Fact]
    public void Only_the_latest_ten_runs_count()
    {
        var old = Enumerable.Range(0, 5).Select(_ => Run(3000, ceilingMb: 8192)).ToList();    // a heavy past
        var recent = Enumerable.Range(0, 10).Select(_ => Run(900, ceilingMb: 8192)).ToList();

        var next = MemoryLearner.Next([.. old, .. recent], Floor, Estimate);

        // 1.6 x 900 = 1440 -> 1536 wanted; 8 GB goes down one step only.
        Assert.Equal(7168, next!.CeilingMb);
    }

    // --- going up ---

    [Fact]
    public void A_heavier_run_raises_the_ceiling_at_once()
    {
        // 1.6 x 3.1 GB = 4960 MB -> 5120 MB, from a previous 4096.
        var next = Next(Run(3100, ceilingMb: 8192, players: 6) with { CeilingMb = 4096 });

        Assert.Equal(5120, next!.CeilingMb);
    }

    [Fact]
    public void The_95th_percentile_of_ten_runs_is_the_highest_when_there_are_ten()
    {
        var runs = Enumerable.Range(0, 9).Select(_ => Run(1000)).Append(Run(2500, ceilingMb: 8192) with { CeilingMb = 4096 }).ToArray();

        // 1.6 x 2500 = 4000 -> 4096 (which is where it already was).
        Assert.Equal(4096, Next(runs)!.CeilingMb);
    }

    [Fact]
    public void Running_out_of_memory_raises_it_by_half_whatever_the_rest_of_the_history_says()
    {
        var next = Next(Run(1000), Run(1000), Run(1000), Run(900, ceilingMb: 3072, oom: true));

        Assert.Equal(4608, next!.CeilingMb);                       // 3072 x 1.5
        Assert.Equal(MemoryCeilingReason.RaisedAfterPressure, next.Reason);
    }

    [Theory]
    [InlineData(1536, 2560)]    // +1 GB beats x1.25
    [InlineData(4096, 5120)]    // +1 GB = x1.25 here
    [InlineData(8192, 10240)]   // x1.25 beats +1 GB
    public void Other_pressure_raises_it_by_a_gigabyte_or_a_quarter_whichever_is_more(long ceiling, long expected)
    {
        var next = Next(Run(500, ceilingMb: ceiling, pressured: true));

        Assert.Equal(expected, next!.CeilingMb);
        Assert.Equal(MemoryCeilingReason.RaisedAfterPressure, next.Reason);
    }

    [Fact]
    public void A_run_held_back_by_its_ceiling_is_raised_like_any_other_pressure()
    {
        var next = Next(Run(peakLiveMb: 3900, ceilingMb: 4096));

        Assert.Equal(5120, next!.CeilingMb);
    }

    [Fact]
    public void The_restart_after_a_crash_gets_more_even_though_the_run_was_short()
    {
        // The run that crashed lasted 40 seconds; it still decides the next start.
        var crashed = Run(900, ceilingMb: 2048, minutes: 1, players: 0, oom: true) with { End = SessionEnd.Crashed };

        Assert.Equal(3072, Next(crashed)!.CeilingMb);
    }

    [Fact]
    public void After_a_raise_that_worked_the_ceiling_is_learned_again_from_the_good_runs()
    {
        var next = Next(Run(900, ceilingMb: 2048, oom: true), Run(1500, ceilingMb: 3072));

        // The last run was fine: 1.6 x 1.5 GB = 2400 -> 2560, below 3072 but only one run counts.
        Assert.Equal(3072, next!.CeilingMb);
    }

    // --- the band's promise ---

    [Theory]
    [InlineData(4096, false, 5120)]
    [InlineData(4096, true, 6144)]
    [InlineData(1536, false, 2560)]
    [InlineData(2000, true, 3072)]
    public void The_band_tells_what_the_server_will_have_after_a_restart(long ceiling, bool oom, long expected) =>
        Assert.Equal(expected, MemoryLearner.AfterPressure(ceiling, oom));
}
