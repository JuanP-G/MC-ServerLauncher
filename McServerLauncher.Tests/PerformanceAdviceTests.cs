using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The settings changes the Performance page offers, the mods it suggests, and the machine's free
/// memory that decides whether to offer more.
/// </summary>
public class PerformanceAdviceTests
{
    private static PerformanceStatus Status(PerformanceLevel level, PerformanceCause cause) =>
        new(level, cause, null, null, 0);

    private static AdviceInputs Inputs(int sim = 10, int view = 12, long ceiling = 4096, long total = 16384,
        long available = 9000, bool canMeasure = true, bool spark = false, ServerType type = ServerType.Fabric) =>
        new(type, sim, view, ceiling, total, available, canMeasure, spark);

    private static IReadOnlyList<Advice> Advise(PerformanceLevel level, PerformanceCause cause, AdviceInputs input) =>
        PerformanceAdvice.For(Status(level, cause), input);

    // --- distances ---

    [Fact]
    public void A_simulation_distance_above_6_goes_to_6_and_the_view_distance_to_10()
    {
        var advice = Advise(PerformanceLevel.Tight, PerformanceCause.Ticks, Inputs(sim: 10, view: 12));

        Assert.Contains(advice, a => a is { Kind: AdviceKind.LowerSimulationDistance, From: 10, To: 6 });
        Assert.Contains(advice, a => a is { Kind: AdviceKind.LowerViewDistance, From: 12, To: 10 });
    }

    [Fact]
    public void When_saturated_it_goes_lower()
    {
        var advice = Advise(PerformanceLevel.Saturated, PerformanceCause.Ticks, Inputs(sim: 10, view: 12));

        Assert.Contains(advice, a => a is { Kind: AdviceKind.LowerSimulationDistance, To: 4 });
        Assert.Contains(advice, a => a is { Kind: AdviceKind.LowerViewDistance, To: 8 });
    }

    [Fact]
    public void Distances_already_low_are_left_alone()
    {
        var advice = Advise(PerformanceLevel.Saturated, PerformanceCause.Ticks, Inputs(sim: 5, view: 8));

        Assert.DoesNotContain(advice, a => a.Kind is AdviceKind.LowerSimulationDistance or AdviceKind.LowerViewDistance);
    }

    [Fact]
    public void The_view_distance_never_ends_below_the_simulation_distance()
    {
        // Simulation 7 -> 6; a view distance of 9 -> 10 is not lowered, and one of 6 stays.
        var advice = Advise(PerformanceLevel.Tight, PerformanceCause.Ticks, Inputs(sim: 7, view: 6));

        Assert.DoesNotContain(advice, a => a.Kind == AdviceKind.LowerViewDistance);
    }

    [Fact]
    public void Nothing_is_offered_for_a_server_that_is_fine()
    {
        Assert.Empty(Advise(PerformanceLevel.Good, PerformanceCause.None, Inputs()));
        Assert.Empty(Advise(PerformanceLevel.Unknown, PerformanceCause.None, Inputs()));
    }

    // --- memory ---

    [Fact]
    public void More_memory_is_offered_when_memory_is_the_cause_and_the_machine_can_spare_it()
    {
        var advice = Advise(PerformanceLevel.Tight, PerformanceCause.Memory, Inputs(ceiling: 4096, total: 16384, available: 9000));

        Assert.Contains(advice, a => a is { Kind: AdviceKind.RaiseMemory, From: 4096, To: 6144 });
    }

    [Fact]
    public void For_a_memory_problem_the_memory_comes_first()
    {
        var advice = Advise(PerformanceLevel.Saturated, PerformanceCause.OutOfMemory, Inputs());

        Assert.Equal(AdviceKind.RaiseMemory, advice[0].Kind);
    }

    [Fact]
    public void More_memory_is_not_offered_when_the_cause_is_something_else()
    {
        var advice = Advise(PerformanceLevel.Saturated, PerformanceCause.Cpu, Inputs());

        Assert.DoesNotContain(advice, a => a.Kind == AdviceKind.RaiseMemory);
    }

