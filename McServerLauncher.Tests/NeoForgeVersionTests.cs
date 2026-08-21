using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Choosing which NeoForge build belongs to a Minecraft version.
/// </summary>
/// <remarks>
/// This is the part with no safety net at runtime: pick a build for the wrong Minecraft version and
/// it downloads, verifies and installs without complaint, then fails to start with an error that
/// points nowhere near here.
/// </remarks>
public class NeoForgeVersionTests
{
    // --- the classic 1.x scheme ---

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.20.2", "20.2.")]
    [InlineData("1.21.11", "21.11.")]
    public void ClassicVersionsDropTheLeadingOne(string mc, string expected) =>
        Assert.Equal(expected, NeoForgeVersions.PrefixFor(mc));

    [Theory]
    [InlineData("1.21", "21.0.")]
    [InlineData("1.20", "20.0.")]
    public void AMissingPatchIsZeroNotAbsent(string mc, string expected)
    {
        // NeoForge numbers Minecraft 1.21 builds "21.0.x", so the patch has to be filled in rather
        // than left off — "21." alone would match 21.1, 21.4 and everything else.
        Assert.Equal(expected, NeoForgeVersions.PrefixFor(mc));
    }

    // --- the newer scheme Minecraft moved to ---

    [Theory]
    [InlineData("26.2", "26.2.0.")]
    [InlineData("26.1.2", "26.1.2.")]
    public void NewSchemeVersionsKeepAllTheirParts(string mc, string expected) =>
        Assert.Equal(expected, NeoForgeVersions.PrefixFor(mc));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("1.21.1.4.7")]
    [InlineData("1.21-pre1")]
    [InlineData("snapshot")]
    public void NonsenseGetsNoPrefix(string? mc) => Assert.Null(NeoForgeVersions.PrefixFor(mc));

    [Fact]
    public void PrefixEndsWithADotSoItCannotMatchALongerFamily()
    {
        // The bug this prevents: "21.1" also starts "21.10.64" and "21.11.45", so without the
        // trailing dot a 1.21.1 server would be offered a Minecraft 1.21.10 loader.
        var versions = new[] { "21.1.248", "21.10.64", "21.11.45" };

        Assert.Equal("21.1.248", NeoForgeVersions.Pick(versions, "1.21.1")!.Version);
        Assert.Equal("21.10.64", NeoForgeVersions.Pick(versions, "1.21.10")!.Version);
    }

    // --- picking a build ---

    [Fact]
    public void PrefersTheNewestStable()
    {
        var versions = new[] { "21.1.100", "21.1.248", "21.1.9", "21.1.250-beta" };

        var choice = NeoForgeVersions.Pick(versions, "1.21.1");

        Assert.NotNull(choice);
        Assert.Equal("21.1.248", choice!.Version);
        Assert.False(choice.IsBeta);
    }

    [Fact]
    public void OrdersByNumberNotByText()
    {
        // "21.1.9" sorts above "21.1.248" as text, which would pin every server to an old build.
        var versions = new[] { "21.1.9", "21.1.248" };

        Assert.Equal("21.1.248", NeoForgeVersions.Pick(versions, "1.21.1")!.Version);
    }

    [Fact]
    public void FallsBackToABetaOnlyWhenThereIsNoStable()
    {
        // Six real Minecraft versions have only ever had beta builds. Refusing them would report
        // NeoForge as unavailable when a beta is what everybody actually runs there.
        var versions = new[] { "21.7.20-beta", "21.7.25-beta" };

        var choice = NeoForgeVersions.Pick(versions, "1.21.7");

        Assert.NotNull(choice);
        Assert.Equal("21.7.25-beta", choice!.Version);
        Assert.True(choice.IsBeta, "hay que poder avisar de que es beta antes de instalar");
    }

    [Fact]
    public void ABetaNeverBeatsAStableOfTheSameFamily()
    {
        var versions = new[] { "21.1.248", "21.1.249-beta" };

        var choice = NeoForgeVersions.Pick(versions, "1.21.1");

        Assert.Equal("21.1.248", choice!.Version);
        Assert.False(choice.IsBeta);
    }

    [Fact]
    public void NoBuildForThatMinecraftVersionIsNull()
    {
        var versions = new[] { "21.1.248", "20.4.251" };

        Assert.Null(NeoForgeVersions.Pick(versions, "1.16.5"));
        Assert.Null(NeoForgeVersions.Pick(versions, "gibberish"));
    }

    [Fact]
    public void AnEmptyCatalogueIsNotACrash() =>
        Assert.Null(NeoForgeVersions.Pick(Array.Empty<string>(), "1.21.1"));

    // --- reading a build number back, for servers already installed on disk ---

    [Theory]
    [InlineData("21.1.248", "1.21.1")]
    [InlineData("21.0.167", "1.21")]
    [InlineData("20.2.93", "1.20.2")]
    [InlineData("21.11.45", "1.21.11")]
    [InlineData("26.2.0.64", "26.2")]
    [InlineData("26.1.2.97", "26.1.2")]
    [InlineData("21.7.25-beta", "1.21.7")]
    public void ABuildNumberSaysWhichMinecraftItIsFor(string build, string expected) =>
        Assert.Equal(expected, NeoForgeVersions.MinecraftVersionOf(build));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("21.1")]
    [InlineData("not.a.version")]
    public void AnUnreadableBuildNumberGivesNothing(string? build) =>
        Assert.Null(NeoForgeVersions.MinecraftVersionOf(build));

    [Theory]
    [InlineData("1.21.1")]
    [InlineData("1.21")]
    [InlineData("1.20.2")]
    [InlineData("26.2")]
    [InlineData("26.1.2")]
    public void TheTwoDirectionsAgree(string mc)
    {
        // Whatever prefix a Minecraft version produces, a build carrying it has to map back to the
        // same Minecraft version. If these two ever drift apart, a server installed by the app
        // would be misread by the app the next time it opened.
        var build = NeoForgeVersions.PrefixFor(mc) + "42";

        Assert.Equal(mc, NeoForgeVersions.MinecraftVersionOf(build));
    }
}
