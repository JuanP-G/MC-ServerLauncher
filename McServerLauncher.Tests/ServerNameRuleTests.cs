using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Folder names: what actually stops a server, and what merely looks unusual.
/// </summary>
/// <remarks>
/// The character list this rests on came from starting a real Paper server in a folder named after
/// each candidate, not from deciding which ones looked risky. That matters in both directions:
/// <c>!</c> and <c>+</c> genuinely stop it, and <c>#</c>, <c>&amp;</c>, <c>%</c>, <c>^</c>, <c>~</c>,
/// <c>'</c>, <c>;</c> and <c>,</c> do not — banning those would have refused perfectly good names.
/// </remarks>
public class ServerNameRuleTests
{
    private static string At(params string[] parts) =>
        Path.Combine(new[] { Path.GetTempPath() }.Concat(parts).ToArray());

    [Fact]
    public void AGoodNamePassesOnEveryType()
    {
        foreach (var type in Enum.GetValues<ServerType>())
        {
            Assert.Null(ServerNameRule.Check(At("servers", "Survival 2026"), type));
            Assert.Null(ServerNameRule.Check(At("servers", "Iberia (v2)"), type));
            Assert.Null(ServerNameRule.Check(At("servers", "Español-ñ"), type));
        }
    }

    [Theory]
    // Confirmed harmless by the sweep. Refusing these would be the app inventing problems.
    [InlineData("ser#ver")]
    [InlineData("ser&ver")]
    [InlineData("ser%ver")]
    [InlineData("ser^ver")]
    [InlineData("ser~ver")]
    [InlineData("ser;ver")]
    [InlineData("ser,ver")]
    [InlineData("ser@ver")]
    [InlineData("ser (1)")]
    [InlineData("ser_ver")]
    [InlineData("ser.ver")]
    public void CharactersThatOnlyLookDangerousAreAllowed(string name) =>
        Assert.Null(ServerNameRule.Check(At("servers", name), ServerType.Paper));

    [Theory]
    [InlineData("Java+Bedrock")]
    [InlineData("wow!")]
    public void TheTwoCharactersPaperRefusesAreCaught(string name)
    {
        var issue = ServerNameRule.Check(At("servers", name), ServerType.Paper);

        Assert.NotNull(issue);
        Assert.Equal(NameIssueKind.ServerRejectsCharacter, issue!.Kind);

        // A mod loader runs from those paths quite happily, and blocking them there would be a
        // rule invented rather than observed.
        Assert.Null(ServerNameRule.Check(At("servers", name), ServerType.NeoForge));
    }

    [Fact]
    public void TheSameCharacterInAParentIsADifferentProblem()
    {
        // Different because the fix is different: renaming a parent moves everything else under it,
        // so the app explains instead of offering to do it.
        var issue = ServerNameRule.Check(At("my+stuff", "servers", "survival"), ServerType.Paper);

        Assert.NotNull(issue);
        Assert.Equal(NameIssueKind.ServerRejectsParentCharacter, issue!.Kind);
        Assert.Equal("+", issue.Detail);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("Con.txt")]     // reserved with an extension too
    public void WindowsReservedNamesAreRefused(string name)
    {
        // Creating one of these fails outright, and nobody would guess why: "CON" looks like a
        // perfectly ordinary name for a server.
        var issue = ServerNameRule.Check(At("servers", name), ServerType.Vanilla);

        Assert.NotNull(issue);
        Assert.Equal(NameIssueKind.ReservedName, issue!.Kind);
    }

    [Theory]
    [InlineData("servidor.")]
    [InlineData("servidor ")]
    public void TrailingDotsAndSpacesAreRefused(string name)
    {
        // Windows trims them silently, so the folder ends up named differently from what was saved
        // in servers.json — and the server goes missing the next time the app looks for it.
        var issue = ServerNameRule.Check(Path.Combine(Path.GetTempPath(), "servers", name), ServerType.Vanilla);

        Assert.NotNull(issue);
        Assert.Equal(NameIssueKind.TrailingDotOrSpace, issue!.Kind);
    }

    [Fact]
    public void CharactersWindowsForbidsAreReportedNotSwallowed()
    {
        // The old behaviour: type "Mi:Server", get a folder called "MiServer", be told nothing.
        var issue = ServerNameRule.Check(Path.Combine(Path.GetTempPath(), "servers", "Mi:Server"), ServerType.Vanilla);

        Assert.NotNull(issue);
        Assert.Equal(NameIssueKind.InvalidCharacter, issue!.Kind);
        Assert.Equal(":", issue.Detail);

        // The suggestion is offered, not applied behind the user's back.
        Assert.Equal("MiServer", ServerNameRule.Clean("Mi:Server"));
    }

    [Fact]
    public void TheMostFundamentalProblemIsReportedFirst()
    {
        // A name that breaks two rules at once: fixing the Paper character would still leave a
        // folder Windows refuses to create, so that is the one worth saying.
        var issue = ServerNameRule.Check(At("servers", "Ja:va+Bedrock"), ServerType.Paper);

        Assert.Equal(NameIssueKind.InvalidCharacter, issue!.Kind);
    }

    [Fact]
    public void NothingToCheckIsNotAProblem()
    {
        Assert.Null(ServerNameRule.Check(null, ServerType.Paper));
        Assert.Null(ServerNameRule.Check("", ServerType.Paper));
        Assert.Null(ServerNameRule.Check("   ", ServerType.Paper));
    }

    [Fact]
    public void TheMessagesExistInEveryLanguage()
    {
        string[] keys =
        {
            "Msg_NameInvalidCharFmt", "Msg_NameReservedFmt",
            "Msg_NameTrailingDot", "Msg_BukkitPathParentFmt",
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
