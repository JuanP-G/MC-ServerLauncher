using System.ComponentModel;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Converting a server, or moving it to another Minecraft version, while the app stays open.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServerConfig"/> is a plain object with no change notification, and the dialogs edit
/// it in place. Everything computed from it — the badge, the version, the Mods tab and its filter
/// chips — therefore only recomputes when something asks, and nothing did: a finished conversion
/// looked like one that had not run until the app was restarted, while the searches underneath
/// were already using the new values. These tests are about the two agreeing again.
/// </para>
/// <para>
/// These stay at the panel's own level, calling <c>RefreshFromConfig</c> directly. The same ground
/// covered through a real <c>ServerViewModel</c>, with nobody asking for a refresh at all, is in
/// <c>ServerViewModelRefreshTests</c> — which only became possible once a constructor stopped
/// starting timers and sockets.
/// </para>
/// </remarks>
public class LiveConfigRefreshTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-live-" + Guid.NewGuid().ToString("N"));

    private ServerConfig Config(ServerType type, string version = "1.21.1") => new()
    {
        Name = "test", FolderPath = _folder, Type = type, GameVersion = version
    };

    private void Jar(string folder, string name)
    {
        var dir = Path.Combine(_folder, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "");
    }

    private static List<string> Watch(INotifyPropertyChanged source)
    {
        var seen = new List<string>();
        source.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? string.Empty);
        return seen;
    }

    [Fact]
    public void BecomingAPluginServerRenamesTheTabAndReadsTheOtherFolder()
    {
        Jar("mods", "lithium.jar");
        var config = Config(ServerType.Fabric);
        var mods = new ServerModsViewModel(config);
        Assert.Equal("lithium.jar", Assert.Single(mods.InstalledMods).FileName);

        // What InstallLoaderDialog does: the same object, new values.
        Jar("plugins", "essentials.jar");
        config.Type = ServerType.Paper;
        var seen = Watch(mods);
        mods.RefreshFromConfig();

        Assert.True(mods.IsPluginBased);
        Assert.Equal("essentials.jar", Assert.Single(mods.InstalledMods).FileName);
        Assert.Equal("Paper", mods.FilterTypeText);
        // Without these the tab keeps the old family's wording over the new family's content.
        Assert.Contains(nameof(mods.IsPluginBased), seen);
        Assert.Contains(nameof(mods.ContentTabTitle), seen);
        Assert.Contains(nameof(mods.InstalledTitle), seen);
    }

    [Fact]
    public void ANewMinecraftVersionReachesTheFilterChips()
    {
        // The case that kept failing later: same loader, one version up. The type had not changed,
        // so nothing refreshed, and the browser went on offering mods chosen for 1.21.1 while every
        // install resolved against 1.21.4 and found nothing.
        var config = Config(ServerType.Fabric);
        var mods = new ServerModsViewModel(config);
        Assert.Equal("1.21.1", mods.FilterVersionText);

        config.GameVersion = "1.21.4";
        var seen = Watch(mods);
        mods.RefreshFromConfig();

        Assert.Equal("1.21.4", mods.FilterVersionText);
        Assert.Contains(nameof(mods.FilterVersionText), seen);
        Assert.Contains("1.21.4", mods.ActiveFilters.Select(f => f.Label));
        Assert.DoesNotContain("1.21.1", mods.ActiveFilters.Select(f => f.Label));
    }

    [Fact]
    public void TheCategoryChipsAreRebuiltOnlyWhenTheFamilyChanges()
    {
        var config = Config(ServerType.Fabric);
        var mods = new ServerModsViewModel(config);
        var before = mods.BrowseTags;

        // A version change leaves the catalogue alone — rebuilding would throw away the categories
        // the user picked for no reason.
        config.GameVersion = "1.21.4";
        mods.RefreshFromConfig();
        Assert.Same(before, mods.BrowseTags);

        // Crossing into the other family does not: Modrinth's plugin categories are not its mod
        // ones, and a chip that no longer exists there filters on nothing.
        config.Type = ServerType.Paper;
        mods.RefreshFromConfig();
        Assert.NotSame(before, mods.BrowseTags);
        Assert.Empty(mods.SelectedTags);
    }

    [Fact]
    public void AFolderRegisteredTodayIsRecognisedWithoutARestart()
    {
        // Registering an existing folder used to store it as Vanilla with no version, because the
        // only look at the disk happened at startup. No Mods tab, and no version to resolve a mod
        // against, until the app was closed and opened again.
        Directory.CreateDirectory(Path.Combine(
            _folder, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.2.0"));
        File.WriteAllText(Path.Combine(
            _folder, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.2.0", "win_args.txt"), "");

        var config = new ServerConfig { Name = "added", FolderPath = _folder };
        Assert.True(new ServerDetectionService().DetectAndFill(config));

        Assert.Equal(ServerType.Forge, config.Type);
        Assert.Equal("1.20.1", config.GameVersion);
        Assert.Equal("47.2.0", config.ModLoaderVersion);
    }

    [Fact]
    public void DetectionLeavesAConfigThatAlreadyKnowsWhatItIsAlone()
    {
        // The same call runs on a folder the user described by hand, so it has to be a no-op there:
        // guessing over a stated answer would be a worse bug than the one it fixes.
        Directory.CreateDirectory(_folder);
        var config = Config(ServerType.Fabric, "1.21.1");

        Assert.False(new ServerDetectionService().DetectAndFill(config));
        Assert.Equal(ServerType.Fabric, config.Type);
        Assert.Equal("1.21.1", config.GameVersion);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
