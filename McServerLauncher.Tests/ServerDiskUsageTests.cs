using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What a server's folder takes, and which part of it is which: the line under the server's status
/// and its Storage page.
/// </summary>
public class ServerDiskUsageTests : IDisposable
{
    private readonly string _server = Path.Combine(Path.GetTempPath(), "mcl-disk-" + Guid.NewGuid().ToString("N"));

    public ServerDiskUsageTests() => Directory.CreateDirectory(_server);

    public void Dispose()
    {
        try { Directory.Delete(_server, recursive: true); } catch { }
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_server, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private ServerDiskUsage Measure(ServerType type = ServerType.Paper) =>
        ServerDiskUsageService.Measure(new ServerConfig { Name = "s", FolderPath = _server, Type = type })!;

    [Fact]
    public void EachPartIsCountedOnceAndTheRestIsWhatIsLeft()
    {
        Write("server.jar", 1000);
        Write("world/level.dat", 300);
        Write("world/region/r.0.0.mca", 700);
        Write("world_nether/DIM-1/region/r.0.0.mca", 200);
        Write("world_the_end/DIM1/region/r.0.0.mca", 100);
        Write("backups/world-1.zip", 400);
        Write("backups/world-2.zip", 500);
        Write("plugins/Geyser.jar", 250);
        Write("logs/latest.log", 40);
        Write("crash-reports/crash.txt", 10);
        Write("libraries/a/b.jar", 600);

        var u = Measure();

        Assert.Equal(4100, u.Total);
        Assert.Equal(1300, u.World);     // the overworld and both Bukkit dimensions
        Assert.Equal(900, u.Backups);
        Assert.Equal(2, u.BackupCount);
        Assert.Equal(250, u.Content);
        Assert.Equal(50, u.Logs);
        Assert.Equal(1600, u.Other);     // the jar and the libraries
    }

    [Fact]
    public void TheWorldIsTheOneLevelNameNames()
    {
        File.WriteAllText(Path.Combine(_server, "server.properties"), "level-name=survival\n");
        Write("survival/level.dat", 500);
        Write("world/level.dat", 70);   // an old world the server no longer uses: not "the world"

        var u = Measure();

        Assert.Equal(500, u.World);
        // and the properties file itself, which belongs to nothing in particular
        Assert.Equal(70 + new FileInfo(Path.Combine(_server, "server.properties")).Length, u.Other);
    }

    [Fact]
    public void ModsCountAsContentOnAModdedServerAndPluginsDoNot()
    {
        Write("mods/sodium.jar", 300);
        Write("plugins/leftover.jar", 20);

        var u = Measure(ServerType.Fabric);

        Assert.Equal(300, u.Content);
        Assert.Equal(20, u.Other);
    }

    [Fact]
    public void AnUnfinishedBackupTakesRoomButIsNotACopy()
    {
        Write("backups/world-1.zip", 100);
        Write("backups/world-2.zip.tmp", 50);

        var u = Measure();

        Assert.Equal(150, u.Backups);
        Assert.Equal(1, u.BackupCount);
    }

    [Fact]
    public void AMissingFolderHasNoMeasurement()
    {
        var config = new ServerConfig { Name = "gone", FolderPath = Path.Combine(_server, "nope") };
        Assert.Null(ServerDiskUsageService.Measure(config));
    }

    [Fact]
    public void ACancelledMeasurementStops()
    {
        Write("world/level.dat", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ServerDiskUsageService.Measure(new ServerConfig { Name = "s", FolderPath = _server }, cts.Token));
    }

    [Fact]
    public void OtherIsNeverNegative()
    {
        // Parts measured as more than the total can only come from a race with the server writing;
        // the bar must still not get a negative piece.
        var u = new ServerDiskUsage(Total: 100, World: 80, Backups: 30, Content: 0, Logs: 0, BackupCount: 1);
        Assert.Equal(0, u.Other);
    }
}
