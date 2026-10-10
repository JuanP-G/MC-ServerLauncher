using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The backups tab saying what retention actually does: two piles, each against its own limit.
/// </summary>
/// <remarks>
/// The flat list it replaced showed five backups under a setting that read "5 automatic, 5 manual",
/// and it looked as if half of them were missing. They were not: the five were the automatic pile,
/// full, and nobody had made a manual one. These hold that the tab says so.
/// </remarks>
[Collection("avalonia")]
public class BackupsSummaryTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-bsum-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    private void Zip(string trigger, DateTime at)
    {
        var dir = Path.Combine(_folder, "backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"world-{at:yyyyMMdd-HHmmss}--{trigger}.zip");
        File.WriteAllBytes(path, new byte[1024]);
        File.SetLastWriteTime(path, at);
    }

    private ServerBackupsViewModel Open(int automatic = 5, int manual = 5)
    {
        var server = new ServerViewModel(new ServerConfig
        {
            Name = "copias", FolderPath = _folder, BackupRetention = automatic, ManualBackupRetention = manual,
        });
        server.Backups.EnsureLoaded();
        return server.Backups;
    }

    [Fact]
    public void EachPileIsCountedAgainstItsOwnLimit() =>
        ui.Run(() =>
        {
            var now = DateTime.Today.AddHours(12);
            Zip("stop", now);
            Zip("auto", now.AddHours(-1));
            Zip("start", now.AddHours(-2));
            Zip("manual", now.AddHours(-3));
            Zip("before-restore", now.AddHours(-4));

            var backups = Open(automatic: 3, manual: 5);

            Assert.Equal("3 / 3", backups.AutomaticCountText);
            Assert.Equal("2 / 5", backups.ManualCountText);
            Assert.Equal(100, backups.AutomaticFill);
        });

    [Fact]
    public void TheOldestOfAFullPileSaysItIsNextToGo() =>
        ui.Run(() =>
        {
            var now = DateTime.Today.AddHours(12);
            Zip("stop", now);
            Zip("auto", now.AddHours(-1));
            Zip("manual", now.AddHours(-2));

            var backups = Open(automatic: 2, manual: 5);

            var oldestAutomatic = backups.Items.Single(i => i.FileName.Contains("--auto"));
            Assert.True(oldestAutomatic.IsGoing);
            Assert.False(backups.Items.Single(i => i.FileName.Contains("--stop")).IsGoing);
            Assert.False(backups.Items.Single(i => i.IsManual).IsGoing);   // its pile is not full
        });

    [Fact]
    public void TheListIsByDayAndTheFilterKeepsOnePile() =>
        ui.Run(() =>
        {
            var today = DateTime.Today.AddHours(12);
            Zip("stop", today);
            Zip("manual", today.AddDays(-1));
            Zip("auto", today.AddDays(-1).AddHours(-1));

            var backups = Open();

            Assert.Equal(2, backups.Groups.Count);
            Assert.Equal(2, backups.Groups[1].Items.Count);

            backups.ShowManualCommand.Execute(null);
            Assert.Single(backups.Groups);
            Assert.True(Assert.Single(backups.Groups[0].Items).IsManual);

            backups.ShowAllCommand.Execute(null);
            Assert.Equal(3, backups.Groups.Sum(g => g.Items.Count));
        });

    [Fact]
    public void AStoppedServerSaysTheNextOneComesOnStart() =>
        ui.Run(() =>
        {
            var backups = Open();
            Assert.True(backups.IsEmpty);
            Assert.Equal(McServerLauncher.Localization.Localizer.Get("Backup_NextOnStart"), backups.NextBackupText);
        });
}
