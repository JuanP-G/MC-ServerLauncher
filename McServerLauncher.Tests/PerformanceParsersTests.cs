using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading what a server says about its speed. The lines are copied from the output of real servers
/// (Fabric with spark 1.10 and Paper 26.2, Minecraft 26.2, captured 2026-10-22), including what
/// Windows and a Spanish locale do to them.
/// </summary>
public class PerformanceParsersTests
{
    private const string Esc = "\x1b";
    private const string Fabric = "[20:41:58] [spark-worker-pool-1-thread-1/INFO]: ";
    private const string Vanilla = "[20:42:03] [Server thread/INFO]: ";
    private const string PaperPrefix = "[20:41:53 INFO]: ";

    private static PerfLine Parse(string line) => PerformanceParsers.Parse(line);

    // --- lag warnings ---

    [Theory]
    [InlineData("[07:58:05] [Server thread/WARN]: Can't keep up! Is the server overloaded? Running 5029ms or 100 ticks behind", 5029)]
    [InlineData("[07:50:31] [Server thread/WARN]: Can't keep up! Is the server overloaded? Running 30527116ms or 610542 ticks behind", 30527116)]
    [InlineData("[12:00:00 WARN]: Can't keep up! Is the server overloaded? Running 2300ms behind, skipping 46 tick(s)", 2300)]
    public void A_lag_warning_gives_how_far_behind_the_server_is(string line, double ms)
    {
        var parsed = Parse(line);
        Assert.Equal(PerfLineKind.LagWarning, parsed.Kind);
        Assert.Equal(ms, parsed.Numbers[0]);
    }

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: <Bob> Can't keep up! Is the server overloaded? Running 5000ms or 100 ticks behind")]
    [InlineData("[12:00:00] [Server thread/INFO]: [Not Secure] <Bob> Can't keep up! Is the server overloaded? Running 5000ms or 100 ticks behind")]
    [InlineData("[12:00:00 INFO]: <Bob> Can't keep up! Is the server overloaded? Running 5000ms or 100 ticks behind")]
    [InlineData("[12:00:00] [Server thread/INFO]: [Server] Can't keep up! Is the server overloaded? Running 5000ms or 100 ticks behind")]
    public void The_same_words_typed_in_the_chat_are_not_a_warning(string line) =>
        Assert.Equal(PerfLineKind.None, Parse(line).Kind);

    // --- spark, as Fabric prints it ---

    [Fact]
    public void Sparks_TPS_lines_are_read_with_the_symbol_intact()
    {
        Assert.Equal(PerfLineKind.SparkTpsHeader, Parse(Fabric + "[⚡] TPS from last 5s, 10s, 1m, 5m, 15m:").Kind);

        var values = Parse(Fabric + "[⚡]  *20.0, *20.0, *20.0, *20.0, *20.0");
        Assert.Equal(PerfLineKind.SparkTpsValues, values.Kind);
        Assert.Equal([20.0, 20, 20, 20, 20], values.Numbers);
    }

    [Fact]
    public void Sparks_figures_may_have_two_decimals_when_the_server_is_not_exactly_at_20()
    {
        // Copied from a real run: the one-decimal shape of an idle server was not the only one.
        var values = Parse(Fabric + "[?]  19.97, 19.99, *20.0, *20.0, *20.0");
        Assert.Equal(PerfLineKind.SparkTpsValues, values.Kind);
        Assert.Equal([19.97, 19.99, 20.0, 20.0, 20.0], values.Numbers);

        var ticks = Parse(Fabric + "[?]  0.12/0.31/0.45/14.83;  0.10/0.30/0.68/38.91");
        Assert.Equal(PerfLineKind.SparkTickValues, ticks.Kind);
        Assert.Equal(8, ticks.Numbers.Length);
    }

    [Theory]
    [InlineData("[?] TPS from last 5s, 10s, 1m, 5m, 15m:")]        // Java could not write the symbol
    [InlineData("[âš¡] TPS from last 5s, 10s, 1m, 5m, 15m:")]  // its bytes read in the wrong code page
    public void The_symbol_may_arrive_mangled(string message) =>
        Assert.Equal(PerfLineKind.SparkTpsHeader, Parse(Fabric + message).Kind);

