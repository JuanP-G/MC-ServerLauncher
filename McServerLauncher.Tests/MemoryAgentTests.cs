using System.Security.Cryptography;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The memory agent: when the app decides to tighten a server's heap, how the jar gets into the
/// server's folder, when it must not be used, and what is done when a Java refuses it.
/// </summary>
[Collection("memory-agent")]
public sealed class MemoryAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-agent-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousFailures = MemoryAgent.FailuresPath;

    public MemoryAgentTests()
    {
        Directory.CreateDirectory(_root);
        MemoryAgent.FailuresPath = Path.Combine(_root, "failures.json");
    }

    public void Dispose()
    {
        MemoryAgent.FailuresPath = _previousFailures;
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private static JavaService.JavaRuntime Java(int major, bool openJ9 = false) => new(major, openJ9, true);

    private static ServerConfig Auto() => new() { MemoryMode = MemoryMode.Automatic };

    private static string None(string _) => "";

    // --- the setting ---

    [Fact]
    public void The_setting_is_on_by_default_and_a_settings_file_without_it_reads_as_on()
    {
        Assert.True(new AppSettings().MemoryAgentEnabled);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.MemoryAgentEnabled);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"MemoryAgentEnabled\":false}")!.MemoryAgentEnabled);
    }

    // --- the policy ---

    [Theory]
    [InlineData(0.20, 0, HeapMargins.Relaxed, HeapMargins.Relaxed)]   // plenty
    [InlineData(0.14, 0, HeapMargins.Relaxed, HeapMargins.Tight)]     // an empty server tightens under 15 %
    [InlineData(0.15, 0, HeapMargins.Relaxed, HeapMargins.Relaxed)]   // exactly 15 % is not under it
    [InlineData(0.14, 3, HeapMargins.Relaxed, HeapMargins.Relaxed)]   // one with players waits
    [InlineData(0.09, 3, HeapMargins.Relaxed, HeapMargins.Tight)]     // ...until 10 %
    [InlineData(0.10, 3, HeapMargins.Relaxed, HeapMargins.Relaxed)]
    public void A_server_is_tightened_when_the_machine_runs_short_empty_ones_first(
        double free, int players, HeapMargins current, HeapMargins expected) =>
        Assert.Equal(expected, MemoryAgentPolicy.Decide(current, free, players));

    [Theory]
    [InlineData(0.16)]
    [InlineData(0.20)]
    [InlineData(0.25)]
    public void Between_15_and_25_percent_a_tightened_server_stays_tight(double free) =>
        Assert.Equal(HeapMargins.Tight, MemoryAgentPolicy.Decide(HeapMargins.Tight, free, 0));

    [Fact]
    public void It_goes_back_to_relaxed_only_above_25_percent()
    {
        Assert.Equal(HeapMargins.Tight, MemoryAgentPolicy.Decide(HeapMargins.Tight, 0.25, 0));
        Assert.Equal(HeapMargins.Relaxed, MemoryAgentPolicy.Decide(HeapMargins.Tight, 0.26, 0));
        Assert.Equal(HeapMargins.Relaxed, MemoryAgentPolicy.Decide(HeapMargins.Tight, 0.26, 5));
    }

    [Fact]
    public void Free_memory_hovering_around_a_threshold_does_not_flap()
    {
        var margins = HeapMargins.Relaxed;
        var changes = 0;
        foreach (var free in new[] { 0.16, 0.14, 0.16, 0.14, 0.18, 0.12, 0.2, 0.15, 0.22 })
        {
            var next = MemoryAgentPolicy.Decide(margins, free, 0);
            if (next != margins) changes++;
            margins = next;
        }

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Not_knowing_the_free_memory_changes_nothing()
    {
        Assert.Equal(HeapMargins.Tight, MemoryAgentPolicy.Decide(HeapMargins.Tight, -1, 0));
        Assert.Equal(HeapMargins.Relaxed, MemoryAgentPolicy.Decide(HeapMargins.Relaxed, -1, 0));
    }

    [Fact]
    public void The_control_file_says_what_the_agent_has_to_set()
    {
        Assert.Equal("MinHeapFreeRatio=10\nMaxHeapFreeRatio=30\nG1PeriodicGCInterval=15000\n",
            MemoryAgentPolicy.ControlText(HeapMargins.Relaxed));
        Assert.Equal("MinHeapFreeRatio=5\nMaxHeapFreeRatio=15\nG1PeriodicGCInterval=5000\n",
            MemoryAgentPolicy.ControlText(HeapMargins.Tight));
    }

    [Fact]
    public void The_relaxed_margins_are_the_ones_the_profile_starts_a_server_with()
    {
        var flags = MemoryProfile.AutomaticFlags(Java(21), 4096, null);

        Assert.Contains("-XX:MinHeapFreeRatio=10", flags);
        Assert.Contains("-XX:MaxHeapFreeRatio=30", flags);
        Assert.Contains("-XX:G1PeriodicGCInterval=15000", flags);
    }

    // --- the jar ---

    private static string RepoFile(params string[] parts) =>
        Path.Combine([LocalizationTests.RepoRoot(), .. parts]);

    [Fact]
    public void The_jar_in_the_app_is_the_committed_one()
    {
        var committed = File.ReadAllBytes(RepoFile("agent", "mcsl-agent.jar"));
        var recorded = File.ReadAllText(RepoFile("agent", "mcsl-agent.jar.sha256")).Split(' ')[0].Trim();

        Assert.Equal(recorded, Convert.ToHexStringLower(SHA256.HashData(committed)));
        Assert.Equal(recorded, MemoryAgent.EmbeddedHash);
    }

    [Fact]
    public void Installing_puts_the_jar_and_a_relaxed_control_file_in_the_hidden_folder()
    {
        Assert.True(MemoryAgent.Install(_root));

        var dir = Path.Combine(_root, GcLogging.FolderName);
        Assert.Equal(MemoryAgent.EmbeddedHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "mcsl-agent.jar")))));
        Assert.Equal(MemoryAgentPolicy.ControlText(HeapMargins.Relaxed), File.ReadAllText(Path.Combine(dir, "control")));
    }

    [Fact]
    public void A_jar_that_is_not_this_builds_is_replaced()
    {
        var dir = Path.Combine(_root, GcLogging.FolderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mcsl-agent.jar"), "corrupt");

        Assert.True(MemoryAgent.Install(_root));

        Assert.Equal(MemoryAgent.EmbeddedHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "mcsl-agent.jar")))));
    }

    [Fact]
    public void Installing_twice_leaves_the_jar_alone_and_clears_the_last_runs_status()
    {
        MemoryAgent.Install(_root);
        var dir = Path.Combine(_root, GcLogging.FolderName);
        var jar = Path.Combine(dir, "mcsl-agent.jar");
        var stamp = File.GetLastWriteTimeUtc(jar);
        File.WriteAllText(Path.Combine(dir, "status.json"), "{}");
        Thread.Sleep(30);

        MemoryAgent.Install(_root);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(jar));
        Assert.False(File.Exists(Path.Combine(dir, "status.json")));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_means_no_agent_rather_than_a_server_that_will_not_start()
    {
        File.WriteAllText(Path.Combine(_root, GcLogging.FolderName), "in the way");

        Assert.False(MemoryAgent.Install(_root));
    }

    [Fact]
    public void The_flag_uses_a_relative_path_without_spaces_or_quotes()
    {
        Assert.Equal("-javaagent:.mcserverlauncher-memory/mcsl-agent.jar=.mcserverlauncher-memory", MemoryAgent.Flag);
        Assert.DoesNotContain(' ', MemoryAgent.Flag);
        Assert.DoesNotContain('"', MemoryAgent.Flag);
    }

    // --- when it is not used ---

    [Fact]
    public void An_automatic_server_on_a_modern_HotSpot_gets_it()
    {
        Assert.True(MemoryAgent.ShouldUse(Auto(), Java(21), settingEnabled: true, None));
        Assert.True(MemoryAgent.ShouldUse(Auto(), Java(12), settingEnabled: true, None));
    }

    [Fact]
    public void A_manual_server_never_gets_it()
    {
        var manual = new ServerConfig { MemoryMode = MemoryMode.Manual };

        Assert.False(MemoryAgent.ShouldUse(manual, Java(21), true, None));
    }

    [Fact]
    public void A_server_whose_own_arguments_size_the_memory_is_manual_and_never_gets_it()
    {
        var config = new ServerConfig { MemoryMode = MemoryMode.Automatic, ExtraJvmArgs = "-Xmx6G" };

        Assert.False(MemoryAgent.ShouldUse(config, Java(21), true, None));
    }

    [Fact]
    public void It_is_not_used_on_OpenJ9_or_before_Java_12()
    {
        Assert.False(MemoryAgent.ShouldUse(Auto(), Java(21, openJ9: true), true, None));
        Assert.False(MemoryAgent.ShouldUse(Auto(), Java(11), true, None));
        Assert.False(MemoryAgent.ShouldUse(Auto(), Java(8), true, None));
        Assert.False(MemoryAgent.ShouldUse(Auto(), Java(0), true, None));
    }

    [Fact]
    public void The_setting_turns_it_off()
    {
        Assert.False(MemoryAgent.ShouldUse(Auto(), Java(21), settingEnabled: false, None));
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData("0", true)]
    [InlineData("", true)]
    public void The_environment_variable_turns_it_off(string value, bool used) =>
        Assert.Equal(used, MemoryAgent.ShouldUse(Auto(), Java(21), true,
            name => name == MemoryAgent.DisableVariable ? value : null));

    // --- when a Java refuses it ---

    [Theory]
    [InlineData("Error opening zip file or JAR manifest missing : .mcserverlauncher-memory/mcsl-agent.jar")]
    [InlineData("agent library failed Agent_OnLoad: instrument")]
    [InlineData("FATAL ERROR in native method: processing of -javaagent failed, processJavaStart failed")]
    public void The_ways_a_JVM_refuses_an_agent_are_recognised(string line) =>
        Assert.True(MemoryAgent.IsLoadError(line));

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: Done (3.2s)! For help, type \"help\"")]
    [InlineData("[12:00:00] [Server thread/INFO]: <Bob> agent library failed")]
    [InlineData("[12:00:00] [Server thread/INFO]: <Bob> Error opening zip file or JAR manifest missing")]
    [InlineData("")]
    public void Other_lines_and_a_players_chat_are_not_agent_errors(string line) =>
        Assert.False(MemoryAgent.IsLoadError(line));

    [Fact]
    public void A_refusal_is_remembered_for_that_java_and_that_agent_and_nothing_else()
    {
        var hash = MemoryAgent.EmbeddedHash;
        Assert.False(MemoryAgent.HasFailed(17, hash));

        MemoryAgent.RecordFailure(17, hash);

        Assert.True(MemoryAgent.HasFailed(17, hash));
        Assert.False(MemoryAgent.HasFailed(21, hash));          // another Java
        Assert.False(MemoryAgent.HasFailed(17, "another-hash")); // another build of the agent
        Assert.False(MemoryAgent.ShouldUse(Auto(), Java(17), true, None));
        Assert.True(MemoryAgent.ShouldUse(Auto(), Java(21), true, None));
    }

    [Fact]
    public void The_refusal_survives_the_app_restarting()
    {
        MemoryAgent.RecordFailure(25, MemoryAgent.EmbeddedHash);

        Assert.True(File.Exists(MemoryAgent.FailuresPath));
        Assert.True(MemoryAgent.HasFailed(25, MemoryAgent.EmbeddedHash));   // read back from the file
    }

    // --- what the agent says ---

    [Fact]
    public void A_recent_status_is_read_and_an_old_one_is_not()
    {
        var dir = Path.Combine(_root, GcLogging.FolderName);
        Directory.CreateDirectory(dir);
        var now = DateTime.UtcNow;
        var written = new DateTimeOffset(now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(dir, "status.json"),
            $$"""{"agent":"1","time":{{written}},"java":"21.0.10","heapUsed":100,"heapCommitted":200,"heapMax":4096,"liveAfterGc":80,"gcCount":3,"gcTimeMs":9,"flags":{"MinHeapFreeRatio":5,"MaxHeapFreeRatio":15,"G1PeriodicGCInterval":5000},"lastError":""}""");

        var status = MemoryAgent.ReadStatus(_root, now, TimeSpan.FromSeconds(20));

        Assert.NotNull(status);
        Assert.Equal(80, status!.LiveAfterGc);
        Assert.Equal(200, status.HeapCommitted);
        Assert.Equal(5, status.Flags["MinHeapFreeRatio"]);

        Assert.Null(MemoryAgent.ReadStatus(_root, now.AddMinutes(1), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void A_missing_or_broken_status_is_nothing_rather_than_an_error()
    {
        Assert.Null(MemoryAgent.ReadStatus(_root, DateTime.UtcNow, TimeSpan.FromSeconds(20)));

        var dir = Path.Combine(_root, GcLogging.FolderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status.json"), "{ half a fi");

        Assert.Null(MemoryAgent.ReadStatus(_root, DateTime.UtcNow, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Writing_the_control_file_replaces_it_whole()
    {
        MemoryAgent.WriteControl(_root, HeapMargins.Tight);
        MemoryAgent.WriteControl(_root, HeapMargins.Relaxed);

        var dir = Path.Combine(_root, GcLogging.FolderName);
        Assert.Equal(MemoryAgentPolicy.ControlText(HeapMargins.Relaxed), File.ReadAllText(Path.Combine(dir, "control")));
        Assert.False(File.Exists(Path.Combine(dir, "control.tmp")));
    }
}
