using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The buttons of the store: a mod installed from a card, or found in the folder, must stop saying
/// "Install". Run through the real panel against a store that answers from memory.
/// </summary>
/// <remarks>
/// The bug this covers: installing showed the green banner, and the very next search put the same
/// mod back on screen with "Install" on it, because the cards were built once and knew nothing about
/// what was on disk.
/// </remarks>
[Collection("content-ledger")]   // the players' pack lives under the ledger's root, which is static
public class InstallStateCardTests : IDisposable
{
    private readonly string _previousLedgerRoot = ContentLedgerStore.RootDirectory;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-cards-" + Guid.NewGuid().ToString("N"));

    private readonly FakeModrinthStore _store = new();
    private readonly ServerConfig _config;

    public InstallStateCardTests()
    {
        _config = new ServerConfig
        {
            Name = "test",
            FolderPath = _folder,
            Type = ServerType.Fabric,
            GameVersion = "1.21.1",
        };
        ContentLedgerStore.RootDirectory = Path.Combine(_folder, "content");
        Directory.CreateDirectory(Mods);
    }

    public void Dispose()
    {
        ContentLedgerStore.RootDirectory = _previousLedgerRoot;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string Mods => Path.Combine(_folder, "mods");

    /// <summary>What <see cref="FakeModrinthStore"/> writes for a download, hashed.</summary>
    private static string HashOfDownload(string fileName) => Convert.ToHexString(
        SHA1.HashData(Encoding.UTF8.GetBytes("downloaded from https://cdn.example.invalid/" + fileName)))
        .ToLowerInvariant();

    /// <summary>A jar already in <paramref name="folder"/>, known to the store as a version of <paramref name="project"/>.</summary>
    private (string Path, string Sha1, VersionResult Version) Jar(string project, string fileName,
        string? folder = null, string number = "1.0.0")
    {
        var path = Path.Combine(folder ?? Mods, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "old " + fileName);
        var sha1 = FakeModrinthStore.Sha1Of(path);
        var version = FakeModrinthStore.Version(project, fileName.Replace(".disabled", ""), sha1, number);
        _store.ByHash[sha1] = version;
        return (path, sha1, version);
    }

    private ServerModsViewModel Panel() => new(_config, _store);

    private static ModrinthProjectViewModel Card(ServerModsViewModel panel, string id, bool clientOnly = false)
    {
        var card = new ModrinthProjectViewModel(new StoreItem
        {
            Id = id,
            Slug = id,
            Title = id,
            ClientSide = clientOnly ? "required" : "optional",
            ServerSide = clientOnly ? "unsupported" : "optional",
        }, panel);
        panel.SearchResults.Add(card);
        return card;
    }

    [Fact]
    public async Task InstallingFromACardLeavesTheCardSayingInstalled()
    {
        // Nothing the store could be asked afterwards: the card must not depend on a second answer.
        _store.Latest = FakeModrinthStore.Version("sodium", "sodium-1.0.jar", HashOfDownload("sodium-1.0.jar"));
        var panel = Panel();
        var card = Card(panel, "sodium");
        Assert.True(card.IsNotInstalled);

        await card.InstallCommand.ExecuteAsync(null);
        await panel.InstallStatesSettled;

        Assert.True(card.IsInstalled);
        Assert.False(card.IsNotInstalled);
    }

    [Fact]
    public async Task ASearchRunAfterAnInstallShowsTheModAsInstalled()
    {
        _store.Latest = FakeModrinthStore.Version("sodium", "sodium-1.0.jar", HashOfDownload("sodium-1.0.jar"));
        var panel = Panel();
        await Card(panel, "sodium").InstallCommand.ExecuteAsync(null);
        await panel.InstallStatesSettled;

        // The results of the next search: fresh cards, built after the install.
        var again = Card(panel, "sodium");

        Assert.True(again.IsInstalled);
    }

    [Fact]
    public async Task AJarCopiedInByHandCountsAsInstalled()
    {
        var jar = Jar("sodium", "sodium-copied.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };
        var panel = Panel();
        var card = Card(panel, "sodium");

        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        Assert.True(card.IsInstalled);
    }

    [Fact]
    public async Task AnOutdatedJarOffersTheUpdateAndTheUpdateReplacesIt()
    {
        var old = Jar("sodium", "sodium-1.0.jar");
        var newer = FakeModrinthStore.Version("sodium", "sodium-2.0.jar", HashOfDownload("sodium-2.0.jar"), "2.0.0");
        _store.LatestByHash = new() { [old.Sha1] = newer };
        var panel = Panel();
        var card = Card(panel, "sodium");

        panel.ReloadInstalled();
        await panel.InstallStatesSettled;
        Assert.True(card.IsUpdatable);

        await card.UpdateCommand.ExecuteAsync(null);
        await panel.InstallStatesSettled;

        Assert.Equal(new[] { "sodium-2.0.jar" },
            Directory.GetFiles(Mods).Select(Path.GetFileName).ToArray());
        Assert.Equal(new[] { newer.Files[0].Url }, _store.Downloaded);
        Assert.True(card.IsInstalled);
        Assert.Equal(Localizer.Get("Msg_ModUpdated"), panel.InstallNotice);
    }

    [Fact]
    public async Task TheUpdateInstallsTheBuildTheStateWasWorkedOutFromNotWhateverTheStoreSaysNow()
    {
        // Two endpoints can disagree for an hour (the project's version list is cached). A card that
        // asked the second one would install a build other than the one it offered, and then offer
        // the update again for ever.
        var old = Jar("sodium", "sodium-1.0.jar");
        var offered = FakeModrinthStore.Version("sodium", "sodium-2.0.jar", HashOfDownload("sodium-2.0.jar"), "2.0.0");
        _store.LatestByHash = new() { [old.Sha1] = offered };
        _store.Latest = FakeModrinthStore.Version("sodium", "sodium-1.5.jar", "0000", "1.5.0");
        var panel = Panel();
        var card = Card(panel, "sodium");
        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        await card.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(new[] { offered.Files[0].Url }, _store.Downloaded);
        Assert.Equal(0, _store.LatestCalls);
    }

    [Fact]
    public async Task UpdatingFromACardClearsTheUpdateTheInstalledListWasOffering()
    {
        var old = Jar("sodium", "sodium-1.0.jar");
        var newer = FakeModrinthStore.Version("sodium", "sodium-2.0.jar", HashOfDownload("sodium-2.0.jar"), "2.0.0");
        _store.LatestByHash = new() { [old.Sha1] = newer };
        var panel = Panel();
        var card = Card(panel, "sodium");
        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        await panel.CheckUpdatesCommand.ExecuteAsync(null);   // "Buscar actualizaciones"
        Assert.Equal(1, panel.PendingUpdateCount);
        Assert.True(card.IsUpdatable);

        await card.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(0, panel.PendingUpdateCount);
        Assert.False(panel.HasPendingUpdates);
        Assert.False(panel.UpdateAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADisabledModShowsAsDisabledAndTheCardSwitchesItBackOn()
    {
        var jar = Jar("sodium", "sodium-1.0.jar.disabled");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };
        var panel = Panel();
        var card = Card(panel, "sodium");

        panel.ReloadInstalled();
        await panel.InstallStatesSettled;
        Assert.True(card.IsDisabled);

        card.EnableCommand.Execute(null);
        await panel.InstallStatesSettled;

        Assert.True(File.Exists(Path.Combine(Mods, "sodium-1.0.jar")));
        Assert.False(File.Exists(jar.Path));
        Assert.True(card.IsInstalled);
    }

    [Fact]
    public async Task AClientOnlyModIsLookedForInThePlayersPackNotTheServer()
    {
        var inPack = Jar("sodium", "sodium-1.0.jar", ClientPack.FolderOf(_config));
        var onServer = Jar("iris", "iris-1.0.jar");
        _store.LatestByHash = new() { [inPack.Sha1] = inPack.Version, [onServer.Sha1] = onServer.Version };
        var panel = Panel();
        var sodium = Card(panel, "sodium", clientOnly: true);
        var irisAsClientOnly = Card(panel, "iris", clientOnly: true);

        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        Assert.True(sodium.IsInstalled);
        Assert.True(irisAsClientOnly.IsNotInstalled);   // on the server, which is not where it would go
    }

    [Fact]
    public async Task APluginServerLooksInThePluginsFolder()
    {
        var paper = new ServerConfig
        {
            Name = "paper",
            FolderPath = _folder,
            Type = ServerType.Paper,
            GameVersion = "1.21.1",
        };
        var path = Path.Combine(_folder, "plugins", "EssentialsX.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "plugin");
        var sha1 = FakeModrinthStore.Sha1Of(path);
        var version = FakeModrinthStore.Version("essentialsx", "EssentialsX.jar", sha1);
        _store.ByHash[sha1] = version;
        _store.LatestByHash = new() { [sha1] = version };
        var panel = new ServerModsViewModel(paper, _store);
        var card = Card(panel, "essentialsx");

        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        Assert.True(card.IsInstalled);
    }

    [Fact]
    public async Task ACardBuiltAfterTheCheckStartsOutRight()
    {
        // "Load more" and every new search build cards after the state is known.
        var jar = Jar("sodium", "sodium-1.0.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };
        var panel = Panel();
        Card(panel, "lithium");
        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        var late = Card(panel, "sodium");

        Assert.True(late.IsInstalled);
    }

    [Fact]
    public async Task NothingIsAskedOfTheStoreWhileNoProjectIsOnScreen()
    {
        var jar = Jar("sodium", "sodium-1.0.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };
        var panel = Panel();

        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        Assert.Equal(0, _store.LatestByHashCalls);
    }

    [Fact]
    public async Task WhenTheStoreCannotBeAskedAnInstalledModIsNotClaimedToBeCurrentOrMissing()
    {
        var jar = Jar("sodium", "sodium-1.0.jar");
        _store.LatestByHash = null;
        var panel = Panel();
        var card = Card(panel, "sodium");

        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        Assert.True(card.IsInstalled);
        Assert.False(card.IsUpdatable);
        _ = jar;
    }

    [Theory]
    [InlineData(typeof(ModrinthProjectViewModel))]
    [InlineData(typeof(ModDetailsViewModel))]
    public void EveryNameTheStateButtonBindsToExistsOnBothPagesItSitsOn(Type viewModel)
    {
        // InstallStateButton.axaml is x:CompileBindings="False": a mistyped name raises nothing and
        // shows nothing, which on a button reads as a mod that can never be updated.
        var markup = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "InstallStateButton.axaml"));

        var names = Regex.Matches(markup, @"\{Binding !{0,2}([A-Za-z0-9_]+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.True(viewModel.GetProperty(name) is not null, $"{viewModel.Name} no tiene «{name}»");
    }

    [Fact]
    public void BothPagesUseTheSameButton()
    {
        foreach (var view in new[] { "ServerModsView.axaml", "ModDetailsView.axaml" })
        {
            var markup = File.ReadAllText(Path.Combine(
                LocalizationTests.RepoRoot(), "McServerLauncher", "Views", view));
            Assert.Contains("<views:InstallStateButton", markup);
        }
    }

    [Fact]
    public async Task DeletingTheJarBringsInstallBack()
    {
        var jar = Jar("sodium", "sodium-1.0.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };
        var panel = Panel();
        var card = Card(panel, "sodium");
        panel.ReloadInstalled();
        await panel.InstallStatesSettled;
        Assert.True(card.IsInstalled);

        File.Delete(jar.Path);
        panel.ReloadInstalled();
        await panel.InstallStatesSettled;

        Assert.True(card.IsNotInstalled);
    }
}
