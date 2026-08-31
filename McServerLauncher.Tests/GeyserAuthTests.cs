using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The authentication Geyser uses towards the Java server — the setting that was never written.
/// </summary>
/// <remarks>
/// A server with Floodgate installed sat on <c>auth-type: online</c> for eight days. Geyser tried to
/// authenticate against Mojang, had no account to do it with ("Cannot reply to ClientboundHelloPacket
/// without profile and access token"), and Floodgate turned every Bedrock player away asking whether
/// it was configured correctly. The app had documented that Geyser would work this out for itself.
/// It does not.
/// </remarks>
public class GeyserAuthTests
{
    /// <summary>A cut of the real Geyser 2.11 config, comments and all.</summary>
    /// <remarks>
    /// Taken from a config Geyser itself wrote rather than invented for the test: the section was
    /// renamed from <c>remote:</c> to <c>java:</c> in that version, and a fixture using the old name
    /// would have let the bug through.
    /// </remarks>
    private const string RealConfig = """
        # --------------------------------
        # Geyser Configuration File
        # --------------------------------

        # Network settings for the Bedrock listener
        bedrock:
          # The IP address that Geyser will bind on.
          address: 0.0.0.0

          # The port that will Geyser will listen on.
          port: 19132

        # Network settings for the Java server connection
        java:
          # What type of authentication Bedrock players will be checked against.
          # Can be "floodgate", "online", or "offline".
          auth-type: online

        # MOTD settings
        motd:
          primary-motd: Geyser

        advanced:
          # Floodgate uses encryption to ensure use from authorized sources.
          # If you're using a plugin version of Floodgate on the same server, the key will
          # automatically be picked up from Floodgate.
          floodgate-key-file: key.pem
        """;

    private static string ValueOf(string yaml, string key) =>
        yaml.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.StartsWith(key + ":", StringComparison.Ordinal))[(key.Length + 1)..]
            .Trim();

    [Fact]
    public void FloodgateAuthenticationIsWrittenNotHopedFor()
    {
        var fixedUp = GeyserConfigService.SetJavaAuth(RealConfig, floodgate: true, "../floodgate/key.pem");

        Assert.Equal("floodgate", ValueOf(fixedUp, "auth-type"));
        Assert.Equal("../floodgate/key.pem", ValueOf(fixedUp, "floodgate-key-file"));
    }

    [Fact]
    public void WithoutFloodgateOnlineIsCorrectAndTheKeyIsLeftAlone()
    {
        // Telling Geyser to check against a Floodgate that is not installed would turn away every
        // Bedrock player just as surely, in the opposite direction.
        var fixedUp = GeyserConfigService.SetJavaAuth(RealConfig, floodgate: false);

        Assert.Equal("online", ValueOf(fixedUp, "auth-type"));
        Assert.Equal("key.pem", ValueOf(fixedUp, "floodgate-key-file"));
    }

    [Fact]
    public void NothingElseInTheFileMoves()
    {
        // Geyser's comments are its documentation, and the whole reason this edits line by line
        // instead of parsing the YAML and writing it back out.
        var fixedUp = GeyserConfigService.SetJavaAuth(RealConfig, floodgate: true, "../floodgate/key.pem");

        var before = RealConfig.Split('\n');
        var after = fixedUp.Split('\n');

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.Count(l => l.TrimStart().StartsWith('#')),
                     after.Count(l => l.TrimStart().StartsWith('#')));

        // Exactly two lines differ: the two this is allowed to touch.
        Assert.Equal(2, before.Zip(after).Count(p => p.First != p.Second));
    }

    [Fact]
    public void RunningItTwiceChangesNothingTheSecondTime()
    {
        // It runs on every start of a crossplay server, so a non-idempotent edit would grow the
        // file a little each time until something broke.
        var once = GeyserConfigService.SetJavaAuth(RealConfig, floodgate: true, "../floodgate/key.pem");
        var twice = GeyserConfigService.SetJavaAuth(once, floodgate: true, "../floodgate/key.pem");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void AConfigAlreadyCorrectIsNotTouched()
    {
        var already = RealConfig
            .Replace("auth-type: online", "auth-type: floodgate")
            .Replace("floodgate-key-file: key.pem", "floodgate-key-file: ../floodgate/key.pem");

        Assert.Equal(already, GeyserConfigService.SetJavaAuth(already, floodgate: true, "../floodgate/key.pem"));
    }

    [Fact]
    public void ANewConfigUsesTheSectionGeyserActuallyReads()
    {
        // The bug, in one assertion. Geyser 2.11 renamed "remote:" to "java:", and a config written
        // with the old name is silently ignored — which is how a server ran for over a week with
        // authentication the app believed it had set.
        var written = GeyserConfigService.MinimalConfig(19132, 51917, floodgate: true);

        Assert.DoesNotContain("remote:", written, StringComparison.Ordinal);
        Assert.Contains("java:", written, StringComparison.Ordinal);
        Assert.Equal("floodgate", ValueOf(written, "auth-type"));
    }

    [Fact]
    public void AConfigWithNoJavaSectionGetsOne()
    {
        const string old = "bedrock:\n  port: 19132\n";

        var fixedUp = GeyserConfigService.SetJavaAuth(old, floodgate: true);

        Assert.Contains("java:", fixedUp, StringComparison.Ordinal);
        Assert.Equal("floodgate", ValueOf(fixedUp, "auth-type"));
    }

    [Fact]
    public void TheRepairMessageExistsInEveryLanguage()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            foreach (var lang in new[] { "es", "en", "pt", "fr", "de" })
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo(lang);

                var value = McServerLauncher.Localization.Localizer.Get("Msg_GeyserConfigRepairedFmt");
                Assert.False(string.IsNullOrWhiteSpace(value) || value == "Msg_GeyserConfigRepairedFmt",
                    $"falta el mensaje en {lang}");
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = original; }
    }
}
