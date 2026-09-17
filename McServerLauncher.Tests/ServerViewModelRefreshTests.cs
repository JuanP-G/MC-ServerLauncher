using System.ComponentModel;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A server being converted, moved or renamed while the app stays open.
/// </summary>
/// <remarks>
/// <para>
/// The report these come from: change a server's type or its Minecraft version and nothing on
/// screen moved — the badge, the version, the Mods tab and its filter chips went on describing what
/// the folder used to be, and only restarting the app told the truth. Installing a mod then failed
/// minutes later, because the browser was offering files chosen for a version the server no longer
/// ran.
/// </para>
/// <para>
/// Nothing here calls <c>RefreshFromConfig()</c>. The point is that nobody has to: the config
/// announces its own changes and <see cref="ServerConfigEffects"/> says what each one costs. A test
/// that asked for a refresh would pass while the thing it is meant to protect was broken.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ServerViewModelRefreshTests : IDisposable
{
    private readonly AvaloniaFixture _ui;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-vmrefresh-" + Guid.NewGuid().ToString("N"));

    public ServerViewModelRefreshTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_root);
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string FolderWithPort(string name, int port)
    {
        var path = Folder(name);
        File.WriteAllText(Path.Combine(path, "server.properties"),
            $"motd=Un servidor de {name}\nserver-port={port}\nmax-players=20\n");
        return path;
    }

    private void Jar(string folder, string contentFolder, string jar)
    {
        var dir = Path.Combine(folder, contentFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, jar), "");
    }

    private void Backup(string folder, string fileName)
    {
        var dir = Path.Combine(folder, "backups");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "");
    }

    private static List<string> Watch(INotifyPropertyChanged source)
    {
        var seen = new List<string>();
        source.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? string.Empty);
        return seen;
    }

    /// <summary>Builds a server view model on the UI thread and runs an assertion against it.</summary>
    private void WithServer(ServerConfig config, Action<ServerViewModel> assert) =>
        _ui.Run(() =>
        {
            var server = new ServerViewModel(config);
            try { assert(server); }
            finally { server.ShutdownAsync().GetAwaiter().GetResult(); }
        });

    [Fact]
    public void ConvertingTheTypeBringsTheModsTabWithIt()
    {
        var folder = Folder("convert");
        Jar(folder, "mods", "lithium.jar");
        var config = new ServerConfig
        {
            Name = "convert", FolderPath = folder, Type = ServerType.Fabric, GameVersion = "1.21.1"
        };

        WithServer(config, server =>
        {
            Assert.True(server.IsModded);
            Assert.Equal("lithium.jar", Assert.Single(server.Mods.InstalledMods).FileName);

            Jar(folder, "plugins", "essentials.jar");
            var card = Watch(server);
            var tab = Watch(server.Mods);

            // Exactly what InstallLoaderDialog does, and nothing else.
            config.Type = ServerType.Paper;

            Assert.Equal("Paper", server.ServerTypeText);
            Assert.Contains(nameof(server.IsModded), card);
            Assert.Contains(nameof(server.ServerTypeText), card);
            Assert.Contains(nameof(server.ServerTypeBrush), card);

            // The panel changed family, so it reads the other folder now.
            Assert.True(server.Mods.IsPluginBased);
            Assert.Contains(nameof(server.Mods.ContentTabTitle), tab);
            Assert.Equal("essentials.jar", Assert.Single(server.Mods.InstalledMods).FileName);
        });
    }

    [Fact]
    public void ANewMinecraftVersionReachesTheCardAndTheStoreChips()
    {
        // The case that had no coverage at all: the type does not change, so the old code rebuilt
        // nothing and the store went on filtering for a version the server had left behind.
        var config = new ServerConfig
        {
            Name = "version", FolderPath = Folder("version"),
            Type = ServerType.Fabric, GameVersion = "1.21.1"
        };

        WithServer(config, server =>
        {
            Assert.Equal("1.21.1", server.GameVersionText);

            var card = Watch(server);
            var tab = Watch(server.Mods);
            config.GameVersion = "1.21.4";

            Assert.Equal("1.21.4", server.GameVersionText);
            Assert.Contains(nameof(server.GameVersionText), card);
            Assert.Equal("1.21.4", server.Mods.FilterVersionText);
            Assert.Contains(nameof(server.Mods.FilterVersionText), tab);
            Assert.Contains("1.21.4", server.Mods.ActiveFilters.Select(f => f.Label));
            Assert.DoesNotContain("1.21.1", server.Mods.ActiveFilters.Select(f => f.Label));
        });
    }

    [Fact]
    public void MovingTheFolderTakesThePortTheModsAndTheBackupsWithIt()
    {
        // Setting the folder used to refresh the MOTD and nothing else, so the port, the installed
        // list and the backup list went on describing a directory that was no longer there.
        var before = FolderWithPort("antes", 25565);
        Jar(before, "mods", "lithium.jar");
        Backup(before, "world-20260101-120000--manual.zip");

        var after = FolderWithPort("despues", 25570);
        Jar(after, "mods", "sodium.jar");

        var config = new ServerConfig
        {
            Name = "mover", FolderPath = before, Type = ServerType.Fabric, GameVersion = "1.21.1"
        };

        WithServer(config, server =>
        {
            server.Backups.EnsureLoaded();     // the tab is lazy; open it before expecting a refresh
            Assert.Equal("25565", server.PortText);
            Assert.Single(server.Backups.Items);
            Assert.Equal("lithium.jar", Assert.Single(server.Mods.InstalledMods).FileName);

            config.FolderPath = after;

            Assert.Equal("25570", server.PortText);
            Assert.Contains("despues", server.MotdText);
            Assert.Equal("sodium.jar", Assert.Single(server.Mods.InstalledMods).FileName);
            Assert.Empty(server.Backups.Items);
        });
    }

    [Fact]
    public void RenamingReachesTheCardWithoutAnybodyAskingIt()
    {
        // The edit dialog writes the name into the config as the user types; the list binds the
        // view model's own Name, so the two have to stay the same value.
        var config = new ServerConfig { Name = "viejo", FolderPath = Folder("renombrar") };

        WithServer(config, server =>
        {
            Assert.Equal("viejo", server.Name);

            config.Name = "nuevo";

            Assert.Equal("nuevo", server.Name);
        });
    }

    [Fact]
    public void TurningCrossplayOffHidesItsPanel()
    {
        // Only ever switched on before: nothing was listening for it going the other way, so the
        // Bedrock panel stayed on screen for a server that no longer had crossplay.
        var config = new ServerConfig
        {
            Name = "bedrock", FolderPath = Folder("bedrock"),
            Type = ServerType.Paper, GameVersion = "1.21.1",
            CrossplayEnabled = true, BedrockPort = 19132
        };

        WithServer(config, server =>
        {
            Assert.True(server.IsCrossplayOn);
            Assert.Equal("19132", server.BedrockLocalPortText);

            var seen = Watch(server);
            config.CrossplayEnabled = false;
            config.BedrockPort = 19140;

            Assert.False(server.IsCrossplayOn);
            Assert.Equal("19140", server.BedrockLocalPortText);
            Assert.Contains(nameof(server.IsCrossplayOn), seen);
            Assert.Contains(nameof(server.BedrockLocalPortText), seen);
        });
    }

    [Fact]
    public void AShutDownServerStopsFollowingItsConfig()
    {
        // The subscription is let go in ShutdownAsync. Without that, a view model the app has
        // finished with keeps reacting to a config somebody else may still be editing.
        var config = new ServerConfig
        {
            Name = "suelto", FolderPath = Folder("suelto"), Type = ServerType.Vanilla
        };

        _ui.Run(() =>
        {
            var server = new ServerViewModel(config);
            server.ShutdownAsync().GetAwaiter().GetResult();

            var seen = Watch(server);
            config.Type = ServerType.Fabric;

            Assert.Empty(seen);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
