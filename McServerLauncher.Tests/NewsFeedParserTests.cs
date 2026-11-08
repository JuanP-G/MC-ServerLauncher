using System.IO;
using System.Text;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading the notices file: what is accepted, what is dropped on the spot and what is trimmed.
/// </summary>
/// <remarks>
/// The file is signed, but a signature only says who wrote it. Each case here is a way a file by the
/// right author could still do harm to the screen: a banner that can never be cleared, a link to
/// somewhere else, a control character in a title, a notice meant for some people shown to all.
/// </remarks>
public class NewsFeedParserTests
{
    private static readonly DateTimeOffset Issued = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A file with the given items (raw JSON objects).</summary>
    private static byte[] Feed(string items, string head = "\"schema\":1,\"seq\":5,\"issued\":\"2026-11-07T10:00:00Z\"") =>
        Encoding.UTF8.GetBytes("{" + head + ",\"items\":[" + items + "]}");

    private const string Words = "\"text\":{\"es\":{\"title\":\"Aviso\",\"body\":\"Cuerpo\"},\"en\":{\"title\":\"Notice\",\"body\":\"Body\"}}";

    private static string Incident(string id = "playit-1", string extra = "") =>
        "{\"id\":\"" + id + "\",\"kind\":\"incident\",\"status\":\"active\",\"from\":\"2026-11-07T09:00:00Z\"," +
        "\"until\":\"2026-11-08T09:00:00Z\"," + Words + extra + "}";

    private static NewsItem Single(string item) => Assert.Single(NewsFeedParser.Parse(Feed(item)).Items);

    // ---- the file as a whole ----

