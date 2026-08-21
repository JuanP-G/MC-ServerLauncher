using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The server type: what it is worth on disk, and everything keyed off it.
/// </summary>
public class ServerTypeTests
{
    [Fact]
    public void TheNumbersAreTheFileFormatAndMustNotMove()
    {
        // servers.json stores this enum as an integer — a real config reads "Type": 0. Renumbering
        // any member silently reinterprets every server already saved on every machine: a Forge
        // server would come back as Paper the next time the app opened. New types go on the end.
        Assert.Equal(0, (int)ServerType.Vanilla);
        Assert.Equal(1, (int)ServerType.Fabric);
        Assert.Equal(2, (int)ServerType.Forge);
        Assert.Equal(3, (int)ServerType.Paper);
        Assert.Equal(4, (int)ServerType.NeoForge);
    }

    [Fact]
    public void ATypeSurvivesBeingSavedAndReloaded()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcsl-type-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var saved = new[] { new ServerConfig { Name = "n", Type = ServerType.NeoForge, ForgeArgs = "21.1.248" } };
            AtomicJsonFile.Write(path, saved);

            var (back, _) = AtomicJsonFile.Load<ServerConfig[]>(path);

            Assert.Equal(ServerType.NeoForge, back![0].Type);
            Assert.Equal("21.1.248", back[0].ForgeArgs);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp */ }
        }
    }

    [Theory]
    [InlineData(ServerType.Fabric, "fabric")]
    [InlineData(ServerType.Forge, "forge")]
    [InlineData(ServerType.NeoForge, "neoforge")]
    public void ModrinthSlugComesStraightFromTheEnumName(ServerType type, string expected)
    {
        // ModrinthService derives the loader facet with type.ToString().ToLowerInvariant(), which
        // happens to be exactly Modrinth's slug for all three. That is a happy accident worth
        // pinning: renaming a member would break the mod store with no compiler complaint at all.
        Assert.Equal(expected, type.ToString().ToLowerInvariant());
    }

    [Fact]
    public void OnlyPaperUsesPluginsRatherThanMods()
    {
        // NeoForge is a mod loader; if it ever ended up on the plugin side the store would search
        // Bukkit plugins for it and find nothing that works.
        Assert.NotEqual(ServerType.Paper, ServerType.NeoForge);
    }

    // --- the two dialogs that offer the list ---

    [Theory]
    [InlineData("CreateServerDialog.axaml")]
    [InlineData("InstallLoaderDialog.axaml")]
    public void EveryTypeIsOfferedAndEveryTagIsReal(string view)
    {
        // The dialogs map the picked item to a ServerType through its Tag. A typo there parses to
        // nothing and silently falls back — you would pick NeoForge and get Vanilla, with no error.
        var path = Path.Combine(RepoRoot(), "McServerLauncher", "Views", view);
        var xaml = File.ReadAllText(path);

        var tags = System.Text.RegularExpressions.Regex
            .Matches(xaml, @"<ComboBoxItem[^>]*\bTag=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(tags);
        foreach (var tag in tags)
            Assert.True(Enum.TryParse<ServerType>(tag, out _), $"{view}: Tag \"{tag}\" no es un ServerType");

        foreach (var type in Enum.GetValues<ServerType>())
            Assert.Contains(type.ToString(), tags);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McServerLauncher.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // --- where each loader keeps its launch args ---

    [Fact]
    public void ForgeAndNeoForgeHaveTheirOwnLibrariesRoot()
    {
        var forge = LoaderPaths.LibrariesRoot("/srv", ServerType.Forge);
        var neo = LoaderPaths.LibrariesRoot("/srv", ServerType.NeoForge);

        Assert.NotNull(forge);
        Assert.NotNull(neo);
        Assert.Contains(Path.Combine("net", "minecraftforge", "forge"), forge);
        Assert.Contains(Path.Combine("net", "neoforged", "neoforge"), neo);

        // The bug this guards: pointing NeoForge at Forge's directory makes a server that installs
        // fine and then cannot be started, because the args file is never found.
        Assert.NotEqual(forge, neo);
    }

    [Theory]
    [InlineData(ServerType.Vanilla)]
    [InlineData(ServerType.Fabric)]
    [InlineData(ServerType.Paper)]
    public void LoadersThatLaunchAJarHaveNoLibrariesRoot(ServerType type) =>
        Assert.Null(LoaderPaths.LibrariesRoot("/srv", type));

    [Fact]
    public void EveryArgsFileLoaderActuallyHasARoot()
    {
        // Keeps the two halves of LoaderPaths honest with each other: a loader listed as launching
        // through an args file but with nowhere to find one would fail only at start-up.
        foreach (var type in LoaderPaths.ArgsFileLoaders)
            Assert.NotNull(LoaderPaths.LibrariesRoot("/srv", type));
    }
}
