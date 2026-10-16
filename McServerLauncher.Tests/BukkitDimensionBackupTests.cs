using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Paper and Purpur keep the Nether and the End beside the world, and they are backed up and
/// restored with it.
/// </summary>
/// <remarks>
/// Only the <c>level-name</c> folder was zipped, which holds every dimension on vanilla and on the
/// modded loaders but not on a Bukkit server: there they are <c>world_nether</c> and
/// <c>world_the_end</c>. Every backup of a Paper server lacked them, a restore put an old overworld
/// beside today's Nether and End, and "nothing changed since the last backup" never looked at them.
/// </remarks>
public class BukkitDimensionBackupTests : IDisposable
{
    private readonly string _server = Path.Combine(Path.GetTempPath(), "mcl-bukkit-" + Guid.NewGuid()
        .ToString("N"));
    private readonly WorldBackupService _backups = new();

    private string World => Path.Combine(_server, "world");
    private string Nether => Path.Combine(_server, "world_nether");
    private string End => Path.Combine(_server, "world_the_end");
    private string NetherRegion => Path.Combine(Nether, "DIM-1", "region", "r.0.0.mca");

    public BukkitDimensionBackupTests()
    {
        Directory.CreateDirectory(World);
        File.WriteAllText(Path.Combine(World, "level.dat"), "overworld today");
        Directory.CreateDirectory(Path.GetDirectoryName(NetherRegion)!);
        File.WriteAllText(NetherRegion, "nether today");
        File.WriteAllText(Path.Combine(Nether, "session.lock"), "held");
        Directory.CreateDirectory(Path.Combine(End, "DIM1"));
        File.WriteAllText(Path.Combine(End, "DIM1", "level.dat"), "end today");
    }

    private ServerConfig Config() => new() { Name = "paper", FolderPath = _server };

    private static List<string> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public async Task TheNetherAndTheEndAreInTheBackup()
    {
        var zip = await _backups.CreateBackupAsync(Config(), "manual");

        var entries = Entries(zip!);
        Assert.Contains("level.dat", entries);   // the world stays at the root, as in every older zip
        Assert.Contains(".mcserverlauncher-dimensions/_nether/DIM-1/region/r.0.0.mca", entries);
        Assert.Contains(".mcserverlauncher-dimensions/_the_end/DIM1/level.dat", entries);
        Assert.DoesNotContain(entries, e => e.EndsWith("session.lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlayingInTheNetherIsAChange()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");
        Assert.True(_backups.IsUpToDate(config));

        File.WriteAllText(NetherRegion, "somebody built a portal");

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task ARestoreBringsTheNetherAndTheEndBackToo()
    {
        var config = Config();
        var zip = await _backups.CreateBackupAsync(config, "manual");
        File.WriteAllText(Path.Combine(World, "level.dat"), "overworld later");
        File.WriteAllText(NetherRegion, "nether later");
        Directory.Delete(End, recursive: true);

        await _backups.RestoreBackupAsync(config, zip!);

        Assert.Equal("overworld today", File.ReadAllText(Path.Combine(World, "level.dat")));
        Assert.Equal("nether today", File.ReadAllText(NetherRegion));
        Assert.Equal("end today", File.ReadAllText(Path.Combine(End, "DIM1", "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(World, ".mcserverlauncher-dimensions")));
        Assert.Empty(Directory.GetDirectories(_server, "*.restoring"));
        Assert.Empty(Directory.GetDirectories(_server, "*.replaced"));
    }

    [Fact]
    public void AZipFromBeforeLeavesTheNetherAsItIs()
    {
        var source = Path.Combine(_server, "old-world");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "level.dat"), "overworld long ago");
        var zip = Path.Combine(_server, "old.zip");
        ZipFile.CreateFromDirectory(source, zip);

        WorldBackupService.ReplaceWorld(World, zip);

        Assert.Equal("overworld long ago", File.ReadAllText(Path.Combine(World, "level.dat")));
        Assert.Equal("nether today", File.ReadAllText(NetherRegion));
    }

    [Fact]
    public async Task ARestoreThatCannotPlaceTheEndPutsEverythingBack()
    {
        var config = Config();
        var zip = await _backups.CreateBackupAsync(config, "manual");
        File.WriteAllText(Path.Combine(World, "level.dat"), "overworld later");
        File.WriteAllText(NetherRegion, "nether later");

        // Something occupies the name the End is moved aside to, after the world and the Nether
        // have already been swapped.
        File.WriteAllText(End + ".replaced", "in the way");
        Assert.ThrowsAny<IOException>(() => WorldBackupService.ReplaceWorld(World, zip!));

        Assert.Equal("overworld later", File.ReadAllText(Path.Combine(World, "level.dat")));
        Assert.Equal("nether later", File.ReadAllText(NetherRegion));
        Assert.Equal("end today", File.ReadAllText(Path.Combine(End, "DIM1", "level.dat")));
        Assert.Empty(Directory.GetDirectories(_server, "*.restoring"));
    }

    [Fact]
    public void AWorldWithoutDimensionsBesideItHashesAsItAlwaysDid()
    {
        // So updating the app does not make every vanilla and modded server copy its world again.
        Directory.Delete(Nether, recursive: true);
        Directory.Delete(End, recursive: true);
        Directory.CreateDirectory(Path.Combine(World, "region"));
        File.WriteAllText(Path.Combine(World, "region", "r.0.0.mca"), "chunks");
        File.WriteAllText(Path.Combine(World, "session.lock"), "held");

        var lines = new DirectoryInfo(World).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Name: Path.GetRelativePath(World, f.FullName)
                .Replace(Path.DirectorySeparatorChar, '/'), File: f))
            .Where(f => f.Name != "session.lock")
            .Select(f => $"{f.Name}|{f.File.Length}|{f.File.LastWriteTimeUtc.Ticks}")
            .Order(StringComparer.Ordinal);
        var before = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(World) + "\n" + string.Join("\n", lines))));

        Assert.Equal(before, WorldBackupService.Fingerprint(World,
            WorldBackupService.DimensionsBeside(_server, "world")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_server, recursive: true); } catch { /* best-effort */ }
    }
}