    [Fact]
    public void AWellFormedFileIsRead()
    {
        var feed = NewsFeedParser.Parse(Feed(Incident()));

        Assert.Equal(5, feed.Seq);
        Assert.Equal(Issued, feed.Issued);
        var item = Assert.Single(feed.Items);
        Assert.Equal("playit-1", item.Id);
        Assert.Equal(NewsKind.Incident, item.Kind);
        Assert.Equal(NewsStatus.Active, item.Status);
        Assert.Equal("Notice", item.TextFor("en")!.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schema\":2,\"seq\":1,\"issued\":\"2026-11-07T10:00:00Z\",\"items\":[]}")]
    [InlineData("{\"schema\":1,\"issued\":\"2026-11-07T10:00:00Z\",\"items\":[]}")]
    [InlineData("{\"schema\":1,\"seq\":0,\"issued\":\"2026-11-07T10:00:00Z\",\"items\":[]}")]
    [InlineData("{\"schema\":1,\"seq\":1,\"issued\":\"yesterday\",\"items\":[]}")]
    [InlineData("{\"schema\":1,\"seq\":1,\"issued\":\"2026-11-07T10:00:00Z\"}")]
    [InlineData("{\"schema\":1,\"seq\":1,\"issued\":\"2026-11-07T10:00:00Z\",\"items\":{}}")]
    public void SomethingThatIsNotANoticesFileIsRefused(string text)
    {
        Assert.Throws<InvalidDataException>(() => NewsFeedParser.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void AnEmptyListOfItemsIsAValidFile()
    {
        // The file the feed starts with: nothing to say, but signed and counted.
        Assert.Empty(NewsFeedParser.Parse(Feed(string.Empty)).Items);
    }

    [Fact]
    public void FieldsThisBuildDoesNotKnowAreIgnored()
    {
        // How the format grows without a new schema: an older app reads what it knows.
        var feed = NewsFeedParser.Parse(Encoding.UTF8.GetBytes(
            "{\"schema\":1,\"seq\":2,\"issued\":\"2026-11-07T10:00:00Z\",\"future\":{\"a\":1},\"items\":[" +
            Incident(extra: ",\"priority\":9") + "]}"));

        Assert.Single(feed.Items);
    }

    [Fact]
    public void AnItemThatIsWrongIsSkippedAndTheOthersStay()
    {
        var feed = NewsFeedParser.Parse(Feed(
            "42," + "{\"id\":\"no-kind\"," + Words + "}," + Incident("good") + ",\"text\""));

        Assert.Equal(["good"], feed.Items.Select(i => i.Id));
    }

    [Fact]
    public void TheFirstItemWithAnIdIsTheOneThatCounts()
    {
        var feed = NewsFeedParser.Parse(Feed(Incident("same") + "," + Incident("same", ",\"link\":\"https://playit.gg/x\"")));

        Assert.Null(Assert.Single(feed.Items).Link);
    }

    [Fact]
    public void AtMostFiftyItemsAreRead()
    {
        var many = string.Join(",", Enumerable.Range(0, 80).Select(i => Incident("n" + i)));

        Assert.Equal(NewsFeedParser.MaxItems, NewsFeedParser.Parse(Feed(many)).Items.Count);
    }

    // ---- identifiers ----

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("-leading-dash")]
    [InlineData("ñandú")]
    public void AnIdThatIsNotPlainIsSkipped(string id)
    {
        Assert.Empty(NewsFeedParser.Parse(Feed(Incident(id))).Items);
    }

    [Fact]
    public void AnIdTooLongIsSkipped()
    {
        Assert.Empty(NewsFeedParser.Parse(Feed(Incident(new string('a', NewsFeedParser.MaxIdLength + 1)))).Items);
        Assert.Single(NewsFeedParser.Parse(Feed(Incident(new string('a', NewsFeedParser.MaxIdLength)))).Items);
    }

    // ---- kinds, dates and status ----

    [Fact]
    public void AnIncidentWithoutAnEndDateIsDropped()
    {
        // A banner with no end is one nobody can ever clear.
        var item = "{\"id\":\"x\",\"kind\":\"incident\",\"from\":\"2026-11-07T09:00:00Z\"," + Words + "}";

        Assert.Empty(NewsFeedParser.Parse(Feed(item)).Items);
    }

    [Fact]
    public void ASecurityNoticeWithoutAnEndDateIsDropped()
    {
        var item = "{\"id\":\"x\",\"kind\":\"security\"," + Words + "}";

        Assert.Empty(NewsFeedParser.Parse(Feed(item)).Items);
    }

    [Fact]
    public void ATipMayHaveNoEndDate()
    {
        var item = Single("{\"id\":\"t\",\"kind\":\"tip\"," + Words + "}");

        Assert.Null(item.Until);
        Assert.Null(item.EndsAt);
        Assert.Equal(Issued, item.From);       // no "from": the file's own date
    }

    [Fact]
    public void AnEndDateBeforeTheStartIsDropped()
    {
        var item = "{\"id\":\"x\",\"kind\":\"incident\",\"from\":\"2026-11-07T09:00:00Z\"," +
                   "\"until\":\"2026-11-07T08:00:00Z\"," + Words + "}";

        Assert.Empty(NewsFeedParser.Parse(Feed(item)).Items);
    }

    [Fact]
    public void AnUrgentNoticeIsCutShortAtThirtyDays()
    {
        // The file has no expiry of its own; this is what stops a forgotten notice becoming permanent.
        var item = Single("{\"id\":\"x\",\"kind\":\"incident\",\"from\":\"2026-11-07T09:00:00Z\"," +
                          "\"until\":\"2030-01-01T00:00:00Z\"," + Words + "}");

        Assert.Equal(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero) + NewsFeedParser.MaxUrgentSpan, item.Until);
    }

    [Fact]
    public void ADateWithNoOffsetIsReadAsUtcNotAsTheReadersClock()
    {
        var item = Single("{\"id\":\"x\",\"kind\":\"incident\",\"from\":\"2026-11-07T09:00:00\"," +
                          "\"until\":\"2026-11-08T09:00:00\"," + Words + "}");

        Assert.Equal(TimeSpan.Zero, item.From.Offset);
        Assert.Equal(9, item.From.Hour);
    }

