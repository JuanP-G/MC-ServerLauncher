using System.Text.Json;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Choosing which release to offer, now that betas are in the running.
/// </summary>
/// <remarks>
/// This code decides what every user of the app is invited to install, so the ways it can go wrong
/// are not small: offering an older release, offering a draft whose files do not exist yet, or —
/// the one that matters most here — offering a beta without saying it is one.
/// </remarks>
public class UpdatePreReleaseTests
{
    private static JsonElement Releases(string json) => JsonDocument.Parse(json).RootElement;

    private static (string? Tag, bool IsBeta) Pick(string json, string current)
    {
        var m = typeof(UpdateService).GetMethod("PickNewestRelease",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (JsonElement?)m.Invoke(null, new object[] { Releases(json), new Version(current) });
        if (result is not { } release) return (null, false);

        return (release.GetProperty("tag_name").GetString(),
                release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True);
    }

    private const string TwoReleases = """
        [
          { "tag_name": "v1.10.1", "prerelease": true,  "draft": false, "html_url": "u" },
          { "tag_name": "v1.10.0", "prerelease": false, "draft": false, "html_url": "u" }
        ]
        """;

    [Fact]
    public void ABetaIsOfferedAndFlaggedAsOne()
    {
        // The whole point: GitHub keeps pre-releases out of /releases/latest, so reading the list is
        // what makes a beta reachable at all — and the flag is what stops it arriving unannounced.
        var (tag, isBeta) = Pick(TwoReleases, "1.10.0");

        Assert.Equal("v1.10.1", tag);
        Assert.True(isBeta);
    }

    [Fact]
    public void AStableIsNotFlagged()
    {
        var (tag, isBeta) = Pick(TwoReleases, "1.9.2");

        Assert.Equal("v1.10.1", tag);   // still the newest
        Assert.True(isBeta);

        var onlyStable = """
            [ { "tag_name": "v1.10.0", "prerelease": false, "draft": false, "html_url": "u" } ]
            """;
        Assert.False(Pick(onlyStable, "1.9.2").IsBeta);
    }

    [Fact]
    public void NothingIsOfferedWhenAlreadyOnTheNewest()
    {
        Assert.Null(Pick(TwoReleases, "1.10.1").Tag);
        Assert.Null(Pick(TwoReleases, "1.11.0").Tag);
    }

    [Fact]
    public void DraftsAreSkipped()
    {
        // A draft is unpublished: its assets may not exist, so offering it would download nothing.
        var json = """
            [
              { "tag_name": "v2.0.0", "prerelease": false, "draft": true,  "html_url": "u" },
              { "tag_name": "v1.10.1", "prerelease": true, "draft": false, "html_url": "u" }
            ]
            """;

        Assert.Equal("v1.10.1", Pick(json, "1.10.0").Tag);
    }

    [Fact]
    public void OrderComesFromTheVersionNotTheList()
    {
        // GitHub sorts by creation date, so a patch to an older line published later appears first.
        // Trusting that order would push everyone from 1.10.0 back down to 1.9.3.
        var json = """
            [
              { "tag_name": "v1.9.3",  "prerelease": false, "draft": false, "html_url": "u" },
              { "tag_name": "v1.10.1", "prerelease": true,  "draft": false, "html_url": "u" },
              { "tag_name": "v1.10.0", "prerelease": false, "draft": false, "html_url": "u" }
            ]
            """;

        Assert.Equal("v1.10.1", Pick(json, "1.9.2").Tag);
    }

    [Fact]
    public void ATagThatIsNotAVersionIsIgnoredRatherThanCrashing()
    {
        var json = """
            [
              { "tag_name": "nightly", "prerelease": true,  "draft": false, "html_url": "u" },
              { "tag_name": "v1.10.1", "prerelease": true,  "draft": false, "html_url": "u" }
            ]
            """;

        Assert.Equal("v1.10.1", Pick(json, "1.10.0").Tag);
    }

    [Fact]
    public void AnEmptyListOffersNothing() => Assert.Null(Pick("[]", "1.10.0").Tag);

    [Fact]
    public void TheBetaWarningsExistInEveryLanguage()
    {
        // A missing key here would put "Msg_UpdateBetaAvailableFmt" on screen where the warning
        // should be — which is worse than no warning, because it looks like a bug rather than a
        // caution.
        string[] keys =
        {
            "Msg_UpdateBetaAvailableFmt", "Notif_UpdateBetaFmt", "Notif_UpdateBetaWhileRunningFmt",
            "Whatsnew_1_10_1",
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

                // And the notes have to actually say it, not merely exist. French spells it BÊTA,
                // which is correct there — the check accommodates the language rather than forcing
                // every translation to use the English spelling.
                var notes = McServerLauncher.Localization.Localizer.Get("Whatsnew_1_10_1");
                Assert.True(
                    notes.Contains("BETA", StringComparison.OrdinalIgnoreCase) ||
                    notes.Contains("BÊTA", StringComparison.OrdinalIgnoreCase),
                    $"las notas de la 1.10.1 en {lang} no dicen que es una beta");
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = original; }
    }
}
