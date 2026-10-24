using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// How a learned ceiling takes its place in the plan a server starts with: over the estimate and over
/// the owner's old numbers, under the machine's budget, with the Performance page's raise on top.
/// </summary>
public class MemoryLearningPlanTests
{
    private static readonly JavaService.JavaRuntime Java = new(21, false, true);

    private static MemoryPlan Plan(ServerConfig config, LearnedCeiling? learned, long total = 0, long others = 0,
        long boost = 0) =>
        MemoryProfile.Plan(new MemoryInputs(config, Java, 0, total, others, false, boost, learned));

    [Fact]
    public void A_learned_ceiling_replaces_the_estimate_and_says_so()
    {
        var plan = Plan(new ServerConfig { MemoryMode = MemoryMode.Automatic },
            new LearnedCeiling(2560, MemoryCeilingReason.Learned));

        Assert.Equal(2560, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Learned, plan.Reason);
        Assert.Contains("-Xmx2560M", plan.Flags);
    }

    [Fact]
    public void It_also_replaces_the_numbers_a_server_had_before_automatic_mode()
    {
        // MemoryMode null: moved over by the update with a 6 GB maximum, "until the app knows better".
        var config = new ServerConfig { MemoryMode = null, MaxRamGb = 6 };

        Assert.Equal(MemoryCeilingReason.KeptPrevious, Plan(config, null).Reason);
        Assert.Equal(3072, Plan(config, new LearnedCeiling(3072, MemoryCeilingReason.Learned)).CeilingMb);
    }

    [Fact]
    public void A_server_in_manual_mode_is_never_touched_by_what_was_learned()
    {
        var plan = Plan(new ServerConfig { MemoryMode = MemoryMode.Manual, MinRamGb = 2, MaxRamGb = 6 },
            new LearnedCeiling(2560, MemoryCeilingReason.Learned));

        Assert.Equal(6144, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Manual, plan.Reason);
    }

    [Fact]
    public void The_budget_still_has_the_last_word()
    {
        // 16 GB machine: half is 8 GB; another server holds 6 GB, so 2 GB are left for this one.
        var plan = Plan(new ServerConfig { MemoryMode = MemoryMode.Automatic },
            new LearnedCeiling(5120, MemoryCeilingReason.RaisedAfterPressure), total: 16384, others: 6144);

        Assert.Equal(2048, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Budget, plan.Reason);
    }

    [Fact]
    public void The_one_off_raise_goes_on_top_of_what_was_learned()
    {
        var plan = Plan(new ServerConfig { MemoryMode = MemoryMode.Automatic },
            new LearnedCeiling(2560, MemoryCeilingReason.Learned), boost: 2048);

        Assert.Equal(4608, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Boosted, plan.Reason);
    }

    // --- the run's own record ---

    [Fact]
    public void A_run_found_short_of_memory_is_remembered_as_such()
    {
        var t = new MemorySessionTracker(new MemorySessionStart(DateTime.UtcNow, MemoryMode.Automatic, 4096, 21, 0, 10, 10));
        t.Add(new GcEvent(1, 0, GcKind.Young, 100, 50, 200, 5, false, false));

        Assert.False(t.Finish(DateTime.UtcNow.AddMinutes(30), SessionEnd.Stopped).Pressured);

        t.NotePressure();

        Assert.True(t.Pressured);
        Assert.True(t.Finish(DateTime.UtcNow.AddMinutes(30), SessionEnd.Stopped).Pressured);
    }

    [Fact]
    public void A_history_written_before_the_field_existed_reads_as_not_pressured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mlp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "history.json"), """{"Version":1,"Sessions":[{"PeakLiveMb":700,"CeilingMb":4096}]}""");

            Assert.False(Assert.Single(new MemoryHistoryStore(dir).Sessions).Pressured);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}