    [Fact]
    public void AResolvedIncidentNeedsToSayWhen()
    {
        var withoutDate = "{\"id\":\"x\",\"kind\":\"incident\",\"status\":\"resolved\",\"from\":\"2026-11-07T09:00:00Z\"," +
                          "\"until\":\"2026-11-20T09:00:00Z\"," + Words + "}";
        var withDate = withoutDate.Replace("\"status\":\"resolved\"",
            "\"status\":\"resolved\",\"resolvedAt\":\"2026-11-07T12:00:00Z\"");

        Assert.Empty(NewsFeedParser.Parse(Feed(withoutDate)).Items);

        var item = Single(withDate);
        Assert.Equal(NewsStatus.Resolved, item.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero), item.ResolvedAt);
    }

    [Theory]
    [InlineData("\"status\":\"maybe\"")]
    [InlineData("\"status\":3")]
    public void AStatusThatIsNotOneOfTheTwoIsDropped(string status)
    {
        Assert.Empty(NewsFeedParser.Parse(Feed(Incident(extra: "," + status))).Items);
    }

    [Fact]
    public void ATipIsNeverResolved()
    {
        var item = Single("{\"id\":\"t\",\"kind\":\"tip\",\"status\":\"resolved\"," + Words + "}");

        Assert.Equal(NewsStatus.Active, item.Status);
        Assert.Null(item.ResolvedAt);
    }

    [Fact]
    public void ADateThatDoesNotParseDropsTheItem()
    {
        Assert.Empty(NewsFeedParser.Parse(Feed(
            "{\"id\":\"x\",\"kind\":\"incident\",\"from\":\"soon\",\"until\":\"2026-11-08T09:00:00Z\"," + Words + "}")).Items);
    }

    // ---- when it is listed ----

    [Fact]
    public void AnActiveNoticeIsListedBetweenItsDatesOnly()
    {
        var item = Single(Incident());
        var from = item.From;
        var until = item.Until!.Value;

        Assert.False(item.IsListedAt(from - TimeSpan.FromSeconds(1)));
        Assert.True(item.IsListedAt(from));
        Assert.True(item.IsListedAt(until - TimeSpan.FromSeconds(1)));
        Assert.False(item.IsListedAt(until));
    }

    [Fact]
    public void AResolvedNoticeIsRetiredAWeekAfterItWasResolved()
    {
        var resolved = Single("{\"id\":\"x\",\"kind\":\"incident\",\"status\":\"resolved\"," +
                              "\"from\":\"2026-11-07T09:00:00Z\",\"until\":\"2026-12-01T09:00:00Z\"," +
                              "\"resolvedAt\":\"2026-11-07T12:00:00Z\"," + Words + "}");
        var at = resolved.ResolvedAt!.Value;

        Assert.True(resolved.IsListedAt(at + TimeSpan.FromDays(6.9)));
        Assert.False(resolved.IsListedAt(at + TimeSpan.FromDays(7)));
    }

    [Fact]
    public void AResolvedNoticeWhoseEndComesSoonerIsRetiredThen()
    {
        // The cap is on the end date too: a week is the longest it stays, not the length it must.
        var resolved = Single("{\"id\":\"x\",\"kind\":\"incident\",\"status\":\"resolved\"," +
                              "\"from\":\"2026-11-07T09:00:00Z\",\"until\":\"2026-11-09T09:00:00Z\"," +
                              "\"resolvedAt\":\"2026-11-07T12:00:00Z\"," + Words + "}");

        Assert.Equal(resolved.Until, resolved.EndsAt);
    }

    // ---- text ----

    [Fact]
    public void ControlCharactersAndLineBreaksNeverReachATitle()
    {
        var item = Single("{\"id\":\"x\",\"kind\":\"tip\",\"text\":{\"en\":{\"title\":\"A\\u0007\\n\\tB\\r\\nC\",\"body\":\"one\\ntwo\"}}}");
        var text = item.TextFor("en")!;

        Assert.Equal("A B C", text.Title);
        Assert.Equal("one two", text.Body);
    }

    [Fact]
    public void TextsAreCutAtTheirLimits()
    {
        var item = Single("{\"id\":\"x\",\"kind\":\"tip\",\"text\":{\"en\":{\"title\":\"" + new string('t', 500) +
                          "\",\"body\":\"" + new string('b', 5000) + "\"}}}");
        var text = item.TextFor("en")!;

        Assert.Equal(NewsFeedParser.MaxTitleLength, text.Title.Length);
        Assert.Equal(NewsFeedParser.MaxBodyLength, text.Body.Length);
    }

    [Fact]
    public void ALanguageWithoutATitleIsSkippedAndAnItemWithNoLanguageIsDropped()
    {
        Assert.Empty(NewsFeedParser.Parse(Feed("{\"id\":\"x\",\"kind\":\"tip\",\"text\":{\"en\":{\"body\":\"b\"}}}")).Items);
        Assert.Empty(NewsFeedParser.Parse(Feed("{\"id\":\"x\",\"kind\":\"tip\",\"text\":{\"it\":{\"title\":\"t\"}}}")).Items);
        Assert.Empty(NewsFeedParser.Parse(Feed("{\"id\":\"x\",\"kind\":\"tip\"}")).Items);
    }

    [Fact]
    public void ABodyMayBeLeftOut()
    {
        var item = Single("{\"id\":\"x\",\"kind\":\"tip\",\"text\":{\"en\":{\"title\":\"Just a title\"}}}");

        Assert.Equal(string.Empty, item.TextFor("en")!.Body);
    }

    // ---- language ----

    [Fact]
    public void TheLanguageOfTheAppIsUsedWhenTheNoticeHasIt()
    {
        var item = Single(Incident());

        Assert.Equal("Aviso", item.TextFor("es")!.Title);
        Assert.Equal("Notice", item.TextFor("en")!.Title);
    }

    [Fact]
    public void ALanguageTheNoticeLacksFallsBackToEnglish()
    {
        // Portuguese is a language of the app; this notice was only written in Spanish and English.
        Assert.Equal("Notice", Single(Incident()).TextFor("pt")!.Title);
    }

    [Fact]
    public void ALanguageTheAppDoesNotHaveFallsBackToEnglishToo()
    {
        // The UI of an unsupported system language shows Spanish (the neutral strings), but a notice
        // is a sentence for a person to read: English is the one most of them can.
        Assert.Equal("Notice", Single(Incident()).TextFor("it")!.Title);
    }

    [Fact]
    public void WithNoEnglishTheFirstLanguageThereIsIsUsed()
    {
        var item = Single("{\"id\":\"x\",\"kind\":\"tip\",\"text\":{\"fr\":{\"title\":\"Salut\"},\"de\":{\"title\":\"Hallo\"}}}");

        Assert.Equal("Hallo", item.TextFor("pt")!.Title);      // "de" comes before "fr" in the app's order
    }

    // ---- links ----

    [Theory]
    [InlineData("https://playit.gg/account/tunnels", true)]
    [InlineData("https://github.com/JuanP-G/MC-ServerLauncher/issues/1", true)]
    [InlineData("https://mc-server-launcher.vercel.app/guias/", true)]
    [InlineData("https://juanp-g.github.io/MC-ServerLauncher/docs/", true)]
    [InlineData("http://playit.gg/", false)]
    [InlineData("https://evil.example/playit.gg", false)]
    [InlineData("https://playit.gg.evil.example/", false)]
    [InlineData("https://user:pass@playit.gg/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("not a link", false)]
    public void OnlyAnHttpsLinkToTheProjectOrPlayitSurvives(string link, bool kept)
    {
        var item = Single(Incident(extra: ",\"link\":\"" + link + "\""));

        Assert.Equal(kept ? link : null, item.Link);
    }

    [Fact]
    public void ABadLinkLosesTheLinkNotTheNotice()
    {
        Assert.NotNull(Single(Incident(extra: ",\"link\":\"http://x\"")));
    }

    // ---- who it is for ----

    private static NewsContext Machine(bool tunnels = false, string os = "windows", string version = "1.22.0") =>
        new(os, Version.Parse(version), tunnels, "es");

    [Fact]
    public void WithNoAudienceItIsForEverybody()
    {
        var item = Single(Incident());

        Assert.True(item.Only.Matches(Machine()));
        Assert.True(item.Only.Matches(Machine(tunnels: true, os: "linux", version: "0.1")));
    }

    [Fact]
    public void ATunnelNoticeIsOnlyForWhoUsesTunnels()
    {
        var item = Single(Incident(extra: ",\"only\":{\"usesTunnels\":true}"));

        Assert.True(item.Only.Matches(Machine(tunnels: true)));
        Assert.False(item.Only.Matches(Machine(tunnels: false)));
    }

    [Fact]
    public void ANoTunnelNoticeIsOnlyForWhoDoesNot()
    {
        var item = Single(Incident(extra: ",\"only\":{\"usesTunnels\":false}"));

        Assert.False(item.Only.Matches(Machine(tunnels: true)));
        Assert.True(item.Only.Matches(Machine(tunnels: false)));
    }

    [Fact]
    public void ASystemListNarrowsItAndIgnoresCase()
    {
        var item = Single(Incident(extra: ",\"only\":{\"os\":[\"Windows\",\"macOS\"]}"));

        Assert.True(item.Only.Matches(Machine(os: "windows")));
        Assert.True(item.Only.Matches(Machine(os: "macos")));
        Assert.False(item.Only.Matches(Machine(os: "linux")));
    }

    [Fact]
    public void VersionBoundsAreInclusive()
    {
        var item = Single(Incident(extra: ",\"only\":{\"minVersion\":\"1.21.0\",\"maxVersion\":\"1.22.0\"}"));

        Assert.False(item.Only.Matches(Machine(version: "1.20.9")));
        Assert.True(item.Only.Matches(Machine(version: "1.21.0")));
        Assert.True(item.Only.Matches(Machine(version: "1.22.0")));
        Assert.False(item.Only.Matches(Machine(version: "1.22.1")));
    }

    [Fact]
    public void TheConditionsAddUp()
    {
        var item = Single(Incident(extra: ",\"only\":{\"usesTunnels\":true,\"os\":[\"windows\"],\"minVersion\":\"1.22.0\"}"));

        Assert.True(item.Only.Matches(Machine(tunnels: true)));
        Assert.False(item.Only.Matches(Machine(tunnels: true, os: "linux")));
        Assert.False(item.Only.Matches(Machine(tunnels: false)));
        Assert.False(item.Only.Matches(Machine(tunnels: true, version: "1.21.9")));
    }

    [Theory]
    [InlineData("\"only\":3")]
    [InlineData("\"only\":{\"usesTunnels\":\"yes\"}")]
    [InlineData("\"only\":{\"os\":\"windows\"}")]
    [InlineData("\"only\":{\"os\":[]}")]
    [InlineData("\"only\":{\"os\":[3]}")]
    [InlineData("\"only\":{\"minVersion\":\"one\"}")]
    [InlineData("\"only\":{\"maxVersion\":2}")]
    public void AnAudienceThatCannotBeUnderstoodDropsTheNoticeInsteadOfShowingItToAll(string only)
    {
        // Ignoring a condition that does not parse would show the notice to people it was not for.
        Assert.Empty(NewsFeedParser.Parse(Feed(Incident(extra: "," + only))).Items);
    }
}
