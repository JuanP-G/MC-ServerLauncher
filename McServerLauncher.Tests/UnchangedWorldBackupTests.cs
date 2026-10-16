using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Whether the world on disk is still the one the last backup copied.
/// </summary>
/// <remarks>
/// <para>
/// A server that sleeps when empty and wakes when somebody presses Join made a backup on the way
/// down and another on the way up, even when nobody came in. With a retention of five, a few such
/// cycles were enough to delete every copy somebody had actually played in.
/// </para>
/// <para>
/// Now each backup records what the world looked like, and the start and the stop skip theirs when
/// it still looks the same. Any doubt — a changed file, a missing zip, another world — makes the copy.
/// </para>
/// </remarks>
public class UnchangedWorldBackupTests : IDisposable
{
    private readonly string _server = Path.Combine(Path.GetTempPath(), "mcl-unchanged-" + Guid.NewGuid().ToString("N"));
    private readonly WorldBackupService _backups = new();

    private string World => Path.Combine(_server, "world");

    public UnchangedWorldBackupTests()
    {
        Directory.CreateDirectory(Path.Combine(World, "region"));
        File.WriteAllText(Path.Combine(World, "level.dat"), "level");
        File.WriteAllText(Path.Combine(World, "region", "r.0.0.mca"), "chunks");
    }

    private ServerConfig Config() => new() { Name = "survival", FolderPath = _server };

    /// <summary>What the server does on its way down: writes a file again, as it is.</summary>
    private void Rewrite(string relative)
    {
        var path = Path.Combine(World, relative);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    [Fact]
    public async Task AWorldJustBackedUpIsUpToDate()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");

        Assert.True(_backups.IsUpToDate(config));
    }

    [Fact]
    public void WithNoBackupAtAllItNeverIs() => Assert.False(_backups.IsUpToDate(Config()));

    [Fact]
    public async Task AServerThatTakesCommandsOverRconIsNeverTakenAsUntouched()
    {
        // A /fill sent over RCON changes the world with nobody joining and nothing typed in the app.
        File.WriteAllLines(Path.Combine(_server, "server.properties"), ["enable-rcon=true"]);
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");
        Rewrite("level.dat");

        _backups.MarkUnchanged(config);

        Assert.False(_backups.IsUpToDate(config));
    }

    /// <summary>Reports synchronously, so the test can act at an exact step of the copy.</summary>
    private sealed class AtStep(string step, Action act) : IProgress<string>
    {
        public void Report(string value)
        {
            if (value.StartsWith(step, StringComparison.Ordinal)) act();
        }
    }

    [Fact]
    public async Task TheRecordIsOfTheWorldTheZipHolds()
    {
        // A running server writes again while the zip is being finished. The record used to be taken
        // after the copy, so it described that newer world, and the next start found it "up to date"
        // and made no copy of what was actually played.
        var config = Config();
        var zipDone = Localizer.Get("Msg_BackupCreatedFmt").Split("{0}")[0];

        await _backups.CreateBackupAsync(config, "auto",
            new AtStep(zipDone, () => File.WriteAllText(Path.Combine(World, "level.dat"), "played since")));

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task AFileWrittenAgainIsAChange()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");

        Rewrite("level.dat");

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task AFileAddedIsAChange()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");

        File.WriteAllText(Path.Combine(World, "region", "r.1.0.mca"), "more");

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task AFileRemovedIsAChange()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");

        File.Delete(Path.Combine(World, "region", "r.0.0.mca"));

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task TheServersLockIsNotPartOfTheWorld()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");

        File.WriteAllText(Path.Combine(World, "session.lock"), "☃");

        Assert.True(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task ABackupThatIsGoneCoversNothing()
    {
        var config = Config();
        var zip = await _backups.CreateBackupAsync(config, "stop");

        File.Delete(zip!);

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task PointingLevelNameAtAnotherWorldIsAChange()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");

        // Same files, same times: only where it is differs.
        CopyFolder(World, Path.Combine(_server, "other"));
        File.WriteAllText(Path.Combine(_server, "server.properties"), "level-name=other\n");

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task NobodyPlayingMakesWhatTheServerWroteOnItsWayDownStillTheBackup()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "start");
        Rewrite("level.dat");

        _backups.MarkUnchanged(config);

        Assert.True(_backups.IsUpToDate(config));
    }

    [Fact]
    public void MarkingNeverStandsInForABackupThatWasNotMade()
    {
        var config = Config();

        _backups.MarkUnchanged(config);

        Assert.False(_backups.IsUpToDate(config));
    }

    [Fact]
    public async Task TheRecordIsNotABackupInTheList()
    {
        var config = Config();
        await _backups.CreateBackupAsync(config, "stop");

        Assert.Single(_backups.ListBackups(config));
        Assert.True(File.Exists(Path.Combine(_server, "backups", "last-backup.json")));
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_server, recursive: true); } catch { /* best-effort */ }
    }
}
