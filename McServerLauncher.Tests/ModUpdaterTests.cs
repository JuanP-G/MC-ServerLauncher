using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Checking for newer versions and replacing a jar with one, run against a store that answers from
/// memory.
/// </summary>
public class ModUpdaterTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-updater-" + Guid.NewGuid().ToString("N"));

    private readonly FakeModrinthStore _store = new();
    private readonly PendingUpdateStore _pending;
    private readonly ModUpdater _updater;
    private readonly List<string> _carried = new();

    public ModUpdaterTests()
    {
        var config = new ServerConfig
        {
            Name = "test",
            FolderPath = _folder,
            Type = ServerType.Fabric,
            GameVersion = "1.21.1",
        };
        Directory.CreateDirectory(Path.Combine(_folder, "mods"));

        _pending = new PendingUpdateStore(config);
        _updater = new ModUpdater(config, _store, new InstalledContentIndex(_store), _pending,
            (oldPath, newName) => _carried.Add(Path.GetFileName(oldPath) + ">" + newName));
    }

    private string Mods => Path.Combine(_folder, "mods");

    private ModItem Jar(string name, string content = "old", bool enabled = true)
    {
        var path = Path.Combine(Mods, name);
        File.WriteAllText(path, content);
        return new ModItem(path, ContentReplacement.DisplayName(path), enabled);
    }

    [Fact]
    public async Task AJarWithADifferentNewestFileOffersAnUpdate()
    {
        var mod = Jar("lithium-1.0.jar");
        var sha = FakeModrinthStore.Sha1Of(mod.FilePath);
        _store.LatestByHash = new() { [sha] = FakeModrinthStore.Version("lithium", "lithium-2.0.jar", "newsha") };

        var result = await _updater.CheckAsync([mod], CancellationToken.None);

        Assert.True(result.CouldAsk);
        Assert.Equal(1, result.Updates);
        Assert.Equal("2.0.0", mod.Update?.VersionNumber);
        Assert.Equal("lithium-2.0.jar", mod.Update?.FileName);
        Assert.NotNull(_pending.For(mod.FilePath));   // survives the list being rebuilt
        Assert.Contains(sha, result.Hashes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AJarThatIsAlreadyTheNewestOffersNothing()
    {
        var mod = Jar("lithium-1.0.jar");
        var sha = FakeModrinthStore.Sha1Of(mod.FilePath);
        _store.LatestByHash = new() { [sha] = FakeModrinthStore.Version("lithium", "lithium-1.0.jar", sha) };

        var result = await _updater.CheckAsync([mod], CancellationToken.None);

        Assert.True(result.CouldAsk);
        Assert.Equal(0, result.Updates);
        Assert.Null(mod.Update);
    }

    [Fact]
    public async Task NoAnswerFromTheStoreIsNotTheSameAsUpToDate()
    {
        var mod = Jar("lithium-1.0.jar");
        _store.LatestByHash = null;

        var result = await _updater.CheckAsync([mod], CancellationToken.None);

        Assert.False(result.CouldAsk);
        Assert.Equal(0, result.Updates);
    }

    [Fact]
    public async Task UpdatingReplacesTheOldJarWithTheVerifiedNewOne()
    {
        var mod = Jar("lithium-1.0.jar");
        mod.Update = new ModUpdateInfo("2.0.0", "https://cdn.example.invalid/lithium-2.0.jar",
            "lithium-2.0.jar", "5", null, "lithium");

        var result = await _updater.UpdateOneAsync(mod, CancellationToken.None);

        Assert.Equal(ContentReplacement.UpdateOutcome.Updated, result.Outcome);
        Assert.False(File.Exists(Path.Combine(Mods, "lithium-1.0.jar")));
        Assert.True(File.Exists(Path.Combine(Mods, "lithium-2.0.jar")));
        Assert.Null(mod.Update);
        Assert.False(mod.IsUpdating);
    }

    [Fact]
    public async Task ADisabledModKeepsItsDisabledDateAcrossTheRename()
    {
        var mod = Jar("lithium-1.0.jar.disabled", enabled: false);
        mod.Update = new ModUpdateInfo("2.0.0", "https://cdn.example.invalid/lithium-2.0.jar",
            "lithium-2.0.jar", "5", null, "lithium");

        var result = await _updater.UpdateOneAsync(mod, CancellationToken.None);

        Assert.Equal(ContentReplacement.UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(["lithium-1.0.jar.disabled>lithium-2.0.jar"], _carried);
    }

    [Fact]
    public async Task AFailedDownloadLeavesTheInstalledJarAndSaysWhy()
    {
        var mod = Jar("lithium-1.0.jar");
        mod.Update = new ModUpdateInfo("2.0.0", "https://cdn.example.invalid/lithium-2.0.jar",
            "lithium-2.0.jar", "5", null, "lithium");
        _store.DownloadFails = new UserFacingException("no connection");

        var result = await _updater.UpdateOneAsync(mod, CancellationToken.None);

        Assert.Equal(ContentReplacement.UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("no connection", result.Error);
        Assert.True(File.Exists(mod.FilePath));
        Assert.NotNull(mod.Update);   // still offered
        Assert.False(mod.IsUpdating);
    }

    [Fact]
    public async Task EachCallUsesTheTokenItWasGiven()
    {
        // The panel replaces its token when the app closes, so one captured here would stay
        // cancelled for good: the second call must work after the first was cancelled.
        var mod = Jar("lithium-1.0.jar");
        mod.Update = new ModUpdateInfo("2.0.0", "https://cdn.example.invalid/lithium-2.0.jar",
            "lithium-2.0.jar", "5", null, "lithium");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var first = await _updater.UpdateOneAsync(mod, cancelled.Token);
        var second = await _updater.UpdateOneAsync(mod, CancellationToken.None);

        Assert.Equal(ContentReplacement.UpdateOutcome.Failed, first.Outcome);
        Assert.Equal(ContentReplacement.UpdateOutcome.Updated, second.Outcome);
    }

    [Fact]
    public async Task UpdateAllWalksTheQueueAndCountsEachOutcome()
    {
        var a = Jar("a-1.jar");
        var b = Jar("b-1.jar");
        a.Update = new ModUpdateInfo("2", "https://cdn.example.invalid/a-2.jar", "a-2.jar", "5", null, "a");
        b.Update = new ModUpdateInfo("2", "https://cdn.example.invalid/b-2.jar", "b-2.jar", "5", null, "b");
        var said = new List<string>();

        var result = await _updater.UpdateAllAsync([a, b], new InlineProgress<string>(said.Add),
            CancellationToken.None);

        Assert.Equal(2, result.Updated);
        Assert.Empty(result.Failed);
        Assert.False(result.StoppedByServer);
        Assert.Equal(2, said.Count);
        Assert.True(File.Exists(Path.Combine(Mods, "a-2.jar")) && File.Exists(Path.Combine(Mods, "b-2.jar")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { /* temp */ }
    }
}
