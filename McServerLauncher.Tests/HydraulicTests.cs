using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Letting Bedrock players see what the mods add: where it works, and refusing where it does not.
/// </summary>
/// <remarks>
/// The facts below were read from GeyserMC's own downloads API and from the Hydraulic jar itself on
/// 2026-08-30, not from documentation: build 110 publishes a Fabric download only, its
/// <c>fabric.mod.json</c> requires <c>minecraft &gt;= 26.2</c>, and it lists <c>fabric-api</c> as a
/// hard dependency.
/// </remarks>
public class HydraulicTests
{
    [Fact]
    public void OnlyFabricCanRunIt()
    {
        Assert.True(HydraulicService.CanEnable(ServerType.Fabric));

        // NeoForge is the one that matters here. Hydraulic built for it until February 2026; the
        // module is now commented out of its settings.gradle and no build has shipped since. An
        // installed jar there would do nothing except look like the problem was handled.
        Assert.False(HydraulicService.CanEnable(ServerType.NeoForge));
        Assert.False(HydraulicService.CanEnable(ServerType.Forge));
        Assert.False(HydraulicService.CanEnable(ServerType.Paper));
        Assert.False(HydraulicService.CanEnable(ServerType.Purpur));
        Assert.False(HydraulicService.CanEnable(ServerType.Vanilla));
    }

    [Fact]
    public void FabricApiIsPartOfTheInstall()
    {
        // Declared as a hard dependency in Hydraulic's own metadata, and nothing else in the app
        // installs it. Without it the mod is simply not loaded: the server starts, looks healthy,
        // and Bedrock players see exactly the untextured world they saw before.
        Assert.Equal("fabric-api", HydraulicService.FabricApiProjectId);
    }

    [Theory]
    // What the published build actually asks for.
    [InlineData("26.2", ">=26.2", true)]
    [InlineData("26.3", ">=26.2", true)]
    [InlineData("1.21.1", ">=26.2", false)]
    [InlineData("1.21", ">=26.2", false)]
    // Other shapes that appear in fabric.mod.json.
    [InlineData("1.21.1", "*", true)]
    [InlineData("1.21.1", "1.21.1", true)]
    [InlineData("1.21.1", "1.21", false)]
    [InlineData("1.21.1", "1.21 || 1.21.1", true)]
    [InlineData("1.21.4", "~1.21", true)]
    [InlineData("1.21.1", "<=1.21.4", true)]
    public void TheDeclaredMinecraftRangeIsHonoured(string mcVersion, string range, bool expected) =>
        Assert.Equal(expected, MinecraftRange.Satisfies(mcVersion, range));

    [Fact]
    public void AVersionThatCannotBeComparedIsRefusedRatherThanGuessed()
    {
        // Snapshots have no ordering a Version can express. Refusing is recoverable; installing a
        // mod the server then fails to load is a silent problem the user has to diagnose.
        Assert.False(MinecraftRange.Satisfies("26w05a", ">=26.2"));
        Assert.False(MinecraftRange.Satisfies("", ">=26.2"));
        Assert.False(MinecraftRange.Satisfies(null, ">=26.2"));

        // No requirement stated is not the same as an unsatisfiable one.
        Assert.True(MinecraftRange.Satisfies("26.2", null));
        Assert.True(MinecraftRange.Satisfies("26.2", ""));
    }

    [Fact]
    public void AJarWithoutReadableMetadataDoesNotBlockTheInstall()
    {
        // The checksum already proved the file arrived intact. Failing on a metadata detail would
        // refuse a download that is very probably fine.
        var path = Path.Combine(Path.GetTempPath(), "mcl-notajar-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "esto no es un zip");
            Assert.Null(HydraulicService.ReadRequiredMinecraft(path));
        }
        finally { try { File.Delete(path); } catch { /* temp */ } }
    }

    [Fact]
    public void TheStringsExistInEveryLanguage()
    {
        string[] keys =
        {
            "Msg_HydraulicUnsupportedFmt", "Msg_HydraulicNoBuild", "Msg_HydraulicWrongVersionFmt",
            "Msg_HydraulicReady", "Hydraulic_Check", "Hydraulic_Hint", "Hydraulic_Unsupported",
        };

        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            foreach (var lang in new[] { "es", "en", "pt", "fr", "de" })
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo(lang);

                foreach (var key in keys)
                {
                    var value = McServerLauncher.Localization.Localizer.Get(key);
                    Assert.False(string.IsNullOrWhiteSpace(value) || value == key, $"falta {key} en {lang}");
                }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = original; }
    }
}
