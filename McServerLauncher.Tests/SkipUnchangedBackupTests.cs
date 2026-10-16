using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The start's and the stop's backups are skipped when nobody has touched the world since the last.
/// </summary>
/// <remarks>
/// A server that sleeps when empty is woken by anybody pressing Join. Each sleep and wake used to
/// leave a <c>--stop</c> and a <c>--start</c> copy of the same world, even when nobody came back in,
/// until the retention had deleted every copy anybody had played in.
/// </remarks>
public class SkipUnchangedBackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-skip-" + Guid.NewGuid()
        .ToString("N"));

    private string World => Path.Combine(_folder, "world");
    private string Backups => Path.Combine(_folder, "backups");

    private (FakeBackupHost Server, BackupCoordinator Coordinator) Server()
    {
        Directory.CreateDirectory(World);
        File.WriteAllText(Path.Combine(World, "level.dat"), "level");
        var server = new FakeBackupHost(_folder);
        return (server, new BackupCoordinator(server, new WorldBackupService()));
    }

    private int Zips() => Directory.EnumerateFiles(Backups, "*.zip").Count();

    /// <summary>What a running server does to its world on the way down, even with nobody on.</summary>
    private void ServerWritesOnItsWayDown()
    {
        var level = Path.Combine(World, "level.dat");
        File.SetLastWriteTimeUtc(level, File.GetLastWriteTimeUtc(level).AddMinutes(1));
    }

    /// <summary>A start that finds the world, then a JVM that runs it.</summary>
    private static Task Start(FakeBackupHost server, BackupCoordinator coordinator) =>
        coordinator.StartAsync(() => server.IsProcessRunning = true);

    private Task Stop(FakeBackupHost server, BackupCoordinator coordinator) =>
        coordinator.StopAsync(() => server.Stop(ServerWritesOnItsWayDown), backupAfter: true);

    [Fact]
    public async Task AWakeFindingTheWorldAsItWasMakesNoCopy()
    {
        var (server, coordinator) = Server();
        await coordinator.BackupAsync("manual");

        await Start(server, coordinator);

        Assert.Equal(1, Zips());
        Assert.False(coordinator.PlayedSinceBackup);
    }

    [Fact]
    public async Task AWorldChangedSinceGetsItsCopy()
    {
        var (server, coordinator) = Server();
        await coordinator.BackupAsync("manual");
        File.WriteAllText(Path.Combine(World, "level.dat"), "edited by hand while stopped");

        await Start(server, coordinator);

        Assert.Equal(2, Zips());
    }

    [Fact]
    public async Task NobodyPlayingSkipsTheStopsCopyAndTheNextStarts()
    {
        var (server, coordinator) = Server();
        await coordinator.BackupAsync("manual");
        await Start(server, coordinator);         // covered, and nobody comes in

        await Stop(server, coordinator);
        await Start(server, coordinator);

        Assert.Equal(1, Zips());
    }

    [Fact]
    public async Task SomebodyPlayingStillGetsTheStopsCopy()
    {
        var (server, coordinator) = Server();
        await coordinator.BackupAsync("manual");
        await Start(server, coordinator);
        coordinator.NoteWorldTouched();           // somebody joined

        await Stop(server, coordinator);

        Assert.Equal(2, Zips());
    }

    [Fact]
    public async Task AFreshServerAssumesTheWorldChanged()
    {
        // Nothing in this session has looked at the world yet: it may have been edited, or swapped.
        var (server, coordinator) = Server();
        await coordinator.BackupAsync("manual");
        server.IsProcessRunning = true;

        await Stop(server, coordinator);

        Assert.Equal(2, Zips());
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
