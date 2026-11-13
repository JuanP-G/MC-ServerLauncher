using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// A server that is its own executable (Pumpkin), everywhere the app used to assume Java.
/// </summary>
public class NativeServerTypeTests
{
    [Fact]
    public void PumpkinIsTheSeventhTypeAndTheOnlyNativeOne()
    {
        // The number is the file format (see ServerTypeTests): appended, never inserted.
        Assert.Equal(6, (int)ServerType.Pumpkin);

        foreach (var type in Enum.GetValues<ServerType>())
            Assert.Equal(type == ServerType.Pumpkin, ServerTypeCatalog.IsNative(type));
    }

    [Fact]
    public void PumpkinIsMarkedExperimentalAndSaysSoInWords()
    {
        // Its own project prints "under heavy development" on every start. The badge and the note
        // are how that reaches somebody choosing a server type.
        Assert.True(ServerTypeCatalog.IsExperimental(ServerType.Pumpkin));
        Assert.False(ServerTypeCatalog.IsExperimental(ServerType.Paper));

        foreach (var key in new[] { "Badge_Experimental", "Experimental_Note" })
            Assert.NotEqual(key, McServerLauncher.Localization.Localizer.Get(key));
    }

    [Fact]
    public void PumpkinHasNothingTheStoreCanInstall()
    {
        // Its plugins are WebAssembly modules of its own; neither Paper's plugins nor any mod
        // runs on it. With content switched on the Mods tab would search Modrinth for a loader
        // called "pumpkin" and offer whatever came back.
        Assert.False(ServerTypeCatalog.HasContent(ServerType.Pumpkin));
        Assert.False(ServerTypeCatalog.HasContent(ServerType.Vanilla));
        Assert.True(ServerTypeCatalog.HasContent(ServerType.Paper));
        Assert.True(ServerTypeCatalog.HasContent(ServerType.Fabric));
    }

    [Fact]
    public void GeyserIsNotOfferedBecausePumpkinSpeaksBedrockItself()
    {
        Assert.False(CrossplayService.CanEnable(ServerType.Pumpkin));
        Assert.Equal("Crossplay_NativeBedrock", CrossplayService.WhyNotKey(ServerType.Pumpkin));

        // The two reasons that were already there keep their own words.
        Assert.Equal("Crossplay_UnsupportedVanilla", CrossplayService.WhyNotKey(ServerType.Vanilla));
        Assert.Equal("Crossplay_Unsupported", CrossplayService.WhyNotKey(ServerType.Forge));
    }

    [Fact]
    public void NoPerformanceCommandIsTypedIntoItsConsole()
    {
        // "spark tps" answers "Unknown or incomplete command" on Pumpkin, and "tick query" answers
        // in a layout of its own. Typed every few seconds, either would bury the console.
        Assert.Empty(PerformanceProbe.Candidates(ServerType.Pumpkin, "26.3", sparkInstalled: false));
        Assert.Empty(PerformanceProbe.Candidates(ServerType.Pumpkin, "26.3", sparkInstalled: true));

        Assert.NotEmpty(PerformanceProbe.Candidates(ServerType.Vanilla, "26.3", sparkInstalled: false));
    }

    [Theory]
    [InlineData(ServerSettingsPage.Game)]
    [InlineData(ServerSettingsPage.World)]
    [InlineData(ServerSettingsPage.Loader)]
    [InlineData(ServerSettingsPage.Performance)]
    [InlineData(ServerSettingsPage.Crossplay)]
    public void ThePagesAboutJavaAndServerPropertiesAreNotOffered(ServerSettingsPage page)
    {
        Assert.False(ServerSettingsPages.IsOffered(page, ServerType.Pumpkin));
        Assert.Equal(ServerSettingsPage.Appearance, ServerSettingsPages.Offered(page, ServerType.Pumpkin));

        foreach (var java in Enum.GetValues<ServerType>().Where(t => t != ServerType.Pumpkin))
            Assert.True(ServerSettingsPages.IsOffered(page, java));
    }

    [Theory]
    [InlineData(ServerSettingsPage.Appearance)]
    [InlineData(ServerSettingsPage.Network)]
    [InlineData(ServerSettingsPage.Power)]
    [InlineData(ServerSettingsPage.Backups)]
    [InlineData(ServerSettingsPage.Notifications)]
    [InlineData(ServerSettingsPage.Advanced)]
    public void TheRestOfThePagesStay(ServerSettingsPage page)
    {
        // Power is the one that matters most: wake on demand and the idle shutdown are the reason
        // to run a server from this app at all.
        Assert.True(ServerSettingsPages.IsOffered(page, ServerType.Pumpkin));
        Assert.Equal(page, ServerSettingsPages.Offered(page, ServerType.Pumpkin));
    }

    [Fact]
    public void ANativeServerSurvivesBeingSavedAndReloaded()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcsl-native-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AtomicJsonFile.Write(path, new[]
            {
                new ServerConfig { Name = "p", Type = ServerType.Pumpkin, JarFile = "pumpkin.exe",
                    GameVersion = "26.3", ModLoaderVersion = "0.2.2" }
            });

            Assert.Contains("\"Type\": 6", File.ReadAllText(path).Replace("\"Type\":6", "\"Type\": 6"));

            var (back, _) = AtomicJsonFile.Load<ServerConfig[]>(path);
            Assert.Equal(ServerType.Pumpkin, back![0].Type);
            Assert.Equal("pumpkin.exe", back[0].JarFile);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp */ }
        }
    }
}
