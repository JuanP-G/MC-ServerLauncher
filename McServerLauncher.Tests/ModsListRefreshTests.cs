using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The Mods tab showing jars that arrived without the user putting them there.
/// </summary>
/// <remarks>
/// The panel reads the folder when it is built, which for a new server is before crossplay has
/// downloaded Geyser and Floodgate into it. The two jars were then on disk and loaded by the
/// server while the tab showed an empty list — and the refresh button is not something anyone
/// thinks to press for mods they never installed.
/// </remarks>
public class ModsListRefreshTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-mods-" + Guid.NewGuid().ToString("N"));

    private ServerConfig Config(ServerType type) => new()
    {
        Name = "test", FolderPath = _folder, Type = type, GameVersion = "1.21.1"
    };

    [Fact]
    public void JarsWrittenAfterThePanelWasBuiltShowUpOnReload()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "mods"));
        var mods = new ServerModsViewModel(Config(ServerType.NeoForge));

        Assert.Empty(mods.InstalledMods);   // the state a new server starts in

        // What CrossplayService.InstallAsync does, reduced to its effect on the folder.
        File.WriteAllText(Path.Combine(_folder, "mods", "Geyser-Neoforge-2.11.2-b1230.jar"), "");
        File.WriteAllText(Path.Combine(_folder, "mods", "Floodgate-Neoforge-2.2.6-b67.jar"), "");

        Assert.Empty(mods.InstalledMods);   // still stale: nothing re-reads the folder by itself

        mods.ReloadInstalled();

        Assert.Equal(
            new[] { "Floodgate-Neoforge-2.2.6-b67.jar", "Geyser-Neoforge-2.11.2-b1230.jar" },
            mods.InstalledMods.Select(m => m.FileName).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void PaperLooksInPluginsInstead()
    {
        // Same bug, other folder: Geyser goes to plugins/ on Paper, and a reload that read mods/
        // would leave that case exactly as broken as before.
        Directory.CreateDirectory(Path.Combine(_folder, "plugins"));
        var mods = new ServerModsViewModel(Config(ServerType.Paper));

        File.WriteAllText(Path.Combine(_folder, "plugins", "Geyser-Spigot.jar"), "");
        mods.ReloadInstalled();

        Assert.Equal("Geyser-Spigot.jar", Assert.Single(mods.InstalledMods).FileName);
    }

    [Fact]
    public void ReloadingAFolderThatIsNotThereIsHarmless()
    {
        // Crossplay can fail before creating anything — a resolve error, no network. The reload
        // still runs in that case and must not take the app down with it.
        Directory.CreateDirectory(_folder);
        var mods = new ServerModsViewModel(Config(ServerType.Fabric));

        mods.ReloadInstalled();

        Assert.Empty(mods.InstalledMods);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
