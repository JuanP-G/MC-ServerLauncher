using System.Text.RegularExpressions;

namespace McServerLauncher.Tests;

/// <summary>
/// The notices file in the repository (<c>web/news.json</c>) and what keeps its bytes the bytes that
/// were signed.
/// </summary>
/// <remarks>
/// The app checks the signature over the bytes the server sends. This machine has
/// <c>core.autocrlf=true</c> and <c>.gitattributes</c> sets <c>* text=auto</c>, so without a rule of its
/// own a checkout would hand the file back with CRLF, the script would sign that, git would publish
/// LF, and every app would refuse the file. These tests are the net under that rule.
/// </remarks>
public class NewsFileTests
{
    private static string Root => LocalizationTests.RepoRoot();

    [Theory]
    [InlineData(@"^web/news\.json\s+-text\s*$")]
    [InlineData(@"^web/news\.json\.sig\s+-text\s*$")]
    public void GitIsToldNeverToTouchTheLineEndingsOfTheNoticesFileOrItsSignature(string rule)
    {
        var attributes = File.ReadAllLines(Path.Combine(Root, ".gitattributes"));

        Assert.Contains(attributes, line => Regex.IsMatch(line, rule));
    }

    [Fact]
    public void TheNoticesFileNeverHoldsACarriageReturnOrAByteOrderMark()
    {
        // Passes while the file does not exist yet: it arrives with the step that activates the feed.
        var path = Path.Combine(Root, "web", "news.json");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "web/news.json must be UTF-8 without a byte-order mark: the signature is over the bytes.");
        Assert.True(bytes.Length <= 64 * 1024, "the app refuses a notices file over 64 KiB");
    }

    [Fact]
    public void TheSignatureFileNeverHoldsACarriageReturn()
    {
        // Same story as the file it signs: it is served as written, and a copy that went through
        // autocrlf is a different file from the one the script produced.
        var path = Path.Combine(Root, "web", "news.json.sig");
        if (!File.Exists(path)) return;

        Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(path));
    }

    [Fact]
    public void TheSigningScriptRefusesWhatTheSignatureCouldNotSurvive()
    {
        // The script is the publisher's last guard. Each refusal here is a case that would otherwise
        // produce a signature the apps reject, or a notice the app drops without a word.
        var script = File.ReadAllText(Path.Combine(Root, "tools", "signing", "sign-news.ps1"));

        Assert.Contains("$bytes -contains 13", script);                 // CR
        Assert.Contains("$bytes[0] -eq 0xEF", script);                  // byte-order mark
        Assert.Contains("65536", script);                               // 64 KiB
        Assert.Contains("'until'", script);                             // urgent notices need an end
        Assert.Contains("'resolvedAt'", script);                        // resolved ones need a date
    }

    [Fact]
    public void TheSigningScriptWritesTheExpiryAndCapsHowFarAheadItCanBe()
    {
        // The file must expire (audit 2026-11-09, #162). The script writes the date, so renewing is
        // running it again; a cap keeps "expires in 2099" from being an expiry in name only.
        var script = File.ReadAllText(Path.Combine(Root, "tools", "signing", "sign-news.ps1"));

        Assert.Contains("[ValidateRange(1, 90)]", script);
        Assert.Contains("[int]$ExpiresInDays = 60", script);
        Assert.Contains("\"expires\"", script);
        Assert.Contains("-le $issuedAt", script);                       // expires must be after issued
    }

    /// <summary>
    /// The file in the repository is the one that is published: if it was edited and not signed again,
    /// every app refuses it and the feed is down without anything saying so until a user's app shows the
    /// card. This is the check that fails on the pull request instead.
    /// </summary>
    [Fact]
    public void TheCommittedNoticesFileIsSignedAndIsOneTheAppAccepts()
    {
        var path = Path.Combine(Root, "web", "news.json");
        var signaturePath = path + ".sig";
        if (!File.Exists(path)) return;     // before the feed was activated there was nothing to check

        var raw = File.ReadAllBytes(path);

        Assert.True(File.Exists(signaturePath), "web/news.json has no news.json.sig: run tools\\signing\\sign-news.ps1");
        Assert.True(McServerLauncher.Services.UpdateSignature.VerifyNews(raw, File.ReadAllText(signaturePath)),
            "the signature of web/news.json does not verify: it was edited after it was signed, or the " +
            "signature is stale. Run tools\\signing\\sign-news.ps1 and commit both files.");

        McServerLauncher.Services.NewsFeed feed;
        try { feed = McServerLauncher.Services.NewsFeedParser.Parse(raw); }
        catch (InvalidDataException ex)
        {
            Assert.Fail("web/news.json is not a file the app accepts (" + ex.Message + "). Run " +
                "tools\\signing\\sign-news.ps1, which writes the expires date, and commit both files.");
            return;
        }

        Assert.True(feed.Expires > feed.Issued);
        // Measured from now, not from issued: renewing leaves issued alone (it is the default `from` of the
        // notices), so a correctly renewed file is soon more than 90 days after it. Right after signing, the
        // script's own 90-day cap is what holds; "not expired yet" is deliberately not asserted, or a
        // lapsed file would turn every unrelated pull request red.
        Assert.True(feed.Expires - DateTimeOffset.UtcNow <= TimeSpan.FromDays(90),
            "expires is more than 90 days away: sign-news.ps1 never writes that");
    }

    [Theory]
    [InlineData("announcements.md")]
    [InlineData("announcements.es.md")]
    public void TheExampleInTheAnnouncementsArticleIsAFileTheAppReads(string article)
    {
        // The article shows the format with a complete example; if the format moves and the example does
        // not, whoever copies it publishes a file the app throws away item by item.
        var text = File.ReadAllText(Path.Combine(Root, "docs", "articles", article));
        var match = Regex.Match(text, "```json\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline);
        Assert.True(match.Success, "the article no longer has a JSON example");

        var feed = McServerLauncher.Services.NewsFeedParser.Parse(System.Text.Encoding.UTF8.GetBytes(match.Groups[1].Value));

        var item = Assert.Single(feed.Items);
        Assert.Equal("playit-2026-11-07", item.Id);
        Assert.Equal(McServerLauncher.Services.NewsKind.Incident, item.Kind);
        Assert.True(item.Only.UsesTunnels);
        Assert.NotNull(item.Link);
        Assert.NotNull(item.TextFor("es"));
        Assert.NotNull(item.TextFor("en"));
    }
}
