using System.IO.Compression;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A backup only ever zips — and a restore only ever replaces — a folder inside the server.
/// </summary>
/// <remarks>
/// <para>
/// Which folder is the world comes from <c>level-name</c> in server.properties, a file the app did
/// not write: a server taken over from a downloaded folder, or a value mistyped in the editor. A
/// restore deletes that folder recursively before unpacking, and it used to take the value as it
/// came.
/// </para>
/// <para>
/// <c>level-name=.</c> deleted the server folder itself, with <c>backups/</c> and the zip being
/// restored inside it, and then failed to unpack a file that no longer existed. <c>..</c> reached
/// the folder above; an absolute path, anywhere at all.
/// </para>
/// </remarks>
public class BackupLevelNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-level-" + Guid.NewGuid().ToString("N"));
    private string Server => Path.Combine(_root, "server");

    public BackupLevelNameTests()
    {
        Directory.CreateDirectory(Path.Combine(Server, "world"));
        File.WriteAllText(Path.Combine(Server, "world", "level.dat"), "level");
        File.WriteAllText(Path.Combine(Server, "server.jar"), "jar");
    }

    private ServerConfig Config() => new() { Name = "survival", FolderPath = Server };

    private void LevelName(string value) =>
        File.WriteAllText(Path.Combine(Server, "server.properties"), "level-name=" + value + "\n");

    /// <summary>A backup made while the world was still where it should be.</summary>
    private string SomeBackup()
    {
        var backups = Path.Combine(Server, "backups");
        Directory.CreateDirectory(backups);
        var zip = Path.Combine(backups, "world-20260101-000000--manual.zip");
        ZipFile.CreateFromDirectory(Path.Combine(Server, "world"), zip);
        return zip;
    }

    [Theory]
    [InlineData("world")]
    [InlineData("survival")]
    [InlineData("worlds/survival")]
    public void AFolderInsideTheServerIsAWorld(string level)
    {
        var world = WorldBackupService.WorldFolderFor(Server, level);

        Assert.NotNull(world);
        Assert.StartsWith(Path.GetFullPath(Server) + Path.DirectorySeparatorChar, world);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("world/../..")]
    [InlineData("backups")]
    [InlineData("backups/world")]
    public void AnythingElseIsNot(string level) =>
        Assert.Null(WorldBackupService.WorldFolderFor(Server, level));

    [Fact]
    public void AnAbsolutePathIsNot() =>
        Assert.Null(WorldBackupService.WorldFolderFor(Server, Path.GetFullPath(_root)));

    [Fact]
    public async Task RestoringWithTheServerFolderAsTheWorldDeletesNothing()
    {
        var zip = SomeBackup();
        LevelName(".");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WorldBackupService().RestoreBackupAsync(Config(), zip));

        Assert.True(File.Exists(zip));
        Assert.True(File.Exists(Path.Combine(Server, "server.jar")));
        Assert.True(File.Exists(Path.Combine(Server, "world", "level.dat")));
    }

    [Fact]
    public async Task RestoringWithAWorldOutsideTheServerDeletesNothing()
    {
        var neighbour = Path.Combine(_root, "neighbour");
        Directory.CreateDirectory(neighbour);
        File.WriteAllText(Path.Combine(neighbour, "keep.txt"), "mine");

        var zip = SomeBackup();
        LevelName("../neighbour");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WorldBackupService().RestoreBackupAsync(Config(), zip));

        Assert.True(File.Exists(Path.Combine(neighbour, "keep.txt")));
    }

    [Fact]
    public async Task ABackupOfTheServerFolderIsRefusedAndSaidSo()
    {
        LevelName(".");
        var said = new List<string>();

        var zip = await new WorldBackupService().CreateBackupAsync(Config(), "start", new SyncLog(said));

        Assert.Null(zip);
        Assert.False(Directory.Exists(Path.Combine(Server, "backups")));
        Assert.Contains(said, line => line.Contains("level-name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWorldInASubfolderIsBackedUpUnderItsOwnName()
    {
        Directory.CreateDirectory(Path.Combine(Server, "worlds", "survival"));
        File.WriteAllText(Path.Combine(Server, "worlds", "survival", "level.dat"), "nested");
        LevelName("worlds/survival");

        var zip = await new WorldBackupService().CreateBackupAsync(Config(), "manual");

        Assert.NotNull(zip);
        Assert.Equal(Path.Combine(Server, "backups"), Path.GetDirectoryName(zip));
        Assert.StartsWith("survival-", Path.GetFileName(zip));
    }

    /// <summary>A progress that records synchronously, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SyncLog(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
