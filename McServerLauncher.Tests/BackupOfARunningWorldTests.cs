using System.IO.Compression;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Zipping a world the server still has open.
/// </summary>
/// <remarks>
/// <para>
/// The backups by the clock run with the server up, and it keeps files in the world folder open the
/// whole time: <c>session.lock</c>, which it opens for writing and locks for as long as it runs, and
/// the region files it has read or written. <c>save-off</c> stops it writing to them; it does not
/// close them.
/// </para>
/// <para>
/// The zip used to open every file allowing others only to read, which Windows refuses outright
/// while another process has the file open for writing — so the first backup taken while someone
/// was playing failed on <c>session.lock</c>, and no backup by the clock could ever succeed.
/// </para>
/// </remarks>
public class BackupOfARunningWorldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-hot-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileStream> _heldByTheServer = new();

    public BackupOfARunningWorldTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "world", "region"));
        Directory.CreateDirectory(Path.Combine(_dir, "world", "data"));   // empty, and still part of the world
        File.WriteAllText(Path.Combine(_dir, "world", "level.dat"), "level");
        File.WriteAllText(Path.Combine(_dir, "world", "region", "r.0.0.mca"), "chunks");
        File.WriteAllText(Path.Combine(_dir, "world", "session.lock"), "☃");
    }

    private ServerConfig Config() => new() { Name = "survival", FolderPath = _dir };

    /// <summary>Opens a file the way a running Minecraft server has it open.</summary>
    private void HeldOpen(string relative, FileShare share)
    {
        var stream = new FileStream(Path.Combine(_dir, "world", relative), FileMode.Open, FileAccess.ReadWrite, share);
        _heldByTheServer.Add(stream);
    }

    private static List<string> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task AWorldTheServerHasOpenIsBackedUp()
    {
        // As the JVM opens them: for writing, letting others read and write too.
        HeldOpen("session.lock", FileShare.ReadWrite);
        HeldOpen(Path.Combine("region", "r.0.0.mca"), FileShare.ReadWrite);

        var zip = await new WorldBackupService().CreateBackupAsync(Config(), "auto");

        Assert.NotNull(zip);
        Assert.Contains("region/r.0.0.mca", Entries(zip));
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("region/r.0.0.mca")!.Open());
        Assert.Equal("chunks", reader.ReadToEnd());
    }

    [Fact]
    public async Task TheSessionLockIsLeftOut()
    {
        // Even held so that nobody else may read it at all: the backup never needs it. It is the
        // running server's claim on the folder, not part of the world, and the server writes a new
        // one every time it starts — including after a restore.
        HeldOpen("session.lock", FileShare.None);

        var zip = await new WorldBackupService().CreateBackupAsync(Config(), "auto");

        Assert.NotNull(zip);
        Assert.DoesNotContain(Entries(zip), e => e.EndsWith("session.lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EverythingElseIsStillThereAsItWas()
    {
        var zip = await new WorldBackupService().CreateBackupAsync(Config(), "stop");

        Assert.Equal(new[] { "data/", "level.dat", "region/r.0.0.mca" }, Entries(zip!));
    }

    [Fact]
    public async Task ARestoredBackupIsTheWorldWithoutTheLock()
    {
        var service = new WorldBackupService();
        var zip = await service.CreateBackupAsync(Config(), "manual");
        File.WriteAllText(Path.Combine(_dir, "world", "level.dat"), "changed since");

        await service.RestoreBackupAsync(Config(), zip!);

        Assert.Equal("level", File.ReadAllText(Path.Combine(_dir, "world", "level.dat")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "world", "data")));
        Assert.False(File.Exists(Path.Combine(_dir, "world", "session.lock")));
    }

    public void Dispose()
    {
        foreach (var stream in _heldByTheServer) stream.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
