using System.IO.Compression;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A restore either puts the whole backup in place or leaves the world as it was.
/// </summary>
/// <remarks>
/// It used to delete the world and then unpack into the empty folder, so a damaged zip or a full
/// disk left half a world. And nothing kept the server from starting meanwhile: not the Start
/// button, and not somebody pressing Join on a server that wakes on demand.
/// </remarks>
[Collection("avalonia")]
public class RestoreSafetyTests : IDisposable
{
    private readonly AvaloniaFixture _ui;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-restore-" + Guid.NewGuid().ToString("N"));
    private string World => Path.Combine(_root, "world");

    public RestoreSafetyTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(Path.Combine(World, "region"));
        File.WriteAllText(Path.Combine(World, "level.dat"), "today");
        File.WriteAllText(Path.Combine(World, "region", "r.0.0.mca"), "today's chunks");
    }

    private string BackupOf(string level)
    {
        var source = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "level.dat"), level);
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(source, zip);
        Directory.Delete(source, recursive: true);
        return zip;
    }

    [Fact]
    public void ADamagedBackupLeavesTheWorldAsItWas()
    {
        var zip = Path.Combine(_root, "damaged.zip");
        File.WriteAllText(zip, "this is not a zip");

        Assert.ThrowsAny<Exception>(() => WorldBackupService.ReplaceWorld(World, zip));

        Assert.Equal("today", File.ReadAllText(Path.Combine(World, "level.dat")));
        Assert.True(File.Exists(Path.Combine(World, "region", "r.0.0.mca")));
        Assert.False(Directory.Exists(World + ".restoring"));
    }

    [Fact]
    public void AGoodBackupReplacesTheWorldCompletely()
    {
        WorldBackupService.ReplaceWorld(World, BackupOf("yesterday"));

        Assert.Equal("yesterday", File.ReadAllText(Path.Combine(World, "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(World, "region")));   // replaced, not merged
        Assert.False(Directory.Exists(World + ".restoring"));
        Assert.False(Directory.Exists(World + ".replaced"));
    }

    [Fact]
    public void LeftoversOfAnInterruptedRestoreAreClearedFirst()
    {
        Directory.CreateDirectory(World + ".restoring");
        File.WriteAllText(Path.Combine(World + ".restoring", "stale.dat"), "half");
        Directory.CreateDirectory(World + ".replaced");

        WorldBackupService.ReplaceWorld(World, BackupOf("yesterday"));

        Assert.False(File.Exists(Path.Combine(World, "stale.dat")));
        Assert.False(Directory.Exists(World + ".restoring"));
        Assert.False(Directory.Exists(World + ".replaced"));
    }

    [Fact]
    public void NothingStartsTheServerWhileItRestores()
    {
        _ui.Run(() =>
        {
            var server = new ServerViewModel(new ServerConfig { Name = "survival", FolderPath = _root });
            Assert.True(server.StartCommand.CanExecute(null));

            server.IsRestoring = true;

            Assert.False(server.CanStart);
            Assert.False(server.StartCommand.CanExecute(null));
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
