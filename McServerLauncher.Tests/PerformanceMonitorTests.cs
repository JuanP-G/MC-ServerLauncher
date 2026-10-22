using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// When a server counts as coping, and when the monitor says otherwise: only for problems that last,
/// never during the first minutes or a backup, and with the reason it gives.
/// </summary>
public class PerformanceMonitorTests
{
    private static readonly DateTime Start = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A monitor whose grace period is over.</summary>
    private static (PerformanceMonitor Monitor, DateTime Now) Settled()
    {
        var m = new PerformanceMonitor();
        m.Ready(Start);
        return (m, Start + PerformanceMonitor.Grace + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Nothing_is_judged_while_the_server_settles()
    {
        var m = new PerformanceMonitor();
        m.Ready(Start);
        m.AddTickReading(Start.AddMinutes(1), TickSource.Spark, 5, 200);
        m.AddTickReading(Start.AddMinutes(1.5), TickSource.Spark, 5, 200);
        m.AddLagWarning(Start.AddMinutes(1), TimeSpan.FromSeconds(10));

        var status = m.Evaluate(Start.AddMinutes(2));

        Assert.Equal(PerformanceLevel.Unknown, status.Level);
        Assert.Equal(0, status.LagWarnings);
    }

    [Fact]
    public void A_server_that_is_stopped_is_not_judged()
    {
        var (m, now) = Settled();
        m.Stopped();

        Assert.Equal(PerformanceLevel.Unknown, m.Evaluate(now).Level);
    }

    [Fact]
    public void A_quiet_server_is_good()
    {
        var (m, now) = Settled();
        m.AddTickReading(now, TickSource.Spark, 20, 0.4);

        var status = m.Evaluate(now);

        Assert.Equal(PerformanceLevel.Good, status.Level);
        Assert.Equal(PerformanceCause.None, status.Cause);
        Assert.Equal(20, status.Tps);
        Assert.Equal(0.4, status.Mspt);
    }

    // --- ticks ---

    [Fact]
    public void One_bad_reading_alone_says_nothing()
    {
        var (m, now) = Settled();
        m.AddTickReading(now, TickSource.TickQuery, 12, 80);

        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now).Level);
    }

    [Fact]
    public void Two_bad_spark_readings_are_enough_because_spark_already_averages_a_minute()
    {
        var (m, now) = Settled();
        m.AddTickReading(now, TickSource.Spark, 12, 80);
        m.AddTickReading(now.AddSeconds(30), TickSource.Spark, 12, 80);

        var status = m.Evaluate(now.AddSeconds(30));

        Assert.Equal(PerformanceLevel.Saturated, status.Level);
        Assert.Equal(PerformanceCause.Ticks, status.Cause);
    }

