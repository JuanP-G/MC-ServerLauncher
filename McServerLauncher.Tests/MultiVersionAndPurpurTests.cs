using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Purpur behaving as the Bukkit-family server it is, and version bridging needing both halves.
/// </summary>
public class MultiVersionAndPurpurTests
{
    [Fact]
    public void BothViaPluginsAreInstalled()
    {
        // ViaVersion admits clients NEWER than the server; ViaBackwards admits OLDER ones. They are
        // not two names for the same thing, and shipping only the first covers the rarer half —
        // the friend who never updates is still turned away, and the feature looks broken.
        Assert.Equal(2, MultiVersionService.ProjectIds.Length);
        Assert.Contains("viaversion", MultiVersionService.ProjectIds);
        Assert.Contains("viabackwards", MultiVersionService.ProjectIds);
    }

    [Fact]
    public void VersionBridgingIsOfferedOnPluginServersOnly()
    {
        Assert.True(MultiVersionService.CanEnable(ServerType.Paper));
        Assert.True(MultiVersionService.CanEnable(ServerType.Purpur));

        // On a mod loader the loader itself already demands a matching client, so bridging versions
        // would solve a problem the loader recreates one step later.
        Assert.False(MultiVersionService.CanEnable(ServerType.Fabric));
        Assert.False(MultiVersionService.CanEnable(ServerType.NeoForge));
        Assert.False(MultiVersionService.CanEnable(ServerType.Forge));
        Assert.False(MultiVersionService.CanEnable(ServerType.Vanilla));
    }

    [Fact]
    public void PurpurIsTreatedAsPluginsEverywhereItMatters()
    {
        // The failure this guards against is a single site still thinking Purpur is a mod loader:
        // plugins written to mods/, the store searching mod projects, or Bedrock players warned
        // about a mod problem that cannot happen on a plugin server.
        Assert.True(ServerTypeCatalog.IsPluginBased(ServerType.Purpur));
        Assert.Equal("plugins", ServerTypeCatalog.ContentFolder(ServerType.Purpur));
        Assert.Equal(ServerFamily.Plugins, ServerTypeCatalog.For(ServerType.Purpur).Family);

        Assert.True(GeyserConfigService.Supports(ServerType.Purpur));
        Assert.Contains("plugins", GeyserConfigService.ConfigPath("/srv", ServerType.Purpur)!);
        Assert.False(CrossplayService.ModsCanLockOutBedrock(ServerType.Purpur));

        // Floodgate for the Bukkit family comes from GeyserMC's own site, not Modrinth.
        Assert.False(CrossplayService.FloodgateComesFromModrinth(ServerType.Purpur));
    }

    [Fact]
    public void PurpurLaunchesFromAJarLikePaper()
    {
        // Purpur is a Paper fork, so it must not be mistaken for an args-file loader: that would
        // send the launcher looking for a libraries directory that never gets created.
        Assert.Null(LoaderPaths.LibrariesRoot("/srv", ServerType.Purpur));
        Assert.DoesNotContain(ServerType.Purpur, LoaderPaths.ArgsFileLoaders);
    }

    [Fact]
    public void EveryTypeTheAppOffersCanBeInstalled()
    {
        // The old code fell through to "download the vanilla jar" for anything unrecognised, so a
        // type offered but not implemented produced a Vanilla server without saying so. The
        // installer now refuses instead, and this keeps the two lists in step.
        foreach (var type in Enum.GetValues<ServerType>())
            Assert.Contains(type, ServerJarInstaller.Installable);
    }
}
