using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Installing one project: the version it picks, where the file goes, what happens to the copies
/// already there, and what it reports. Run against a store that answers from memory.
/// </summary>
[Collection("content-ledger")]   // the players' pack lives under the ledger's root, which is static
public class ModInstallerTests : IDisposable
{
    private readonly string _previousLedgerRoot = ContentLedgerStore.RootDirectory;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-installer-" + Guid.NewGuid().ToString("N"));

    private readonly FakeModrinthStore _store = new();
    private readonly List<string> _progress = new();
    private readonly ServerConfig _config;
    private readonly ModInstaller _installer;

    public ModInstallerTests()
    {
        _config = new ServerConfig
        {
            Name = "test",
            FolderPath = _folder,
            Type = ServerType.Fabric,
            GameVersion = "1.21.1",
        };
        Directory.CreateDirectory(Path.Combine(_folder, "mods"));
        ContentLedgerStore.RootDirectory = Path.Combine(_folder, "content");

        var index = new InstalledContentIndex(_store);
        _installer = new ModInstaller(_config, _store, index,
            new ModDependencyInstaller(_config, _store, new ModDependencyService(_store), index));
    }

    private string Mods => Path.Combine(_folder, "mods");

    private Task<ModInstaller.Result> Install(string project = "lithium", VersionResult? chosen = null,
        bool toPlayerPack = false, CancellationToken ct = default) =>
        _installer.InstallAsync(project, chosen, toPlayerPack, new InlineProgress<string>(_progress.Add), ct);

    [Fact]
    public async Task TheNewestCompatibleVersionIsDownloadedIntoTheContentFolder()
    {
        _store.Latest = FakeModrinthStore.Version("lithium", "lithium-1.0.jar");

        var result = await Install();

        Assert.Equal(ModInstaller.Outcome.Installed, result.Outcome);
        Assert.Equal(0, result.Libraries);
        Assert.True(File.Exists(Path.Combine(Mods, "lithium-1.0.jar")));
        Assert.True(_progress.Count >= 2);   // resolving, then downloading
    }

    [Fact]
    public async Task AChosenVersionIsInstalledWithoutAskingTheStoreForTheNewest()
    {
        var chosen = FakeModrinthStore.Version("lithium", "lithium-0.9.jar", number: "0.9.0");

        var result = await Install(chosen: chosen);

        Assert.Equal(ModInstaller.Outcome.Installed, result.Outcome);
        Assert.Equal(0, _store.LatestCalls);
        Assert.True(File.Exists(Path.Combine(Mods, "lithium-0.9.jar")));
    }

    [Fact]
    public async Task AProjectWithNothingForThisServerInstallsNothing()
    {
        _store.Latest = null;

        var result = await Install();

        Assert.Equal(ModInstaller.Outcome.NoCompatibleVersion, result.Outcome);
        Assert.Empty(Directory.GetFiles(Mods));
    }

    [Fact]
    public async Task APlayerPackInstallGoesToThePackNotTheServer()
    {
        _store.Latest = FakeModrinthStore.Version("sodium", "sodium-1.0.jar");

        var result = await Install("sodium", toPlayerPack: true);

        Assert.Equal(ModInstaller.Outcome.Installed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(ClientPack.FolderOf(_config), "sodium-1.0.jar")));
        Assert.Empty(Directory.GetFiles(Mods));
    }

    [Fact]
    public async Task AFailedDownloadIsReportedNotThrown()
    {
        _store.Latest = FakeModrinthStore.Version("lithium", "lithium-1.0.jar");
        _store.DownloadFails = new UserFacingException("the checksum did not match");

        var result = await Install();

        Assert.Equal(ModInstaller.Outcome.Failed, result.Outcome);
        Assert.Contains("checksum", result.Error);
        Assert.Empty(Directory.GetFiles(Mods));
    }

    [Fact]
    public async Task ANewVersionTakesTheOldCopyOfTheSameProjectOut()
    {
        var old = Path.Combine(Mods, "lithium-0.8.jar");
        File.WriteAllText(old, "the old one");
        _store.ByHash[FakeModrinthStore.Sha1Of(old)] = FakeModrinthStore.Version("lithium", "lithium-0.8.jar",
            FakeModrinthStore.Sha1Of(old), "0.8.0");
        _store.Latest = FakeModrinthStore.Version("lithium", "lithium-1.0.jar");

        var result = await Install();

        Assert.Equal(ModInstaller.Outcome.Installed, result.Outcome);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(Mods, "lithium-1.0.jar")));
    }

    [Fact]
    public async Task ACopyHeldOpenByTheRunningServerChangesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;   // sharing modes are only enforced by Windows

        var old = Path.Combine(Mods, "lithium-0.8.jar");
        File.WriteAllText(old, "the old one");
        _store.ByHash[FakeModrinthStore.Sha1Of(old)] = FakeModrinthStore.Version("lithium", "lithium-0.8.jar",
            FakeModrinthStore.Sha1Of(old), "0.8.0");
        _store.Latest = FakeModrinthStore.Version("lithium", "lithium-1.0.jar");

        ModInstaller.Result result;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))   // the JVM has it open: readable, not deletable
            result = await Install();

        Assert.Equal(ModInstaller.Outcome.InUse, result.Outcome);
        Assert.Equal("lithium-0.8.jar", result.BlockingCopy);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(Path.Combine(Mods, "lithium-1.0.jar")));   // all or nothing
    }

    [Fact]
    public async Task ACancelledCallIsAFailureOfThatCallAndTheInstallerKeepsWorking()
    {
        _store.Latest = FakeModrinthStore.Version("lithium", "lithium-1.0.jar");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var first = await Install(ct: cancelled.Token);
        var second = await Install(ct: CancellationToken.None);   // the panel replaced its token

        Assert.Equal(ModInstaller.Outcome.Failed, first.Outcome);
        Assert.Equal(ModInstaller.Outcome.Installed, second.Outcome);
    }

    public void Dispose()
    {
        ContentLedgerStore.RootDirectory = _previousLedgerRoot;
        try { Directory.Delete(_folder, true); } catch { /* temp */ }
    }
}
