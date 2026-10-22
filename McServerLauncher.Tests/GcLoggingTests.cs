using System.Diagnostics;
using System.IO;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// When the app asks the JVM for a GC log, and the little that reads the process's own memory.
/// </summary>
public sealed class GcLoggingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gclogging-" + Guid.NewGuid().ToString("N"));

    public GcLoggingTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private static JavaService.JavaRuntime Java(int major, bool openJ9 = false) => new(major, openJ9, true);

    [Theory]
    [InlineData(11)]
    [InlineData(17)]
    [InlineData(21)]
    [InlineData(25)]
    public void A_HotSpot_from_Java_11_is_asked_for_the_log(int major)
    {
        var flag = GcLogging.Flag(Java(major), null);

        // The path is relative and uses no colon of its own: -Xlog splits its parts on them.
        Assert.Equal("-Xlog:gc:file=.mcserverlauncher-memory/gc.log:uptime,level,tags:filecount=2,filesize=8M", flag);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0)]   // a Java that could not be read
    public void An_older_or_unreadable_Java_is_not_asked(int major) =>
        Assert.Null(GcLogging.Flag(Java(major), null));

    [Fact]
    public void OpenJ9_is_not_asked_because_its_options_differ()
    {
        Assert.Null(GcLogging.Flag(Java(21, openJ9: true), null));
    }

    [Theory]
    [InlineData("-Xlog:gc*:file=logs/gc.log")]
    [InlineData("-Dfoo=1 -Xlog:disable -Xlog:all=warning")]
    public void Arguments_that_configure_logging_are_left_alone(string extra) =>
        Assert.Null(GcLogging.Flag(Java(21), extra));

    [Fact]
    public void Other_extra_arguments_do_not_stop_it()
    {
        Assert.NotNull(GcLogging.Flag(Java(21), "-Dfile.encoding=UTF-8 -XX:+UseG1GC"));
    }

    [Fact]
    public void Preparing_makes_the_folder_and_clears_the_last_runs_files()
    {
        var dir = Path.Combine(_folder, GcLogging.FolderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gc.log"), "old");
        File.WriteAllText(Path.Combine(dir, "gc.log.0"), "older");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "mine");

        Assert.True(GcLogging.Prepare(_folder));

        Assert.False(File.Exists(Path.Combine(dir, "gc.log")));
        Assert.False(File.Exists(Path.Combine(dir, "gc.log.0")));
        Assert.True(File.Exists(Path.Combine(dir, "other.txt")));
    }

    [Fact]
    public void A_folder_that_cannot_be_made_means_no_flag_rather_than_a_server_that_will_not_start()
    {
        // A file where the folder should be.
        File.WriteAllText(Path.Combine(_folder, GcLogging.FolderName), "in the way");
        var config = new ServerConfig { FolderPath = _folder };

        Assert.Empty(GcLogging.FlagsFor(config, Java(21)));
    }

    [Fact]
    public void A_usable_folder_gives_the_flag()
    {
        var config = new ServerConfig { FolderPath = _folder };

        Assert.Single(GcLogging.FlagsFor(config, Java(21)));
        Assert.True(Directory.Exists(Path.Combine(_folder, GcLogging.FolderName)));
    }

    // --- the process's own memory ---

    [Fact]
    public void Linux_private_memory_is_the_clean_and_dirty_private_pages()
    {
        const string rollup = """
            55d0a1b00000-7ffc7a5ff000 ---p 00000000 00:00 0                          [rollup]
            Rss:              812340 kB
            Pss:              790112 kB
            Shared_Clean:      22000 kB
            Shared_Dirty:          0 kB
            Private_Clean:     60000 kB
            Private_Dirty:    730000 kB
            Private_Hugetlb:       0 kB
            Anonymous:        740000 kB
            """;

        Assert.Equal(790000, ProcessMemory.ParsePrivateKb(rollup));
    }

    [Fact]
    public void A_file_with_no_private_lines_gives_nothing()
    {
        Assert.Null(ProcessMemory.ParsePrivateKb("Rss: 1 kB\nPss: 1 kB\n"));
    }

    [Fact]
    public void A_running_process_reports_some_private_memory()
    {
        using var self = Process.GetCurrentProcess();

        var mb = ProcessMemory.PrivateMb(self);

        // Windows and Linux can say; elsewhere the caller falls back to the working set.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Assert.True(mb > 10);
    }

    [Fact]
    public void The_sample_carries_private_memory_next_to_the_working_set()
    {
        using var self = Process.GetCurrentProcess();

        var sample = new ProcessStatsService().Sample(self);

        Assert.NotNull(sample);
        Assert.True(sample!.PrivateMb > 0);
        Assert.True(sample.RamMb > 0);
    }

    [Fact]
    public void The_card_shows_private_memory_except_with_ZGC_whose_heap_it_does_not_count()
    {
        var sample = new ProcessStatsService.Stats(0, RamMb: 2400, TimeSpan.Zero, PrivateMb: 700);

        Assert.Equal(700, MemoryReadout.OwnMb(sample, GcCollector.G1));
        Assert.Equal(700, MemoryReadout.OwnMb(sample, GcCollector.Unknown));
        Assert.Equal(2400, MemoryReadout.OwnMb(sample, GcCollector.Z));
    }

    [Fact]
    public void A_sample_without_private_memory_falls_back_to_the_working_set()
    {
        var sample = new ProcessStatsService.Stats(0, RamMb: 900, TimeSpan.Zero);

        Assert.Equal(900, MemoryReadout.OwnMb(sample, GcCollector.G1));
    }
}
