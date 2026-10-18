using System.IO.Compression;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using Store = McServerLauncher.Services.ExportSelection.StoreSide;

namespace McServerLauncher.Tests;

/// <summary>
/// The jars a server's players need and the server must not load.
/// </summary>
/// <remarks>
/// <para>
/// Sodium and MaLiLib do nothing on a server. The store now lists them, and installing one cannot
/// mean copying it into the folder the server loads: Fabric ignores a client-only mod, but Forge and
/// NeoForge can refuse to start over it. So they live in a folder of their own, and the export is
/// where they meet the players.
/// </para>
/// <para>
/// These tests are about that meeting: what is in the folder, what the export does with it, and what
/// removing the server leaves behind. They run on real files in a temporary directory and never
/// reach the store.
/// </para>
/// </remarks>
[Collection("content-ledger")]
public class ClientPackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-pack-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousLedgerRoot = ContentLedgerStore.RootDirectory;

    private static readonly IReadOnlyDictionary<string, (Store Client, Store Server)> NoStore =
        new Dictionary<string, (Store, Store)>();

    public ClientPackTests()
    {
        Directory.CreateDirectory(_root);
        ContentLedgerStore.RootDirectory = Path.Combine(_root, "content");
    }

    public void Dispose()
    {
        ContentLedgerStore.RootDirectory = _previousLedgerRoot;
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string Destination => Path.Combine(_root, "pack.zip");

    private ServerConfig Server(ServerType type)
    {
        var folder = Path.Combine(_root, "server-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(folder, ServerTypeCatalog.ContentFolder(type)));
        return new ServerConfig { Name = "mi servidor", FolderPath = folder, Type = type, GameVersion = "1.21.1" };
    }

    private static void Write(string folder, string fileName, string? fabricEnvironment = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        if (fabricEnvironment is null)
        {
            File.WriteAllText(path, "pretend this is " + fileName);
            return;
        }

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        using var entry = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
        entry.Write("{\"schemaVersion\":1,\"id\":\"" + Path.GetFileNameWithoutExtension(fileName) +
                    "\",\"version\":\"1.0.0\",\"environment\":\"" + fabricEnvironment + "\"}");
    }

    private static string[] EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("mods/", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    // --- The folder ---

    [Fact]
    public void TheFolderIsTheAppsAndNotTheServers()
    {
        var config = Server(ServerType.Fabric);

        var folder = ClientPack.FolderOf(config);

        Assert.Equal(Path.Combine(ContentLedgerStore.RootDirectory, config.Id, "client"), folder);
        Assert.DoesNotContain(config.FolderPath, folder);
    }

    [Fact]
    public void OnlyJarsAreListedInOrder()
    {
        var config = Server(ServerType.Fabric);
        var folder = ClientPack.FolderOf(config);
        Write(folder, "sodium.jar");
        Write(folder, "malilib.jar");
        Write(folder, "notes.txt");
        Write(folder, "old.jar.disabled");

        Assert.Equal(new[] { "malilib.jar", "sodium.jar" },
            ClientPack.Jars(config).Select(Path.GetFileName));
    }

    [Fact]
    public void NoFolderYetIsAnEmptyPackNotAnError() =>
        Assert.Empty(ClientPack.Jars(Server(ServerType.Fabric)));

    [Fact]
    public void RemovingTheServerRemovesItsPack()
    {
        var config = Server(ServerType.Fabric);
        Write(ClientPack.FolderOf(config), "sodium.jar");

        ContentLedgerStore.PruneOrphans(new[] { "some-other-server" });

        Assert.False(Directory.Exists(ClientPack.FolderOf(config)));
    }

    // --- The export ---

    [Fact]
    public async Task ThePackGoesIntoTheExportBesideTheServersMods()
    {
        var config = Server(ServerType.Fabric);
        Write(Path.Combine(config.FolderPath, "mods"), "lithium.jar");
        Write(ClientPack.FolderOf(config), "sodium.jar");
        var mods = new ServerModsViewModel(config);

        var result = await mods.BuildModpackWithSidesAsync(Destination, NoStore, false, null,
            CancellationToken.None);

        // Under mods/ like the rest, so the player's script has nothing new to learn.
        Assert.Equal(new[] { "mods/lithium.jar", "mods/sodium.jar" }, EntryNames(Destination));
        Assert.Equal(new[] { "lithium.jar", "sodium.jar" }, result.Included.OrderBy(n => n));
    }

    [Fact]
    public async Task ThePackIsNotJudgedByTheServerOnlyTable()
    {
        // A jar that says "server" would be left out of a players' pack by the table. These were
        // put in the pack by hand, for the players, so nothing gets to second-guess them.
        var config = Server(ServerType.Fabric);
        Write(ClientPack.FolderOf(config), "odd.jar", fabricEnvironment: "server");
        var mods = new ServerModsViewModel(config);

        var result = await mods.BuildModpackWithSidesAsync(Destination, NoStore, false, null,
            CancellationToken.None);

        Assert.Contains("mods/odd.jar", EntryNames(Destination));
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public async Task ANameTheServerAlreadyUsesIsPackedOnce()
    {
        // Two files cannot share a name in one folder, and the player must end up with the
        // server's copy.
        var config = Server(ServerType.Fabric);
        Write(Path.Combine(config.FolderPath, "mods"), "shared.jar");
        Write(ClientPack.FolderOf(config), "shared.jar");
        var mods = new ServerModsViewModel(config);

        await mods.BuildModpackWithSidesAsync(Destination, NoStore, false, null, CancellationToken.None);

        Assert.Single(EntryNames(Destination));
    }

    [Fact]
    public async Task AnEmptyPackChangesNothing()
    {
        var config = Server(ServerType.Fabric);
        Write(Path.Combine(config.FolderPath, "mods"), "lithium.jar");
        var mods = new ServerModsViewModel(config);

        await mods.BuildModpackWithSidesAsync(Destination, NoStore, false, null, CancellationToken.None);

        Assert.Equal(new[] { "mods/lithium.jar" }, EntryNames(Destination));
    }

    [Fact]
    public async Task APluginServerHasNoPlayersPack()
    {
        var config = Server(ServerType.Paper);
        Write(Path.Combine(config.FolderPath, "plugins"), "essentials.jar");
        Write(ClientPack.FolderOf(config), "sodium.jar");
        var mods = new ServerModsViewModel(config);

        await mods.BuildModpackWithSidesAsync(Destination, NoStore, true, null, CancellationToken.None);

        using var zip = ZipFile.OpenRead(Destination);
        var jars = zip.Entries.Select(e => e.FullName).Where(n => n.EndsWith(".jar")).ToArray();
        Assert.Equal(new[] { "plugins/essentials.jar" }, jars);
    }
}