    [Theory]
    [InlineData(16384, 3000, false)]    // free memory would drop below the 2 GB reserve
    [InlineData(16384, 4096, true)]     // exactly 2 GB reserve left
    [InlineData(8000, 9000, false)]     // 4 + 2 = 6 GB is over three quarters of 8000 MB
    [InlineData(0, 9000, false)]        // total unknown
    [InlineData(16384, 0, false)]       // free memory unknown
    public void More_memory_needs_room_left_over(long total, long available, bool offered)
    {
        var advice = Advise(PerformanceLevel.Tight, PerformanceCause.Memory,
            Inputs(ceiling: 4096, total: total, available: available));

        Assert.Equal(offered, advice.Any(a => a.Kind == AdviceKind.RaiseMemory));
    }

    [Fact]
    public void More_memory_is_never_offered_above_32_GB()
    {
        var advice = Advise(PerformanceLevel.Tight, PerformanceCause.Memory,
            Inputs(ceiling: 31 * 1024, total: 128 * 1024, available: 100 * 1024));

        Assert.DoesNotContain(advice, a => a.Kind == AdviceKind.RaiseMemory);
    }

    // --- measuring ---

    [Fact]
    public void Spark_is_offered_only_when_nothing_else_can_measure_the_server()
    {
        Assert.Contains(Advise(PerformanceLevel.Tight, PerformanceCause.Cpu, Inputs(canMeasure: false, spark: false)),
            a => a.Kind == AdviceKind.InstallSpark);
        Assert.DoesNotContain(Advise(PerformanceLevel.Tight, PerformanceCause.Cpu, Inputs(canMeasure: true)),
            a => a.Kind == AdviceKind.InstallSpark);
        Assert.DoesNotContain(Advise(PerformanceLevel.Tight, PerformanceCause.Cpu, Inputs(canMeasure: false, spark: true)),
            a => a.Kind == AdviceKind.InstallSpark);
    }

