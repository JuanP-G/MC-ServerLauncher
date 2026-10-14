using System.Globalization;
using System.IO.Compression;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// What the backups tab says, and when it bothers to read the disk.
/// </summary>
[Collection("avalonia")]
public class BackupsTabTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-btab-" + Guid.NewGuid().ToString("N"));

    private ServerViewModel Server()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "world"));
        File.WriteAllText(Path.Combine(_folder, "world", "level.dat"), "level");
        ServerViewModel? server = null;
        ui.Run(() => server = new ServerViewModel(new ServerConfig { Name = "copias", FolderPath = _folder }));
        return server!;
    }

    [Fact]
    public void NoNextBackupIsAnnouncedWhileTheServerIsStillStarting()
    {
        // The clock only starts once the server is up; before that it holds an earlier run's time,
        // or none, and the tab announced a "next backup" already in the past.
        var server = Server();
        ui.Run(() => server.State = ServerState.Starting);

        Assert.Null(server.NextAutoBackupUtc);
    }

    [Fact]
    public async Task StoppingDoesNotReadTheListIfNobodyOpenedTheTab()
    {
        var server = Server();
        await new WorldBackupService().CreateBackupAsync(server.Config, "manual");

        await server.StopCommand.ExecuteAsync(null);

        Assert.Empty(server.Backups.Items);
        ui.Run(server.Backups.EnsureLoaded);
        Assert.Single(server.Backups.Items);
    }

    /// <summary>Reports synchronously, so the test can act in the middle of a restore.</summary>
    private sealed class DuringRestore(Action act) : IProgress<string>
    {
        private bool _done;

        public void Report(string value)
        {
            if (_done) return;
            _done = true;
            act();
        }
    }

    [Fact]
    public async Task ABackupRefusedBecauseTheWorldIsBusySaysSo()
    {
        // It used to say there was no world to back up yet, as if the server had never run.
        var server = Server();
        var source = Path.Combine(_folder, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "level.dat"), "yesterday");
        var zip = Path.Combine(_folder, "old.zip");
        ZipFile.CreateFromDirectory(source, zip);
        string? said = null;

        await server.BackupCoordinator.RestoreAsync(zip, new DuringRestore(() =>
        {
            server.Backups.BackupNowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            said = server.Backups.StatusText;
        }));

        Assert.Equal(Localizer.Get("Backup_WorldBusy"), said);
    }

    [Fact]
    public void DaysAreNamedInTheAppsLanguage()
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");     // an English Windows
            CultureInfo.CurrentUICulture = new CultureInfo("es-ES");   // the app in Spanish

            var title = BackupDayGroup.TitleFor(new DateTime(2026, 10, 12), new DateTime(2026, 10, 14));

            Assert.StartsWith("Lunes", title);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, ui);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
