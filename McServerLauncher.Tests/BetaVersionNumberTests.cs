using System.Text.Json;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Betas carry a fourth number — 1.10.4.1 — and the updater has to order them correctly.
/// </summary>
/// <remarks>
/// The trap is that .NET reports an unspecified component as -1, so an untreated <c>1.10.4</c>
/// sorts <em>above</em> <c>1.10.4.1</c>. Left alone that offers everyone on a beta a "newer" stable
/// which is really the same release, and never offers the beta to anybody at all.
/// </remarks>
public class BetaVersionNumberTests
{
    private static (string? Tag, bool IsBeta) Pick(string json, string current)
    {
        var m = typeof(UpdateService).GetMethod("PickNewestRelease",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var normalize = typeof(UpdateService).GetMethod("Normalize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var releases = JsonDocument.Parse(json).RootElement;
        var version = (Version)normalize.Invoke(null, new object[] { new Version(current) })!;

        var result = (JsonElement?)m.Invoke(null, new object[] { releases, version });
        if (result is not { } release) return (null, false);

        return (release.GetProperty("tag_name").GetString(),
                release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True);
    }

    private const string Line = """
        [
          { "tag_name": "v1.10.3.2", "prerelease": true,  "draft": false, "html_url": "u" },
          { "tag_name": "v1.10.3.1", "prerelease": true,  "draft": false, "html_url": "u" },
          { "tag_name": "v1.10.3",   "prerelease": false, "draft": false, "html_url": "u" }
        ]
        """;

    [Fact]
    public void AStableIsOfferedTheNewestBeta()
    {
        var (tag, isBeta) = Pick(Line, "1.10.3");

        Assert.Equal("v1.10.3.2", tag);
        Assert.True(isBeta);
    }

    [Fact]
    public void ABetaIsOfferedTheNextBetaOnly()
    {
        Assert.Equal("v1.10.3.2", Pick(Line, "1.10.3.1").Tag);

        // Already on the newest: nothing to offer, and in particular not the older stable.
        Assert.Null(Pick(Line, "1.10.3.2").Tag);
    }

    [Fact]
    public void TheBetasHangOffTheLastStableSoTheNextStableOutranksThem()
    {
        // The scheme, and the reason it is this way round: a beta extends the stable it follows
        // (1.10.3 → 1.10.3.1 → 1.10.3.2), and the finished release is the next stable number. Had
        // betas been numbered after the version they lead TO — 1.10.4.1 before 1.10.4 — the stable
        // would sort BELOW its own betas, and everyone who tested them would be stranded there.
        var json = """
            [
              { "tag_name": "v1.11.0",   "prerelease": false, "draft": false, "html_url": "u" },
              { "tag_name": "v1.10.3.2", "prerelease": true,  "draft": false, "html_url": "u" }
            ]
            """;

        var (tag, isBeta) = Pick(json, "1.10.3.2");

        Assert.Equal("v1.11.0", tag);
        Assert.False(isBeta);
    }

    [Fact]
    public void ThreeNumbersStillWork()
    {
        // Every release published so far has three, and they must keep comparing as they always did.
        var json = """
            [
              { "tag_name": "v1.10.3", "prerelease": true,  "draft": false, "html_url": "u" },
              { "tag_name": "v1.10.0", "prerelease": false, "draft": false, "html_url": "u" }
            ]
            """;

        Assert.Equal("v1.10.3", Pick(json, "1.10.0").Tag);
        Assert.Null(Pick(json, "1.10.3").Tag);
    }

    [Fact]
    public void TheVersionShownKeepsTheFourthNumberOnlyWhenThereIsOne()
    {
        var format = typeof(Changelog).GetMethod("Format",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        // "1.10.4.0" matches no tag, no installer filename and nothing the user has ever seen.
        Assert.Equal("1.10.4", format.Invoke(null, new object[] { new Version(1, 10, 4) }));
        Assert.Equal("1.10.4", format.Invoke(null, new object[] { new Version(1, 10, 4, 0) }));
        Assert.Equal("1.10.4.1", format.Invoke(null, new object[] { new Version(1, 10, 4, 1) }));
    }
}