    // --- the picks ---

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static IReadOnlyList<PerformancePick> Suggest(ServerType type, PerformanceCause cause = PerformanceCause.Ticks,
        IReadOnlySet<string>? ids = null, string[]? files = null, bool bundled = false, bool spark = false) =>
        new PerformancePicks(overridePath: Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid().ToString("N")))
            .Suggest(type, cause, ids ?? None, files ?? [], bundled, spark);

    [Fact]
    public void The_embedded_list_loads_and_has_no_C2ME()
    {
        var all = new PerformancePicks(Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid().ToString("N"))).All;

        Assert.NotEmpty(all);
        Assert.DoesNotContain(all, p => p.Slug.Contains("c2me", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fabric_gets_the_Fabric_picks_and_not_Forges()
    {
        var slugs = Suggest(ServerType.Fabric).Select(p => p.Slug).ToList();

        Assert.Contains("lithium", slugs);
        Assert.Contains("krypton", slugs);
        Assert.DoesNotContain("canary", slugs);
    }

    [Fact]
    public void Forge_gets_canary_and_not_lithium()
    {
        var slugs = Suggest(ServerType.Forge).Select(p => p.Slug).ToList();

        Assert.Contains("canary", slugs);
        Assert.DoesNotContain("lithium", slugs);
    }

    [Fact]
    public void Vanilla_gets_nothing()
    {
        Assert.Empty(Suggest(ServerType.Vanilla));
    }

    [Fact]
    public void What_is_installed_is_not_offered_again_whether_known_by_id_or_by_file_name()
    {
        var byId = Suggest(ServerType.Fabric, ids: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lithium" })
            .Select(p => p.Slug).ToList();
        var byFile = Suggest(ServerType.Fabric, files: ["ferritecore-9.0.0-fabric.jar", "modernfix-5.27.jar"])
            .Select(p => p.Slug).ToList();

        Assert.DoesNotContain("lithium", byId);
        Assert.DoesNotContain("ferrite-core", byFile);
        Assert.DoesNotContain("modernfix", byFile);
        Assert.Contains("lithium", byFile);
    }

    [Fact]
    public void A_pick_that_clashes_with_something_installed_is_hidden()
    {
        var withCanary = Suggest(ServerType.Forge, files: ["canary-mc1.20-0.3.jar"]).Select(p => p.Slug).ToList();

        Assert.DoesNotContain("lithium", withCanary);
        Assert.DoesNotContain("canary", withCanary);
    }

    [Fact]
    public void The_picks_that_relieve_the_cause_come_first()
    {
        var memory = Suggest(ServerType.Fabric, PerformanceCause.Memory).Select(p => p.Slug).ToList();

        Assert.True(memory.IndexOf("ferrite-core") < memory.IndexOf("krypton"));
        Assert.True(memory.IndexOf("modernfix") < memory.IndexOf("lithium"));
    }

    [Fact]
    public void Spark_is_offered_only_when_asked_for_and_not_already_bundled()
    {
        Assert.DoesNotContain("spark", Suggest(ServerType.Fabric).Select(p => p.Slug));
        Assert.Contains("spark", Suggest(ServerType.Fabric, spark: true).Select(p => p.Slug));
        Assert.DoesNotContain("spark", Suggest(ServerType.Paper, bundled: true, spark: true).Select(p => p.Slug));
    }

    [Fact]
    public void A_list_in_the_users_folder_replaces_the_embedded_one()
    {
        var path = Path.Combine(Path.GetTempPath(), "picks-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"picks":[{"slug":"mine","modIds":["mine"],"loaders":["Fabric"],"helps":["Ticks"]}]}""");
        try
        {
            var picks = new PerformancePicks(path);
            Assert.Equal(["mine"], picks.All.Select(p => p.Slug));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_broken_list_in_the_users_folder_falls_back_to_the_embedded_one()
    {
        var path = Path.Combine(Path.GetTempPath(), "picks-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.NotEmpty(new PerformancePicks(path).All);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Paper's own distances ---

    [Fact]
    public void Spigot_yml_with_default_distances_overrides_nothing()
    {
        const string yml = """
            world-settings:
              default:
                view-distance: default
                simulation-distance: default
            """;
        Assert.Equal((false, false), PaperDistanceOverride.Parse(yml));
    }

    [Fact]
    public void A_number_in_spigot_yml_overrides_the_property()
    {
        const string yml = """
            world-settings:
              default:
                view-distance: 6
                simulation-distance: default
            """;
        Assert.Equal((true, false), PaperDistanceOverride.Parse(yml));
    }

    [Fact]
    public void A_missing_spigot_yml_overrides_nothing()
    {
        Assert.Equal((false, false), PaperDistanceOverride.Read(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))));
    }

    // --- the machine's free memory ---

    [Fact]
    public void MemAvailable_is_read_from_proc_meminfo()
    {
        const string meminfo = "MemTotal:       16384000 kB\nMemFree:         1000000 kB\nMemAvailable:    9216000 kB\nBuffers: 1 kB\n";

        Assert.Equal(9000, SystemMemory.ParseMemAvailableMb(meminfo));
        Assert.Equal(0, SystemMemory.ParseMemAvailableMb("nothing here"));
    }

    [Fact]
    public void The_free_memory_of_this_machine_is_something_on_Windows_and_Linux()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Assert.True(SystemMemory.AvailableMb() > 0);
    }

    // --- the one-off raise ---

    [Fact]
    public void A_one_off_raise_adds_to_the_estimate_and_says_so()
    {
        var config = new ServerConfig { MemoryMode = MemoryMode.Automatic };
        var plan = MemoryProfile.Plan(new MemoryInputs(config, new JavaService.JavaRuntime(21, false, true), 0, 0, 0, false, BoostMb: 2048));

        Assert.Equal(4096 + 2048, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Boosted, plan.Reason);
    }

    [Fact]
    public void The_raise_is_taken_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "boost-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryHistoryStore(dir);
            store.SetNextStartBoost(2048);

            Assert.Equal(2048, new MemoryHistoryStore(dir).TakeNextStartBoost());   // survives a reopen
            Assert.Equal(0, new MemoryHistoryStore(dir).TakeNextStartBoost());       // and is gone
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}
