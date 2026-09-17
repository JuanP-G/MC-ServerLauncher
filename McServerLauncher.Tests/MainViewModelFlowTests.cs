using System.Text.Json;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The whole way through: a server list on disk, a conversion, and the file afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The layers below this each have their own tests, and each of them passed while the app was
/// visibly wrong — that is what a missing seam costs. This one owns a real
/// <see cref="MainViewModel"/> over a temporary data folder and checks the two things a user
/// actually sees: the card changed, and the change is in <c>servers.json</c>.
/// </para>
/// <para>
/// <c>Activate()</c> is never called, so nothing here starts the Playit agent, calls GitHub or opens
/// a socket. The dialogs are not opened either — they need an owner window — so a conversion is
/// performed the way <c>InstallLoaderDialog</c> performs it: by writing into the live config.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class MainViewModelFlowTests : IDisposable
{
    private readonly AvaloniaFixture _ui;

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "mcl-flow-" + Guid.NewGuid().ToString("N"));

    public MainViewModelFlowTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_dataDir);
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dataDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteServersJson(params ServerConfig[] servers) =>
        File.WriteAllText(
            Path.Combine(_dataDir, "servers.json"),
            JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Reads servers.json back as raw JSON, so the assertions are about the file.</summary>
    private JsonElement ReadServersJson()
    {
        var text = File.ReadAllText(Path.Combine(_dataDir, "servers.json"));
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ConvertingAServerChangesTheCardAndTheFile()
    {
        // The reported bug, end to end. Everything below is what InstallLoaderDialog does to the
        // config after the download succeeds, plus the Save the edit flow does on the way out.
        var folder = Folder("survival");
        Directory.CreateDirectory(Path.Combine(folder, "mods"));
        WriteServersJson(new ServerConfig
        {
            Id = "conv1", Name = "survival", FolderPath = folder,
            Type = ServerType.Vanilla, GameVersion = "1.21.1"
        });

        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            var server = Assert.Single(main.Servers);

            Assert.False(server.IsModded);        // no Mods tab yet
            Assert.Equal("Vanilla", server.ServerTypeText);

            server.Config.Type = ServerType.Fabric;
            server.Config.GameVersion = "1.21.4";
            server.Config.ModLoaderVersion = "0.16.14";
            server.Config.JarFile = "fabric-server.jar";
            main.SaveCommand.Execute(null);

            // The card, without anybody asking it to refresh.
            Assert.True(server.IsModded);
            Assert.Equal("Fabric", server.ServerTypeText);
            Assert.Equal("1.21.4", server.GameVersionText);
            Assert.Equal("1.21.4", server.Mods.FilterVersionText);

            // And the file, which is what survives closing the app.
            var saved = ReadServersJson()[0];
            Assert.Equal((int)ServerType.Fabric, saved.GetProperty("Type").GetInt32());
            Assert.Equal("1.21.4", saved.GetProperty("GameVersion").GetString());
            Assert.Equal("fabric-server.jar", saved.GetProperty("JarFile").GetString());
            Assert.Equal("conv1", saved.GetProperty("Id").GetString());
        });
    }

    [Fact]
    public void TheServerKeepsItsPlaceAndItsIdentityThroughAConversion()
    {
        // It used to be rebuilt from scratch on a type change, which threw away the console
        // history, the connected players and the CPU and RAM charts — and could only be done to a
        // stopped server, so the one case where that history matters most was left unrefreshed.
        WriteServersJson(
            new ServerConfig { Name = "uno", FolderPath = Folder("uno"), GameVersion = "1.21.1" },
            new ServerConfig { Name = "dos", FolderPath = Folder("dos"), GameVersion = "1.21.1" },
            new ServerConfig { Name = "tres", FolderPath = Folder("tres"), GameVersion = "1.21.1" });

        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            main.SelectedServer = main.Servers[1];
            var before = main.Servers[1];

            before.Config.Type = ServerType.Paper;

            Assert.Same(before, main.Servers[1]);
            Assert.Same(before, main.SelectedServer);
            Assert.Equal(new[] { "uno", "dos", "tres" }, main.Servers.Select(s => s.Name).ToArray());
        });
    }

    [Fact]
    public void RenamingAServerReachesTheListAndSurvivesTheSave()
    {
        WriteServersJson(new ServerConfig
        {
            Name = "antiguo", FolderPath = Folder("renombrado"), GameVersion = "1.21.1"
        });

        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            var server = Assert.Single(main.Servers);

            // What the name box in the edit dialog does on every keystroke.
            server.Config.Name = "moderno";
            Assert.Equal("moderno", server.Name);

            main.SaveCommand.Execute(null);
            Assert.Equal("moderno", ReadServersJson()[0].GetProperty("Name").GetString());
        });
    }

    [Fact]
    public void AServerListSavedTodayIsTheOneReadTomorrow()
    {
        // Closing and reopening the app, which is what the whole file format argument is about.
        WriteServersJson(new ServerConfig
        {
            Name = "ida y vuelta", FolderPath = Folder("ida"),
            Type = ServerType.Purpur, GameVersion = "1.21.1",
            CrossplayEnabled = true, BedrockPort = 19133, IdleShutdownMinutes = 15
        });

        _ui.Run(() =>
        {
            var first = new MainViewModel(_dataDir);
            first.SaveCommand.Execute(null);      // rewritten by this version of the app

            var second = new MainViewModel(_dataDir);
            var server = Assert.Single(second.Servers);

            Assert.Equal("ida y vuelta", server.Name);
            Assert.Equal(ServerType.Purpur, server.Config.Type);
            Assert.True(server.Config.CrossplayEnabled);
            Assert.Equal(19133, server.Config.BedrockPort);
            Assert.Equal(15, server.Config.IdleShutdownMinutes);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }
}
