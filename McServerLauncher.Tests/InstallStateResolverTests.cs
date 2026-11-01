using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What the store browser should offer for a project that is already in the folder: nothing, an
/// update, or switching it on. Run against a store that answers from memory.
/// </summary>
public class InstallStateResolverTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-state-" + Guid.NewGuid().ToString("N"));

    private readonly FakeModrinthStore _store = new();
    private readonly ServerConfig _config;
    private readonly InstalledContentIndex _index;
    private readonly Clock _clock = new();
    private readonly InstallStateResolver _resolver;

    public InstallStateResolverTests()
    {
        _config = new ServerConfig
        {
            Name = "test",
            FolderPath = _folder,
            Type = ServerType.Fabric,
            GameVersion = "1.21.1",
        };
        Directory.CreateDirectory(Mods);
        _index = new InstalledContentIndex(_store);
        _resolver = new InstallStateResolver(_config, _store, _index, _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string Mods => Path.Combine(_folder, "mods");

    /// <summary>A manual clock: the resolver only needs to know how long ago something was said.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Puts a jar in the folder and tells the store which version of which project it is.</summary>
    private (string Path, string Sha1, VersionResult Version) Install(string project, string fileName,
        string content = "jar", bool known = true, string number = "1.0.0")
    {
        var path = Path.Combine(Mods, fileName);
        File.WriteAllText(path, content + fileName);
        var sha1 = FakeModrinthStore.Sha1Of(path);
        var version = FakeModrinthStore.Version(project, fileName.Replace(".disabled", ""), sha1, number);
        if (known) _store.ByHash[sha1] = version;
        return (path, sha1, version);
    }

    private Task<IReadOnlyDictionary<string, ProjectState>> Resolve() =>
        _resolver.ResolveAsync(Mods, CancellationToken.None);

    [Fact]
    public async Task AnEmptyFolderHasNothingInstalled()
    {
        Assert.Empty(await Resolve());
        Assert.Empty(await _resolver.ResolveAsync(Path.Combine(_folder, "nowhere"), CancellationToken.None));
    }

    [Fact]
    public async Task AJarOnTheNewestBuildIsInstalled()
    {
        var jar = Install("sodium", "sodium-1.0.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };

        var state = (await Resolve())["sodium"];

        Assert.Equal(InstallState.Installed, state.State);
        Assert.Equal(jar.Path, state.Path);
    }

    [Fact]
    public async Task AJarWithANewerBuildOffersTheUpdateAndNamesTheBuild()
    {
        var jar = Install("sodium", "sodium-1.0.jar");
        var newer = FakeModrinthStore.Version("sodium", "sodium-2.0.jar", "ffff", "2.0.0");
        _store.LatestByHash = new() { [jar.Sha1] = newer };

        var state = (await Resolve())["sodium"];

        Assert.Equal(InstallState.UpdateAvailable, state.State);
        Assert.Same(newer, state.Newer);
        Assert.Equal(jar.Path, state.Path);
    }

    [Fact]
    public async Task WhenTheStoreCannotBeAskedTheModIsInstalledAndNotClaimedToBeCurrent()
    {
        var jar = Install("sodium", "sodium-1.0.jar");
        _store.LatestByHash = null;

        var state = (await Resolve())["sodium"];

        Assert.Equal(InstallState.Installed, state.State);
        Assert.Null(state.Newer);
    }

    [Fact]
    public async Task AJarTheStoreDoesNotKnowIsNotAProject()
    {
        Install("handmade", "handmade.jar", known: false);
        _store.LatestByHash = new();

        Assert.Empty(await Resolve());
    }

    [Fact]
    public async Task AJarThatIsOnlyAModuleOfAVersionDoesNotCountAsTheProject()
    {
        var path = Path.Combine(Mods, "sodium-extra.jar");
        File.WriteAllText(path, "module");
        var sha1 = FakeModrinthStore.Sha1Of(path);
        var version = FakeModrinthStore.Version("sodium", "sodium-core.jar", "cccc");
        version.Files.Add(new VersionFile
        {
            Url = "https://cdn.example.invalid/sodium-extra.jar",
            Filename = "sodium-extra.jar",
            Primary = false,
            Hashes = new FileHashes { Sha1 = sha1, Sha512 = "5" },
        });
        _store.ByHash[sha1] = version;

        Assert.Empty(await Resolve());
    }

    [Fact]
    public async Task AModThatIsOnlySwitchedOffIsDisabledAndSaysWhichJar()
    {
        var jar = Install("sodium", "sodium-1.0.jar.disabled");

        var state = (await Resolve())["sodium"];

        Assert.Equal(InstallState.Disabled, state.State);
        Assert.Equal(jar.Path, state.Path);
    }

    [Fact]
    public async Task ASwitchedOffCopyBesideAnEnabledOneChangesNothing()
    {
        var enabled = Install("sodium", "sodium-1.0.jar", content: "a");
        Install("sodium", "sodium-0.9.jar.disabled", content: "b", number: "0.9.0");
        _store.LatestByHash = new() { [enabled.Sha1] = enabled.Version };

        var state = (await Resolve())["sodium"];

        Assert.Equal(InstallState.Installed, state.State);
        Assert.Equal(enabled.Path, state.Path);
    }

    [Fact]
    public async Task AnUpdateIsNotOfferedForAJarThatIsSwitchedOff()
    {
        var jar = Install("sodium", "sodium-1.0.jar.disabled");
        _store.LatestByHash = new()
        {
            [jar.Sha1] = FakeModrinthStore.Version("sodium", "sodium-2.0.jar", "ffff", "2.0.0"),
        };

        Assert.Equal(InstallState.Disabled, (await Resolve())["sodium"].State);
        Assert.Equal(0, _store.LatestByHashCalls);   // nothing enabled to ask about
    }

    [Fact]
    public async Task EachProjectHasItsOwnState()
    {
        var current = Install("sodium", "sodium.jar", content: "a");
        var behind = Install("lithium", "lithium.jar", content: "b");
        var off = Install("iris", "iris.jar.disabled", content: "c");
        _store.LatestByHash = new()
        {
            [current.Sha1] = current.Version,
            [behind.Sha1] = FakeModrinthStore.Version("lithium", "lithium-2.jar", "ffff", "2.0.0"),
        };

        var states = await Resolve();

        Assert.Equal(InstallState.Installed, states["sodium"].State);
        Assert.Equal(InstallState.UpdateAvailable, states["lithium"].State);
        Assert.Equal(InstallState.Disabled, states["iris"].State);
        Assert.False(states.ContainsKey("fabric-api"));
        _ = off;
    }

    [Fact]
    public async Task AJustInstalledJarIsInstalledEvenWhenTheStoreAnswersNothing()
    {
        // The case the card used to get wrong: the hash of what was just written is in nobody's
        // cache, and the store, asked afterwards, is unreachable or says nothing.
        var path = Path.Combine(Mods, "sodium-1.0.jar");
        File.WriteAllText(path, "fresh");
        var sha1 = FakeModrinthStore.Sha1Of(path);
        var version = FakeModrinthStore.Version("sodium", "sodium-1.0.jar", sha1);
        _store.LatestByHash = null;

        _resolver.NoteInstalled(sha1, version, isNewest: true);

        var state = (await Resolve())["sodium"];
        Assert.Equal(InstallState.Installed, state.State);
        Assert.Equal(0, _store.LatestByHashCalls);
    }

    [Fact]
    public async Task AVersionPickedByHandIsNotAssumedToBeTheNewest()
    {
        var path = Path.Combine(Mods, "sodium-0.9.jar");
        File.WriteAllText(path, "old");
        var sha1 = FakeModrinthStore.Sha1Of(path);
        var picked = FakeModrinthStore.Version("sodium", "sodium-0.9.jar", sha1, "0.9.0");
        var newer = FakeModrinthStore.Version("sodium", "sodium-1.0.jar", "ffff", "1.0.0");
        _store.LatestByHash = new() { [sha1] = newer };

        _resolver.NoteInstalled(sha1, picked, isNewest: false);

        Assert.Equal(InstallState.UpdateAvailable, (await Resolve())["sodium"].State);
    }

    [Fact]
    public async Task ANewestBuildAnswerIsReusedWhileItIsFresh()
    {
        var jar = Install("sodium", "sodium.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };

        await Resolve();
        _clock.Now += InstallStateResolver.NewestLifetime - TimeSpan.FromSeconds(1);
        await Resolve();

        Assert.Equal(1, _store.LatestByHashCalls);
    }

    [Fact]
    public async Task ANewestBuildAnswerIsAskedAgainOnceItIsStale()
    {
        var jar = Install("sodium", "sodium.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };
        Assert.Equal(InstallState.Installed, (await Resolve())["sodium"].State);

        // A newer build is published in the meantime.
        _store.LatestByHash = new()
        {
            [jar.Sha1] = FakeModrinthStore.Version("sodium", "sodium-2.jar", "ffff", "2.0.0"),
        };
        _clock.Now += InstallStateResolver.NewestLifetime + TimeSpan.FromSeconds(1);

        Assert.Equal(InstallState.UpdateAvailable, (await Resolve())["sodium"].State);
        Assert.Equal(2, _store.LatestByHashCalls);
    }

    [Fact]
    public async Task AStaleAnswerIsNotReusedWhenTheStoreCannotBeAskedAgain()
    {
        var jar = Install("sodium", "sodium.jar");
        _store.LatestByHash = new() { [jar.Sha1] = FakeModrinthStore.Version("sodium", "s2.jar", "ffff", "2") };
        Assert.Equal(InstallState.UpdateAvailable, (await Resolve())["sodium"].State);

        _store.LatestByHash = null;
        _clock.Now += InstallStateResolver.NewestLifetime + TimeSpan.FromSeconds(1);

        Assert.Equal(InstallState.Installed, (await Resolve())["sodium"].State);
    }

    [Fact]
    public async Task MovingTheServerToAnotherVersionDropsTheAnswersForTheOldOne()
    {
        var jar = Install("sodium", "sodium.jar");
        _store.LatestByHash = new() { [jar.Sha1] = jar.Version };
        await Resolve();

        _config.GameVersion = "1.21.4";
        await Resolve();

        Assert.Equal(2, _store.LatestByHashCalls);
    }
}
