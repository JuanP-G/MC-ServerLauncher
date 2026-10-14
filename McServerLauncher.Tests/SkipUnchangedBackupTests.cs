using System.Reflection;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The start's and the stop's backups are skipped when nobody has touched the world since the last.
/// </summary>
/// <remarks>
/// A server that sleeps when empty is woken by anybody pressing Join. Each sleep and wake used to
/// leave a <c>--stop</c> and a <c>--start</c> copy of the same world, even when nobody came back in,
/// until the retention had deleted every copy anybody had played in.
/// </remarks>
[Collection("avalonia")]
public class SkipUnchangedBackupTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-skip-" + Guid.NewGuid().ToString("N"));

    private string World => Path.Combine(_folder, "world");
    private string Backups => Path.Combine(_folder, "backups");

    private ServerViewModel Server()
    {
        Directory.CreateDirectory(World);
        File.WriteAllText(Path.Combine(World, "level.dat"), "level");
        ServerViewModel? server = null;
        ui.Run(() => server = new ServerViewModel(new ServerConfig { Name = "survival", FolderPath = _folder }));
        return server!;
    }

    private static Task<bool> BackupUnlessUnchanged(ServerViewModel server, string trigger) =>
        (Task<bool>)typeof(ServerViewModel)
            .GetMethod("BackupUnlessUnchangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, [trigger, false])!;

    private static Task NoteUntouchedWorld(ServerViewModel server) =>
        (Task)typeof(ServerViewModel)
            .GetMethod("NoteUntouchedWorldAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, [])!;

    private static void SetPlayed(ServerViewModel server, bool played) =>
        typeof(ServerViewModel)
            .GetField("_playedSinceBackup", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(server, played);

    private int Zips() => Directory.EnumerateFiles(Backups, "*.zip").Count();

    /// <summary>What a running server does to its world on the way down, even with nobody on.</summary>
    private void ServerWritesOnItsWayDown()
    {
        var level = Path.Combine(World, "level.dat");
        File.SetLastWriteTimeUtc(level, File.GetLastWriteTimeUtc(level).AddMinutes(1));
    }

    [Fact]
    public async Task AWakeFindingTheWorldAsItWasMakesNoCopy()
    {
        var server = Server();
        await server.RunBackupAsync("manual");

        Assert.True(await BackupUnlessUnchanged(server, "start"));

        Assert.Equal(1, Zips());
    }

    [Fact]
    public async Task AWorldChangedSinceGetsItsCopy()
    {
        var server = Server();
        await server.RunBackupAsync("manual");
        File.WriteAllText(Path.Combine(World, "level.dat"), "edited by hand while stopped");

        Assert.True(await BackupUnlessUnchanged(server, "start"));

        Assert.Equal(2, Zips());
    }

    [Fact]
    public async Task NobodyPlayingSkipsTheStopsCopyAndTheNextStarts()
    {
        var server = Server();
        await server.RunBackupAsync("manual");
        SetPlayed(server, false);                 // the start was covered and nobody came in

        ServerWritesOnItsWayDown();
        await NoteUntouchedWorld(server);

        Assert.True(await BackupUnlessUnchanged(server, "stop"));
        Assert.True(await BackupUnlessUnchanged(server, "start"));
        Assert.Equal(1, Zips());
    }

    [Fact]
    public async Task SomebodyPlayingStillGetsTheStopsCopy()
    {
        var server = Server();
        await server.RunBackupAsync("manual");
        SetPlayed(server, true);                  // somebody joined

        ServerWritesOnItsWayDown();
        await NoteUntouchedWorld(server);

        Assert.True(await BackupUnlessUnchanged(server, "stop"));
        Assert.Equal(2, Zips());
    }

    [Fact]
    public async Task AFreshServerAssumesTheWorldChanged()
    {
        // Nothing in this session has looked at the world yet: it may have been edited, or swapped.
        var server = Server();
        await server.RunBackupAsync("manual");
        ServerWritesOnItsWayDown();

        await NoteUntouchedWorld(server);

        Assert.True(await BackupUnlessUnchanged(server, "stop"));
        Assert.Equal(2, Zips());
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
