using System.IO.Compression;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Whatever reads or replaces a server's world holds it: backups, restores, and the moments the JVM
/// starts and stops writing to it.
/// </summary>
/// <remarks>
/// <para>
/// Each command used to take the backup gate only for the part it thought of as "the backup". Stop
/// sent <c>stop</c> while a backup by the clock was still zipping, so the server wrote the world on
/// its way down into a copy being read; the stop's own backup was refused as "already running"; and
/// the torn zip was recorded as matching the world, so the next start did not copy it either. A
/// restore confirmed after a knock on a sleeping server had started it replaced the world under a
/// running JVM. And a backup that failed before a start kept the server down.
/// </para>
/// <para>
/// No Avalonia here: the coordinator talks to its server through <see cref="IBackupHost"/>, and the
/// live backups are held open simply by not saying "Saved the game" yet.
/// </para>
/// </remarks>
public class BackupCoordinatorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-coord-" + Guid.NewGuid()
        .ToString("N"));

    private string World => Path.Combine(_folder, "world");
    private string LevelDat => Path.Combine(World, "level.dat");

    public BackupCoordinatorTests()
    {
        Directory.CreateDirectory(World);
        File.WriteAllText(LevelDat, "level");
    }

    /// <summary>A backup service that remembers when the world was noted as untouched.</summary>
    private sealed class RecordingService : WorldBackupService
    {
        public List<string> Events { get; } = new();

        public override async Task<string?> CreateBackupAsync(ServerConfig config, string trigger,
            IProgress<string>? log = null, string? protectFromPruning = null, CancellationToken ct = default)
        {
            var zip = await base.CreateBackupAsync(config, trigger, log, protectFromPruning, ct);
            lock (Events) Events.Add("zip:" + trigger);
            return zip;
        }

        public override void MarkUnchanged(ServerConfig config)
        {
            lock (Events) Events.Add("note");
            base.MarkUnchanged(config);
        }
    }

    private sealed class ThrowingService : WorldBackupService
    {
        public override Task<string?> CreateBackupAsync(ServerConfig config, string trigger,
            IProgress<string>? log = null, string? protectFromPruning = null,
                CancellationToken ct = default) =>
            throw new IOException("There is not enough space on the disk.",
                OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);   // ENOSPC elsewhere
    }

    private (FakeBackupHost Server, BackupCoordinator Coordinator) Running(WorldBackupService? service = null)
    {
        var server = new FakeBackupHost(_folder) { IsProcessRunning = true };
        return (server, new BackupCoordinator(server, service ?? new WorldBackupService()));
    }

    private (FakeBackupHost Server, BackupCoordinator Coordinator) Stopped(WorldBackupService? service = null)
    {
        var server = new FakeBackupHost(_folder);
        return (server, new BackupCoordinator(server, service ?? new WorldBackupService()));
    }

    private string[] Zips() => Directory.Exists(Path.Combine(_folder, "backups"))
        ? Directory.GetFiles(Path.Combine(_folder, "backups"), "*.zip*")
        : Array.Empty<string>();

    /// <summary>Waits until a live backup has asked the server to flush, i.e. is holding the world.</summary>
    private static async Task UntilFlushAsked(FakeBackupHost server)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (server.Commands)
                if (server.Commands.Contains("save-all flush")) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("the live backup never asked the server to flush");
    }

    [Fact]
    public async Task StopWaitsForTheCopyBeforeSendingStop()
    {
        var (server, coordinator) = Running();
        var copy = coordinator.BackupAsync("manual");
        await UntilFlushAsked(server);

        var stop = coordinator.StopAsync(() => server.Stop(), backupAfter: true);
        await Task.Delay(200);
        Assert.False(stop.IsCompleted);
        Assert.DoesNotContain("stop", server.Commands);

        coordinator.ServerSaved();
        Assert.Equal(BackupResult.Made, await copy.WaitAsync(TimeSpan.FromSeconds(10)));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "save-off", "save-all flush", "save-on", "stop" }, server.Commands);
        Assert.Contains(server.Lines, l => l == Localizer.Get("Msg_StopWaitsForBackup"));
    }

    [Fact]
    public async Task TheStopsOwnBackupIsMadeAfterALiveOneInsteadOfBeingRefused()
    {
        var (server, coordinator) = Running();
        server.PlayersOnline = 1;   // somebody is playing, so the world is not untouched
        var copy = coordinator.BackupAsync("manual");
        await UntilFlushAsked(server);

        // The server writes on its way down, after the manual copy was taken.
        var stop = coordinator.StopAsync(
            () => server.Stop(() => File.WriteAllText(LevelDat, "saved on the way down")), backupAfter: true);
        coordinator.ServerSaved();
        await copy;
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, Zips().Length);
        Assert.Contains(Zips(), z => z.EndsWith("--stop.zip", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Lines, l => l == Localizer.Get("Msg_BackupAlreadyRunning"));
    }

    [Fact]
    public async Task AStopCutsACopyByTheClockShortAndMakesItsOwn()
    {
        var (server, coordinator) = Running();
        server.PlayersOnline = 1;
        var copy = coordinator.BackupAsync("auto");
        await UntilFlushAsked(server);

        var stop = coordinator.StopAsync(
            () => server.Stop(() => File.WriteAllText(LevelDat, "saved on the way down")), backupAfter: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(()
            => copy.WaitAsync(TimeSpan.FromSeconds(10)));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "save-off", "save-all flush", "save-on", "stop" }, server.Commands);
        Assert.Single(Zips(), z => z.EndsWith("--stop.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingCopiesTheWorldBetweenTheStartsBackupAndTheLaunch()
    {
        var (_, coordinator) = Stopped();
        Task<BackupResult>? sneaked = null;

        await coordinator.StartAsync(() => sneaked = coordinator.BackupAsync("manual"));

        Assert.Equal(BackupResult.Busy, await sneaked!);
    }

    [Fact]
    public async Task ACrashDuringACopyNotesTheWorldOnlyAfterTheCopy()
    {
        var service = new RecordingService();
        var (server, coordinator) = Running(service);
        await new WorldBackupService().CreateBackupAsync(server.Config, "start");   // a backup to point at

        var copy = coordinator.BackupAsync("manual");
        await UntilFlushAsked(server);
        server.IsProcessRunning = false;   // the JVM dies mid-copy
        coordinator.ServerStopped();
        await Task.Delay(200);
        Assert.DoesNotContain("note", service.Events);

        coordinator.ServerSaved();
        await copy;
        await coordinator.StartAsync(() => { });   // waits for the note, as a crash's restart does

        Assert.Equal(new[] { "zip:manual", "note" },
            service.Events.Where(e => e is "note" || e.StartsWith("zip:")));
    }

    [Fact]
    public async Task ClosingCutsTheCopyShortAndStopsAfterIt()
    {
        var (server, coordinator) = Running();
        var copy = coordinator.BackupAsync("manual");
        await UntilFlushAsked(server);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.ShutdownAsync(() => server.Stop()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "closing waited for the save timeout");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        Assert.Equal(new[] { "save-off", "save-all flush", "save-on", "stop" }, server.Commands);
        Assert.Empty(Zips());
    }

    [Fact]
    public async Task ClosingCancelsNothingAfterIt()
    {
        // A failed update brings the app back with these same servers.
        var (server, coordinator) = Stopped();
        await coordinator.ShutdownAsync(null);

        Assert.Equal(BackupResult.Made, await coordinator.BackupAsync("manual"));
        Assert.Single(Zips());
        Assert.False(server.IsProcessRunning);
    }

    private string BackupOf(string level)
    {
        var source = Path.Combine(_folder, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "level.dat"), level);
        var zip = Path.Combine(_folder, "restore-me.zip");
        ZipFile.CreateFromDirectory(source, zip);
        return zip;
    }

    [Fact]
    public async Task ARestoreConfirmedAfterTheServerWokeIsRefused()
    {
        var (server, coordinator) = Running();

        Assert.Equal(RestoreResult.ServerRunning, await coordinator.RestoreAsync(BackupOf("yesterday")));
        Assert.Equal("level", File.ReadAllText(LevelDat));
        Assert.False(coordinator.IsRestoring);
    }

    [Fact]
    public async Task ARestoreWhileAStartIsGettingReadyIsRefused()
    {
        var (server, coordinator) = Stopped();
        server.IsPreparing = true;

        Assert.Equal(RestoreResult.ServerRunning, await coordinator.RestoreAsync(BackupOf("yesterday")));
        Assert.Equal("level", File.ReadAllText(LevelDat));
    }

    /// <summary>Reports synchronously, so the test can act at an exact step of the restore.</summary>
    private sealed class AtFirstReport(Action act) : IProgress<string>
    {
        private bool _done;

        public void Report(string value)
        {
            if (_done) return;
            _done = true;
            act();
        }
    }

    [Fact]
    public async Task AStartDuringARestoreLaunchesOnTheRestoredWorld()
    {
        var (_, coordinator) = Stopped();
        Task? start = null;
        string? worldAtLaunch = null;

        var result = await coordinator.RestoreAsync(BackupOf("yesterday"), new AtFirstReport(() =>
            start = coordinator.StartAsync(() => worldAtLaunch = File.ReadAllText(LevelDat))));
        await start!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RestoreResult.Restored, result);
        Assert.Equal("yesterday", worldAtLaunch);
    }

    [Fact]
    public async Task AStartWhoseBackupFailsStillStarts()
    {
        var (server, coordinator) = Stopped(new ThrowingService());
        var launched = false;

        await coordinator.StartAsync(() => launched = true);

        Assert.True(launched);
        Assert.True(coordinator.PlayedSinceBackup);   // so the next chance makes the copy
        var said = Localizer.Get("Msg_BackupFailedStartingAnywayFmt").Split("{0}")[0];
        Assert.Contains(server.Lines, l => l.StartsWith(said, StringComparison.Ordinal));
        Assert.Contains(server.Lines, l => l.Contains(Localizer.Get("Err_DiskFull")));
    }

    [Fact]
    public async Task ABackupAskedForWhileAnotherRunsIsRefusedNotQueued()
    {
        var (server, coordinator) = Running();
        var copy = coordinator.BackupAsync("auto");
        await UntilFlushAsked(server);

        Assert.Equal(BackupResult.Busy, await coordinator.BackupAsync("manual")
            .WaitAsync(TimeSpan.FromSeconds(5)));

        coordinator.ServerSaved();
        await copy;
    }

    [Fact]
    public async Task TheStartsBackupWaitsForTheOneInProgress()
    {
        // Pressing Start right after Stop found the stop's own backup still zipping; the start's was
        // refused as "already running" and the server started anyway, writing to the world being copied.
        var (server, coordinator) = Running();
        var copy = coordinator.BackupAsync("manual");
        await UntilFlushAsked(server);
        var launched = false;

        var start = coordinator.StartAsync(() => launched = true);
        await Task.Delay(200);
        Assert.False(launched);

        coordinator.ServerSaved();
        await copy;
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(launched);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
