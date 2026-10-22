using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Choosing the command that asks a server for its speed, keeping its answer out of the console, and
/// moving on to the next command when one does not work.
/// </summary>
public class PerformanceProbeTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private const string Fabric = "[20:41:58] [spark-worker-pool-1-thread-1/INFO]: ";
    private const string Vanilla = "[20:42:03] [Server thread/INFO]: ";

    private static readonly string[] SparkAnswer =
    [
        Fabric + "[⚡] TPS from last 5s, 10s, 1m, 5m, 15m:",
        Fabric + "[⚡]  *20.0, *20.0, 19.2, *20.0, *20.0",
        Fabric + "[⚡] ",
        Fabric + "[⚡] Tick durations (min/med/95%ile/max ms) from last 10s, 1m:",
        Fabric + "[⚡]  0.1/0.3/0.4/14.8;  0.1/42.5/60.7/138.9",
        Fabric + "[⚡] ",
        Fabric + "[⚡] CPU usage from last 10s, 1m, 15m:",
        Fabric + "[⚡]  12%, 13%, 13%  (system)",
        Fabric + "[⚡]  0%, 1%, 1%  (process)"
    ];

    private static readonly string[] TickQueryAnswer =
    [
        Vanilla + "The game is running normally",
        Vanilla + "Target tick rate: 20.0 per second.",
        "Average time per tick: 0.4ms (Target: 50.0ms)",
        Vanilla + "Percentiles: P50: 0.3ms P95: 0.4ms P99: 14.8ms. Sample: 100"
    ];

    // --- the choice ---

    [Theory]
    [InlineData(ServerType.Fabric, "1.21.1", false, new[] { ProbeKind.TickQuery })]
    [InlineData(ServerType.Fabric, "26.2", true, new[] { ProbeKind.Spark, ProbeKind.TickQuery })]
    [InlineData(ServerType.Vanilla, "1.20.3", false, new[] { ProbeKind.TickQuery })]
    [InlineData(ServerType.Vanilla, "1.20.2", false, new ProbeKind[0])]
    [InlineData(ServerType.Paper, "1.21.4", false, new[] { ProbeKind.Spark, ProbeKind.TickQuery, ProbeKind.PaperTps })]
    [InlineData(ServerType.Purpur, "26.2", false, new[] { ProbeKind.Spark, ProbeKind.TickQuery, ProbeKind.PaperTps })]
    [InlineData(ServerType.Paper, "1.20.1", false, new[] { ProbeKind.PaperTps })]
    [InlineData(ServerType.Paper, "1.20.1", true, new[] { ProbeKind.Spark, ProbeKind.PaperTps })]
    [InlineData(ServerType.Forge, "1.12.2", true, new[] { ProbeKind.Spark })]
    [InlineData(ServerType.Forge, "1.12.2", false, new ProbeKind[0])]
    public void The_best_command_comes_first_and_only_those_that_can_work_are_offered(
        ServerType type, string version, bool spark, ProbeKind[] expected) =>
        Assert.Equal(expected, PerformanceProbe.Candidates(type, version, spark));

    [Theory]
    [InlineData("1.20.3", 1, 20, 3, true)]
    [InlineData("1.20.2", 1, 20, 3, false)]
    [InlineData("1.21", 1, 20, 3, true)]
    [InlineData("1.20", 1, 20, 3, false)]
    [InlineData("26.2", 1, 21, 0, true)]     // versions from 26 on dropped the leading 1
    [InlineData("26.1.2", 1, 20, 3, true)]
    [InlineData("1.7.10", 1, 20, 3, false)]
    [InlineData("", 1, 20, 3, false)]
    [InlineData(null, 1, 20, 3, false)]
    [InlineData("snapshot", 1, 20, 3, false)]
    public void Versions_are_compared_as_numbers(string? version, int major, int minor, int patch, bool expected) =>
        Assert.Equal(expected, PerformanceProbe.VersionAtLeast(version, major, minor, patch));

    [Theory]
    [InlineData(ProbeKind.Spark, "spark tps")]
    [InlineData(ProbeKind.TickQuery, "tick query")]
    [InlineData(ProbeKind.PaperTps, "tps")]
    public void Each_kind_types_its_own_command(ProbeKind kind, string command) =>
        Assert.Equal(command, PerformanceProbe.Command(kind));

    // --- what it swallows ---

    [Fact]
    public void All_of_sparks_answer_is_swallowed_and_gives_a_reading_from_the_last_minute()
    {
        var probe = new PerformanceProbe([ProbeKind.Spark]);
        Assert.Equal("spark tps", probe.Ask(T0));

        TickReading? reading = null;
        foreach (var line in SparkAnswer)
        {
            Assert.True(probe.TryConsume(line, T0.AddSeconds(1), out var r), line);
            reading ??= r;
        }

        Assert.NotNull(reading);
        Assert.Equal(TickSource.Spark, reading!.Source);
        Assert.Equal(19.2, reading.Tps);     // the one-minute figure
        Assert.Equal(42.5, reading.Mspt);    // the median of the last minute
        Assert.Equal(60.7, reading.Mspt95);
    }

    [Fact]
    public void All_of_tick_querys_answer_is_swallowed_and_gives_a_reading()
    {
        var probe = new PerformanceProbe([ProbeKind.TickQuery]);
        probe.Ask(T0);

        TickReading? reading = null;
        foreach (var line in TickQueryAnswer)
        {
            Assert.True(probe.TryConsume(line, T0.AddSeconds(1), out var r), line);
            reading ??= r;
        }

        Assert.Equal(TickSource.TickQuery, reading!.Source);
        Assert.Equal(20.0, reading.Tps);
        Assert.Equal(0.4, reading.Mspt);
    }

    [Fact]
    public void A_slow_average_lowers_the_rate_below_the_target()
    {
        var probe = new PerformanceProbe([ProbeKind.TickQuery]);
        probe.Ask(T0);

        probe.TryConsume("Average time per tick: 80.0ms (Target: 50.0ms)", T0.AddSeconds(1), out var reading);

        Assert.Equal(12.5, reading!.Tps);
    }

    [Fact]
    public void Papers_tps_is_swallowed_and_gives_a_reading()
    {
        var probe = new PerformanceProbe([ProbeKind.PaperTps]);
        probe.Ask(T0);

        Assert.True(probe.TryConsume("[12:00:00 INFO]: TPS from last 1m, 5m, 15m: 18.5, 19.0, 19.9", T0.AddSeconds(1), out var r));

        Assert.Equal(18.5, r!.Tps);
        Assert.Null(r.Mspt);
    }

    [Fact]
    public void Lines_that_are_not_part_of_the_answer_are_left_alone()
    {
        var probe = new PerformanceProbe([ProbeKind.Spark]);
        probe.Ask(T0);

        Assert.False(probe.TryConsume("[12:00:01] [Server thread/INFO]: <Bob> hello", T0.AddSeconds(1), out _));
        Assert.False(probe.TryConsume("[12:00:01] [Server thread/INFO]: Alice joined the game", T0.AddSeconds(1), out _));
        // Tick query's answer is not what this probe asked for.
        Assert.False(probe.TryConsume(Vanilla + "The game is running normally", T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Nothing_is_swallowed_before_asking_or_after_the_window()
    {
        var probe = new PerformanceProbe([ProbeKind.Spark]);
        Assert.False(probe.TryConsume(SparkAnswer[0], T0, out _));   // not asked: the owner's own spark tps

        probe.Ask(T0);
        Assert.False(probe.TryConsume(SparkAnswer[0], T0 + PerformanceProbe.Window + TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void Something_the_owner_types_disarms_the_probe_so_they_see_the_answer()
    {
        var probe = new PerformanceProbe([ProbeKind.Spark]);
        probe.Ask(T0);

        probe.Disarm();

        Assert.False(probe.Armed);
        Assert.False(probe.TryConsume(SparkAnswer[0], T0.AddSeconds(1), out _));
    }

    [Fact]
    public void A_player_cannot_make_the_probe_swallow_their_chat_or_fake_a_reading()
    {
        var probe = new PerformanceProbe([ProbeKind.TickQuery]);
        probe.Ask(T0);

        Assert.False(probe.TryConsume(Vanilla + "<Bob> Average time per tick: 99.0ms", T0.AddSeconds(1), out var r));
        Assert.Null(r);
    }

    // --- falling back ---

    [Fact]
    public void An_unknown_command_moves_on_to_the_next_one_and_its_error_is_swallowed()
    {
        var probe = new PerformanceProbe([ProbeKind.Spark, ProbeKind.TickQuery]);
        probe.Ask(T0);

        Assert.True(probe.TryConsume(Vanilla + "Unknown or incomplete command. See below for error", T0.AddSeconds(1), out _));
        Assert.True(probe.TryConsume(Vanilla + "spark<--[HERE]", T0.AddSeconds(1), out _));

        Assert.Equal(ProbeKind.TickQuery, probe.Current);
        Assert.Equal("tick query", probe.Ask(T0.AddSeconds(30)));
    }

    [Fact]
    public void Three_asks_without_an_answer_move_on_to_the_next_command()
    {
        var probe = new PerformanceProbe([ProbeKind.Spark, ProbeKind.TickQuery]);

        probe.Ask(T0);
        probe.Ask(T0.AddSeconds(30));
        Assert.Equal(ProbeKind.Spark, probe.Current);
        probe.Ask(T0.AddSeconds(60));
        Assert.Equal(ProbeKind.Spark, probe.Current);

        Assert.Equal("tick query", probe.Ask(T0.AddSeconds(90)));
    }

    [Fact]
    public void An_answer_resets_the_count_of_misses()
    {
        var probe = new PerformanceProbe([ProbeKind.PaperTps, ProbeKind.TickQuery]);
        probe.Ask(T0);
        probe.Ask(T0.AddSeconds(30));
        probe.TryConsume("[12:00:00 INFO]: TPS from last 1m, 5m, 15m: 20.0, 20.0, 20.0", T0.AddSeconds(31), out _);
        probe.Ask(T0.AddSeconds(60));
        probe.Ask(T0.AddSeconds(90));

        Assert.Equal(ProbeKind.PaperTps, probe.Current);
    }

    [Fact]
    public void With_nothing_left_to_try_nothing_is_asked()
    {
        var probe = new PerformanceProbe([]);

        Assert.Null(probe.Current);
        Assert.Null(probe.Ask(T0));
    }

    [Fact]
    public void Running_out_of_commands_ends_the_asking()
    {
        var probe = new PerformanceProbe([ProbeKind.PaperTps]);
        probe.Ask(T0);
        probe.TryConsume("[12:00:00 INFO]: Unknown command. Type \"/help\" for help.", T0.AddSeconds(1), out _);

        Assert.Null(probe.Current);
        Assert.Null(probe.Ask(T0.AddSeconds(30)));
    }
}