    [Fact]
    public void Sparks_tick_durations_give_ten_seconds_then_a_minute()
    {
        Assert.Equal(PerfLineKind.SparkTickHeader,
            Parse(Fabric + "[⚡] Tick durations (min/med/95%ile/max ms) from last 10s, 1m:").Kind);

        var values = Parse(Fabric + "[⚡]  0.1/0.3/0.4/14.8;  0.1/0.3/0.7/38.9");
        Assert.Equal(PerfLineKind.SparkTickValues, values.Kind);
        Assert.Equal([0.1, 0.3, 0.4, 14.8, 0.1, 0.3, 0.7, 38.9], values.Numbers);
    }

    [Theory]
    [InlineData("[⚡] ")]
    [InlineData("[⚡] CPU usage from last 10s, 1m, 15m:")]
    [InlineData("[⚡]  12%, 13%, 13%  (system)")]
    [InlineData("[⚡]  0%, 1%, 1%  (process)")]
    public void The_rest_of_sparks_answer_is_recognised_so_it_can_be_kept_out_of_the_console(string message) =>
        Assert.Equal(PerfLineKind.SparkOther, Parse(Fabric + message).Kind);

    [Fact]
    public void A_figure_above_the_target_keeps_its_value_without_the_asterisk()
    {
        var values = Parse(Fabric + "[⚡]  *20.0, 19.5, 18.2, 17.0, 20.0");
        Assert.Equal([20.0, 19.5, 18.2, 17.0, 20.0], values.Numbers);
    }

    [Fact]
    public void Another_plugins_symbol_tag_is_not_sparks()
    {
        Assert.Equal(PerfLineKind.None, Parse(Fabric + "[★] Welcome back!").Kind);
        Assert.Equal(PerfLineKind.None, Parse(Fabric + "[Rank] TPS from last 5s, 10s, 1m, 5m, 15m:").Kind);
    }

    // --- spark, as Paper prints it: colours inside the line ---

    [Fact]
    public void Paper_colours_inside_a_line_are_removed_first()
    {
        var header = PaperPrefix + Esc + "[38;5;7m" + Esc + "[38;5;8m[" + Esc + "[1m" + Esc + "[38;5;11m?" + Esc
                     + "[0m" + Esc + "[38;5;8m]" + Esc + "[38;5;7m TPS from last 5s, 10s, 1m, 5m, 15m:" + Esc + "[0m";
        Assert.Equal(PerfLineKind.SparkTpsHeader, Parse(header).Kind);

        var values = PaperPrefix + Esc + "[38;5;7m" + Esc + "[38;5;8m[" + Esc + "[1m" + Esc + "[38;5;11m?" + Esc
                     + "[0m" + Esc + "[38;5;8m]" + Esc + "[38;5;7m  " + Esc + "[38;5;10m*20.0" + Esc + "[38;5;7m, "
                     + Esc + "[38;5;10m*20.0" + Esc + "[38;5;7m, " + Esc + "[38;5;10m19.9" + Esc + "[38;5;7m, "
                     + Esc + "[38;5;10m*20.0" + Esc + "[38;5;7m, " + Esc + "[38;5;10m*20.0" + Esc + "[0m";
        var parsed = Parse(values);
        Assert.Equal(PerfLineKind.SparkTpsValues, parsed.Kind);
        Assert.Equal(19.9, parsed.Numbers[2]);
    }

    [Fact]
    public void Section_sign_colour_codes_are_removed_too()
    {
        Assert.Equal(PerfLineKind.SparkTpsHeader,
            Parse(Fabric + "§7[§e?§7] TPS from last 5s, 10s, 1m, 5m, 15m:").Kind);
    }

    // --- tick query, as vanilla prints it ---

