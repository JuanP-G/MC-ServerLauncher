using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What gets deleted when a new backup is made.
/// </summary>
/// <remarks>
/// One shared count stopped working the day backups started happening by the clock: a server left
/// running overnight fills the whole allowance with hourly copies, and the backup somebody took by
/// hand before trying something — the one they were sure they still had — is the first to go. So
/// the automatic ones and the user's own are counted separately, and zips this app never wrote are
/// not counted at all.
/// </remarks>
public class BackupRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-keep-" + Guid.NewGuid().ToString("N"));

    public BackupRetentionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "world"));
        File.WriteAllText(Path.Combine(_dir, "world", "level.dat"), "bytes");
        Directory.CreateDirectory(Path.Combine(_dir, "backups"));
    }

    /// <summary>A zip already in the folder, aged so the ordering by write time is unambiguous.</summary>
    private void Existing(string name, int minutesOld)
    {
        var path = Path.Combine(_dir, "backups", name);
        File.WriteAllText(path, "not a real zip, but the pruning only looks at names and dates");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesOld));
    }

    private IEnumerable<string> Remaining() =>
        Directory.EnumerateFiles(Path.Combine(_dir, "backups"), "*.zip")
            .Select(Path.GetFileName)!
            .OrderBy(n => n, StringComparer.Ordinal)!;

    private ServerConfig Config(int automatic, int manual) => new()
    {
        Name = "survival", FolderPath = _dir, BackupRetention = automatic, ManualBackupRetention = manual
    };

    [Fact]
    public async Task TheClockNeverDeletesABackupSomebodyTookByHand()
    {
        for (var i = 1; i <= 8; i++) Existing($"world-2026090{i % 10}-120000--auto.zip", 100 + i);
        Existing("world-20260901-090000--manual.zip", 5000);   // the oldest file of all
        Existing("world-20260902-090000--before-restore.zip", 4000);

        await new WorldBackupService().CreateBackupAsync(Config(automatic: 3, manual: 2), "auto");

        var left = Remaining().ToList();
        Assert.Equal(3, left.Count(n => n.Contains("--auto")));
        Assert.Contains(left, n => n.Contains("--manual"));
        Assert.Contains(left, n => n.Contains("--before-restore"));
    }

    [Fact]
    public async Task TheUsersOwnBackupsHaveTheirOwnLimitToo()
    {
        // Not unlimited, or the folder would grow for ever — just counted apart.
        for (var i = 1; i <= 6; i++) Existing($"world-2026090{i}-120000--manual.zip", 60 - i);

        await new WorldBackupService().CreateBackupAsync(Config(automatic: 5, manual: 2), "manual");

        Assert.Equal(2, Remaining().Count(n => n.Contains("--manual")));
    }

    [Fact]
    public async Task TheNewestSurvive()
    {
        Existing("world-20260101-120000--auto.zip", 10_000);  // oldest
        Existing("world-20260601-120000--auto.zip", 5_000);
        Existing("world-20260901-120000--auto.zip", 10);      // newest of the three

        await new WorldBackupService().CreateBackupAsync(Config(automatic: 2, manual: 5), "auto");

        var left = Remaining().ToList();
        Assert.DoesNotContain(left, n => n.Contains("20260101"));
        Assert.Contains(left, n => n.Contains("20260901"));
    }

    [Fact]
    public async Task AZipThisAppDidNotWriteIsLeftAlone()
    {
        // No "--trigger" in the name, so somebody put it there on purpose. Deleting other people's
        // files out of a folder they chose is not this app's business.
        Existing("mi copia del mundo bueno.zip", 9_000);
        for (var i = 1; i <= 6; i++) Existing($"world-2026090{i}-120000--auto.zip", 60 - i);

        await new WorldBackupService().CreateBackupAsync(Config(automatic: 1, manual: 1), "auto");

        Assert.Contains("mi copia del mundo bueno.zip", Remaining());
    }

    [Fact]
    public async Task AHandEditedZeroStillKeepsOne()
    {
        // servers.json is a file people can edit, and "keep zero backups" would mean deleting the
        // one just made.
        Existing("world-20260901-120000--auto.zip", 500);

        var zip = await new WorldBackupService().CreateBackupAsync(Config(automatic: 0, manual: 0), "auto");

        Assert.NotNull(zip);
        Assert.True(File.Exists(zip));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
