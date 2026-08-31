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
        Assert.Equal(5, (int)ServerType.Purpur);
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

    // --- the list both dialogs offer ---

    [Fact]
    public void EveryTypeIsOfferedAndCanActuallyBeInstalled()
    {
        // This used to parse the ComboBoxItem Tags out of each dialog's XAML, because the type was
        // recovered by parsing that string and a typo silently fell back to Vanilla. Both dialogs
        // now share one picker built from the catalogue, so the question worth asking has moved:
        // is every type in the enum offered, and can every offered type be installed?
        var offered = ServerTypeCatalog.All.Select(e => e.Type).ToList();

        foreach (var type in Enum.GetValues<ServerType>())
            Assert.Contains(type, offered);

        Assert.Equal(offered.Count, offered.Distinct().Count());

        foreach (var type in offered)
            Assert.Contains(type, ServerJarInstaller.Installable);
    }

    [Fact]
    public void EveryTypeHasAFamilyAndADescription()
    {
        // A type added to the enum without a row lands on the fallback: no family badge, no
        // description, and quietly treated as taking mods. Better to fail here.
        foreach (var type in Enum.GetValues<ServerType>())
        {
            var entry = ServerTypeCatalog.For(type);

            Assert.Equal(type, entry.Type);
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(entry.DescriptionKey));
            Assert.NotEqual(entry.DescriptionKey,
                McServerLauncher.Localization.Localizer.Get(entry.DescriptionKey));
        }
    }

    [Fact]
    public void TheFamilyDecidesTheContentFolder()
    {
        // The mod store, the crossplay installer and the Mods tab all ask this one question, and a
        // plugin written into mods/ is simply never loaded.
        Assert.Equal("plugins", ServerTypeCatalog.ContentFolder(ServerType.Paper));
        Assert.Equal("plugins", ServerTypeCatalog.ContentFolder(ServerType.Purpur));
        Assert.Equal("mods", ServerTypeCatalog.ContentFolder(ServerType.Fabric));
        Assert.Equal("mods", ServerTypeCatalog.ContentFolder(ServerType.NeoForge));
        Assert.Equal("mods", ServerTypeCatalog.ContentFolder(ServerType.Forge));
    }

    [Fact]
    public void TheCrossplayBadgeMatchesWhatGeyserActuallySupports()
    {
        // The badge is a promise made in the picker, before anything is downloaded. If it and
        // GeyserConfigService disagreed, the app would advertise Bedrock support for a server it
        // then refuses to set up.
        foreach (var entry in ServerTypeCatalog.All)
            Assert.Equal(GeyserConfigService.Supports(entry.Type), entry.SupportsCrossplay);
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