    [Fact]
    public void Tick_query_has_a_prefixed_first_line_and_unprefixed_ones_after_it()
    {
        Assert.Equal(PerfLineKind.TickQueryState, Parse(Vanilla + "The game is running normally").Kind);
        Assert.Equal(PerfLineKind.TickQueryTarget, Parse(Vanilla + "Target tick rate: 20.0 per second.").Kind);

        var average = Parse("Average time per tick: 0.4ms (Target: 50.0ms)");
        Assert.Equal(PerfLineKind.TickQueryAverage, average.Kind);
        Assert.Equal(0.4, average.Numbers[0]);

        var percentiles = Parse(Vanilla + "Percentiles: P50: 0.3ms P95: 0.4ms P99: 14.8ms. Sample: 100");
        Assert.Equal(PerfLineKind.TickQueryPercentiles, percentiles.Kind);
        Assert.Equal([0.3, 0.4, 14.8], percentiles.Numbers);
    }

    [Fact]
    public void Paper_prints_tick_query_without_a_thread_name()
    {
        Assert.Equal(PerfLineKind.TickQueryState, Parse(PaperPrefix + "The game is running normally").Kind);
        Assert.Equal(PerfLineKind.TickQueryPercentiles,
            Parse(PaperPrefix + "Percentiles: P50: 0.3ms P95: 0.5ms P99: 7.4ms. Sample: 100").Kind);
    }

    [Theory]
    [InlineData("The game is running slow")]
    [InlineData("The game is frozen")]
    public void The_other_states_of_the_game_are_recognised(string message) =>
        Assert.Equal(PerfLineKind.TickQueryState, Parse(Vanilla + message).Kind);

    [Fact]
    public void A_player_cannot_fake_tick_query_in_the_chat()
    {
        Assert.Equal(PerfLineKind.None, Parse(Vanilla + "<Bob> Average time per tick: 99.0ms").Kind);
        Assert.Equal(PerfLineKind.None, Parse(Vanilla + "<Bob> The game is running normally").Kind);
    }

    // --- Paper's tps ---

    [Fact]
    public void Papers_tps_is_read_with_a_dot_for_the_decimal_point()
    {
        var parsed = Parse(PaperPrefix + Esc + "[38;5;3mTPS from last 1m, 5m, 15m: " + Esc + "[0m" + Esc + "[38;5;10m19.4"
                           + Esc + "[0m, " + Esc + "[38;5;10m19.9" + Esc + "[0m, " + Esc + "[38;5;10m*20.0" + Esc + "[0m");
        Assert.Equal(PerfLineKind.PaperTps, parsed.Kind);
        Assert.Equal([19.4, 19.9, 20.0], parsed.Numbers);
    }

    [Fact]
    public void Papers_tps_is_read_with_a_comma_for_the_decimal_point()
    {
        // A Spanish Windows prints "20,0, 20,0, 20,0": the commas between figures are not decimals.
        var parsed = Parse(PaperPrefix + Esc + "[38;5;3mTPS from last 1m, 5m, 15m: " + Esc + "[0m" + Esc + "[38;5;10m20,0"
                           + Esc + "[0m, " + Esc + "[38;5;10m19,5" + Esc + "[0m, " + Esc + "[38;5;10m20,0" + Esc + "[0m");
        Assert.Equal(PerfLineKind.PaperTps, parsed.Kind);
        Assert.Equal([20.0, 19.5, 20.0], parsed.Numbers);
    }

    // --- unknown commands ---

    [Theory]
    [InlineData("[20:42:08] [Server thread/INFO]: Unknown or incomplete command. See below for error")]
    [InlineData("[20:42:08] [Server thread/INFO]: tps<--[HERE]")]
    [InlineData("[20:42:08 INFO]: Unknown command. Type \"/help\" for help.")]
    public void An_unknown_command_is_recognised(string line) =>
        Assert.Equal(PerfLineKind.UnknownCommand, Parse(line).Kind);

    // --- nothing else ---

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[12:00:00] [Server thread/INFO]: Done (3.2s)! For help, type \"help\"")]
    [InlineData("[12:00:00] [Server thread/INFO]: <Bob> hello")]
    [InlineData("    at net.minecraft.server.MinecraftServer.run(MinecraftServer.java:123)")]
    public void Ordinary_lines_are_not_about_speed(string? line) =>
        Assert.Equal(PerfLineKind.None, Parse(line!).Kind);
}
