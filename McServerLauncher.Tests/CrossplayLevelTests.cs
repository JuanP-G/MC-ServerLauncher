using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// How well Bedrock players fare on each server type, which is not the same question as whether
/// Geyser installs.
/// </summary>
/// <remarks>
/// The levels are what has been observed on real servers, not what the projects advertise: Paper
/// and Purpur work because plugins never touch the client; Fabric works because Hydraulic converts
/// the mods' content; NeoForge connects and authenticates and is then at the mercy of whichever
/// mods are installed; Forge has no Geyser build at all.
/// </remarks>
public class CrossplayLevelTests
{
    [Theory]
    [InlineData(ServerType.Paper, CrossplayLevel.Full)]
    [InlineData(ServerType.Purpur, CrossplayLevel.Full)]
    [InlineData(ServerType.Fabric, CrossplayLevel.Full)]
    [InlineData(ServerType.NeoForge, CrossplayLevel.Partial)]
    [InlineData(ServerType.Forge, CrossplayLevel.None)]
    [InlineData(ServerType.Vanilla, CrossplayLevel.None)]
    public void EachTypeCarriesTheLevelThatWasActuallyObserved(ServerType type, CrossplayLevel expected) =>
        Assert.Equal(expected, ServerTypeCatalog.Crossplay(type));

    [Fact]
    public void HavingALevelAtAllMeansGeyserRunsThere()
    {
        // The two have to agree: a type the picker badges for Bedrock and the installer then refuses
        // is a promise broken between one screen and the next.
        foreach (var entry in ServerTypeCatalog.All)
        {
            Assert.Equal(GeyserConfigService.Supports(entry.Type), entry.SupportsCrossplay);
            Assert.Equal(entry.Crossplay != CrossplayLevel.None, entry.SupportsCrossplay);
        }
    }

    [Fact]
    public void OnlyTheModLoadersCarryACaveatAndTheyCarryDifferentOnes()
    {
        // Fabric has an answer to give — the content checkbox — and NeoForge does not. One shared
        // paragraph covering both said less about each than two saying their own thing.
        Assert.Equal("Crossplay_ModdedNote", CrossplayService.CaveatKey(ServerType.Fabric));
        Assert.Equal("Crossplay_PartialNote", CrossplayService.CaveatKey(ServerType.NeoForge));

        // Nothing to warn about, and inventing a warning would be noise on the types that work.
        Assert.Null(CrossplayService.CaveatKey(ServerType.Paper));
        Assert.Null(CrossplayService.CaveatKey(ServerType.Purpur));

        // No crossplay at all: the checkbox is disabled and explains itself, so no caveat either.
        Assert.Null(CrossplayService.CaveatKey(ServerType.Forge));
        Assert.Null(CrossplayService.CaveatKey(ServerType.Vanilla));
    }

    [Fact]
    public void BothBedrockBadgesSayBedrock()
    {
        // The partial badge is amber and worded differently, but it still has to read as the Bedrock
        // badge at a glance — and the picker's own test looks for exactly that word.
        Assert.Contains("Bedrock", Localizer.Get("Badge_Bedrock"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bedrock", Localizer.Get("Badge_BedrockPartial"), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Localizer.Get("Badge_Bedrock"), Localizer.Get("Badge_BedrockPartial"));
    }

    [Fact]
    public void TheOnlyPartialTypeIsTheOneThatFailsSometimes()
    {
        // Stated as a whole-table claim rather than one row: adding a type and leaving its level at
        // the default would otherwise pass every other test in this file.
        Assert.Equal(new[] { ServerType.NeoForge },
            ServerTypeCatalog.All.Where(e => e.Crossplay == CrossplayLevel.Partial).Select(e => e.Type));
    }
}