    [Fact]
    public void The_other_commands_need_three_bad_readings()
    {
        var (m, now) = Settled();
        m.AddTickReading(now, TickSource.TickQuery, 12, 80);
        m.AddTickReading(now.AddSeconds(30), TickSource.TickQuery, 12, 80);
        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddSeconds(30)).Level);

        m.AddTickReading(now.AddSeconds(60), TickSource.TickQuery, 12, 80);
        Assert.Equal(PerformanceLevel.Saturated, m.Evaluate(now.AddSeconds(60)).Level);
    }

    [Theory]
    [InlineData(17.9, 10, PerformanceLevel.Saturated)]
    [InlineData(18.0, 10, PerformanceLevel.Tight)]
    [InlineData(19.4, 10, PerformanceLevel.Tight)]
    [InlineData(19.5, 10, PerformanceLevel.Good)]
    [InlineData(20.0, 50.1, PerformanceLevel.Saturated)]
    [InlineData(20.0, 40.1, PerformanceLevel.Tight)]
    [InlineData(20.0, 40.0, PerformanceLevel.Good)]
    public void The_thresholds_for_TPS_and_MSPT(double tps, double mspt, PerformanceLevel expected)
    {
        var (m, now) = Settled();
        m.AddTickReading(now, TickSource.Spark, tps, mspt);
        m.AddTickReading(now.AddSeconds(30), TickSource.Spark, tps, mspt);

        Assert.Equal(expected, m.Evaluate(now.AddSeconds(30)).Level);
    }

    [Fact]
    public void Two_good_readings_clear_a_problem()
    {
        var (m, now) = Settled();
        m.AddTickReading(now, TickSource.Spark, 12, 80);
        m.AddTickReading(now.AddSeconds(30), TickSource.Spark, 12, 80);
        Assert.Equal(PerformanceLevel.Saturated, m.Evaluate(now.AddSeconds(30)).Level);

        m.AddTickReading(now.AddSeconds(60), TickSource.Spark, 20, 5);
        Assert.Equal(PerformanceLevel.Saturated, m.Evaluate(now.AddSeconds(60)).Level);   // one is not enough

        m.AddTickReading(now.AddSeconds(90), TickSource.Spark, 20, 5);
        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddSeconds(90)).Level);
    }

    // --- "Can't keep up!" ---

    [Fact]
    public void One_or_two_lag_warnings_are_tight_and_three_are_saturated()
    {
        var (m, now) = Settled();
        m.AddLagWarning(now, TimeSpan.FromSeconds(5));
        var one = m.Evaluate(now);
        Assert.Equal(PerformanceLevel.Tight, one.Level);
        Assert.Equal(PerformanceCause.LagWarnings, one.Cause);

        m.AddLagWarning(now.AddSeconds(20), TimeSpan.FromSeconds(5));
        Assert.Equal(PerformanceLevel.Tight, m.Evaluate(now.AddSeconds(20)).Level);

        m.AddLagWarning(now.AddSeconds(40), TimeSpan.FromSeconds(5));
        var three = m.Evaluate(now.AddSeconds(40));
        Assert.Equal(PerformanceLevel.Saturated, three.Level);
        Assert.Equal(3, three.LagWarnings);
    }

    [Fact]
    public void Warnings_older_than_five_minutes_stop_counting()
    {
        var (m, now) = Settled();
        m.AddLagWarning(now, TimeSpan.FromSeconds(5));
        m.AddLagWarning(now.AddSeconds(10), TimeSpan.FromSeconds(5));
        m.AddLagWarning(now.AddSeconds(20), TimeSpan.FromSeconds(5));

        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddMinutes(6)).Level);
    }

    [Fact]
    public void A_delay_of_over_a_minute_is_the_clock_jumping_not_lag()
    {
        // The PC waking from sleep: "Running 30527116ms behind".
        var (m, now) = Settled();
        m.AddLagWarning(now, TimeSpan.FromMilliseconds(30527116));

        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now).Level);
        Assert.Equal(0, m.Evaluate(now).LagWarnings);
    }

    // --- CPU ---

    [Fact]
    public void A_spike_of_CPU_is_nothing_and_a_sustained_load_is_something()
    {
        var (m, now) = Settled();
        m.AddCpu(now, 95);
        m.AddCpu(now.AddSeconds(20), 30);                     // the spike ends
        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddMinutes(5)).Level);

        m.AddCpu(now.AddMinutes(6), 95);
        m.AddCpu(now.AddMinutes(6).AddSeconds(60), 95);
        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddMinutes(7)).Level);        // 1 min

        m.AddCpu(now.AddMinutes(8), 95);
        var status = m.Evaluate(now.AddMinutes(8));                                        // 2 min
        Assert.Equal(PerformanceLevel.Saturated, status.Level);
        Assert.Equal(PerformanceCause.Cpu, status.Cause);
    }

    [Fact]
    public void Three_minutes_over_75_percent_is_tight()
    {
        var (m, now) = Settled();
        m.AddCpu(now, 80);
        m.AddCpu(now.AddMinutes(2), 80);
        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddMinutes(2)).Level);

        m.AddCpu(now.AddMinutes(3), 80);
        Assert.Equal(PerformanceLevel.Tight, m.Evaluate(now.AddMinutes(3)).Level);
    }

    [Fact]
    public void CPU_is_measured_in_time_so_a_window_in_the_tray_still_works()
    {
        // One sample every 20 s: six samples are two minutes.
        var (m, now) = Settled();
        for (var i = 0; i <= 6; i++) m.AddCpu(now.AddSeconds(20 * i), 96);

        Assert.Equal(PerformanceLevel.Saturated, m.Evaluate(now.AddSeconds(120)).Level);
    }

    // --- memory ---

    [Fact]
    public void A_heap_nearly_full_for_five_minutes_is_tight_and_never_more()
    {
        var (m, now) = Settled();
        for (var s = 0; s <= 300; s += 30) m.AddMemory(now.AddSeconds(s), 3800, 4096, 100, 0);

        var status = m.Evaluate(now.AddSeconds(300));

        Assert.Equal(PerformanceLevel.Tight, status.Level);
        Assert.Equal(PerformanceCause.Memory, status.Cause);
    }

    [Fact]
    public void A_heap_nearly_full_for_a_moment_is_nothing()
    {
        var (m, now) = Settled();
        m.AddMemory(now, 3800, 4096, 100, 0);
        m.AddMemory(now.AddSeconds(30), 1200, 4096, 150, 0);
        for (var s = 60; s <= 400; s += 30) m.AddMemory(now.AddSeconds(s), 1200, 4096, 200, 0);

        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddSeconds(400)).Level);
    }

    [Fact]
    public void A_collector_taking_a_sixth_of_the_time_counts_as_pressure_even_with_room_left()
    {
        var (m, now) = Settled();
        double pause = 0;
        for (var s = 0; s <= 330; s += 30)
        {
            m.AddMemory(now.AddSeconds(s), 1000, 8192, pause, 0);
            pause += 30_000 * 0.2;    // 20 % of every interval
        }

        Assert.Equal(PerformanceCause.Memory, m.Evaluate(now.AddSeconds(330)).Cause);
    }

    [Fact]
    public void A_full_collection_keeps_the_pressure_on_for_a_minute()
    {
        var (m, now) = Settled();
        m.AddMemory(now, 1000, 8192, 100, 0);
        m.AddMemory(now.AddSeconds(30), 1000, 8192, 200, 1);      // a full GC
        for (var s = 60; s <= 330; s += 30) m.AddMemory(now.AddSeconds(s), 1000, 8192, 250, 1);

        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddSeconds(330)).Level);
    }

    // --- out of memory ---

    [Fact]
    public void Running_out_of_memory_is_saturated_at_once_even_while_settling()
    {
        var m = new PerformanceMonitor();
        m.Ready(Start);
        m.OutOfMemory();

        var status = m.Evaluate(Start.AddSeconds(10));

        Assert.Equal(PerformanceLevel.Saturated, status.Level);
        Assert.Equal(PerformanceCause.OutOfMemory, status.Cause);
    }

    [Fact]
    public void A_new_run_forgets_the_last_ones_out_of_memory()
    {
        var m = new PerformanceMonitor();
        m.Ready(Start);
        m.OutOfMemory();
        m.Ready(Start.AddHours(1));

        Assert.Equal(PerformanceLevel.Unknown, m.Evaluate(Start.AddHours(1).AddMinutes(1)).Level);
    }

    // --- backups ---

    [Fact]
    public void Nothing_is_judged_during_a_backup_nor_for_half_a_minute_after()
    {
        var (m, now) = Settled();
        m.BackupStarted();
        m.AddTickReading(now, TickSource.Spark, 5, 200);
        m.AddTickReading(now.AddSeconds(30), TickSource.Spark, 5, 200);
        m.AddLagWarning(now.AddSeconds(30), TimeSpan.FromSeconds(10));
        Assert.Equal(PerformanceLevel.Unknown, m.Evaluate(now.AddSeconds(30)).Level);

        m.BackupEnded(now.AddSeconds(60));
        Assert.Equal(PerformanceLevel.Unknown, m.Evaluate(now.AddSeconds(80)).Level);
        Assert.Equal(PerformanceLevel.Good, m.Evaluate(now.AddSeconds(91)).Level);
    }

    // --- the strongest cause wins ---

    [Fact]
    public void When_several_signals_agree_the_most_serious_cause_is_named()
    {
        var (m, now) = Settled();
        m.AddTickReading(now, TickSource.Spark, 10, 90);
        m.AddTickReading(now.AddSeconds(30), TickSource.Spark, 10, 90);
        m.AddLagWarning(now.AddSeconds(30), TimeSpan.FromSeconds(5));

        Assert.Equal(PerformanceCause.Ticks, m.Evaluate(now.AddSeconds(30)).Cause);
    }
}
