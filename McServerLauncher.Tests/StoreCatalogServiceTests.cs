using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The downloaded catalogue: what the app accepts from it, when it asks for it, and where it ranks.
/// </summary>
/// <remarks>
/// <para>
/// Written from the reason the catalogue exists. Rules that read a title and a line of description
/// cannot tell a backpack mod from an armour mod, and no amount of tuning them will; a model that
/// reads the text can. But the model runs somewhere else, on a schedule, and what reaches the app is
/// a file from the internet. So most of what is tested here is distrust: a file that is too big, in
/// a format from the future, half broken or full of control characters must leave the store exactly
/// as it was, and an offline machine must keep using the last good copy.
/// </para>
/// <para>
/// Nothing here touches the network: the download is a fake handler, and the files are written to a
/// temporary folder.
/// </para>
/// </remarks>
public class StoreCatalogServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-catalog-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    private static byte[] Gzip(string json)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zip.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    private const string Sodium =
        """
        {"version":1,"pipeline":"abc","projects":{
          "AANobbMI":{"t":"performance","x":["decoration"],"c":0.91,"h":"1","p":"abc",
                      "s":{"en":"Replaces the renderer to raise the frame rate.","es":"Sustituye el motor de dibujado para subir los fotogramas."}},
          "BackPack":{"t":"storage","c":0.8,"s":{"en":"Adds backpacks that hold more items."}}
        }}
        """;

    /// <summary>Answers each request with whatever the test set, and remembers what was asked.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Ok(byte[] body, string etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private (StoreCatalogService Service, FakeHandler Handler) Service()
    {
        var handler = new FakeHandler();
        return (new StoreCatalogService(_folder, new HttpClient(handler), "https://example.test/c.json.gz",
            () => _now), handler);
    }

    // --- Reading the file ----------------------------------------------------------------------

    [Fact]
    public void TheFileThePipelineWritesIsTheFileTheAppReads()
    {
        // The sample was written by tools/store-catalog/build_catalog.py (models replaced by
        // fakes, the file itself untouched). If either side changes the format without the other,
        // the catalogue silently stops loading in the app, which no other test would notice.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "store-catalog.sample.json.gz"));

        var entries = StoreCatalogService.Parse(bytes);

        Assert.Equal(3, entries.Count);
        Assert.Equal("performance", entries["AANobbMI"].Topic);
        Assert.Equal(5, entries["AANobbMI"].Summaries.Count);
        Assert.StartsWith("Sube los fotogramas", entries["AANobbMI"].Summaries["es"]);
    }

    [Fact]
    public void AnEntryCarriesTheTopicTheRunnersUpAndTheSentences()
    {
        var entries = StoreCatalogService.Parse(Gzip(Sodium));

        var sodium = entries["AANobbMI"];
        Assert.Equal("performance", sodium.Topic);
        Assert.Equal(new[] { "decoration" }, sodium.Also);
        Assert.Equal(0.91, sodium.Confidence);
        Assert.Equal("Sustituye el motor de dibujado para subir los fotogramas.", sodium.Summaries["es"]);
    }

    [Fact]
    public void AFileInAFormatFromTheFutureIsRefusedWhole()
    {
        // Better the rules than half a reading of something this build does not understand.
        Assert.ThrowsAny<Exception>(() => StoreCatalogService.Parse(Gzip("""{"version":2,"projects":{}}""")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"projects":{}}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"projects":[]}""")]
    [InlineData("not json at all")]
    public void ThingsThatAreNotACatalogueAreRefused(string json) =>
        Assert.ThrowsAny<Exception>(() => StoreCatalogService.Parse(Gzip(json)));

    [Fact]
    public void BytesThatAreNotGzipAreRefused() =>
        Assert.ThrowsAny<Exception>(() => StoreCatalogService.Parse(Encoding.UTF8.GetBytes("{}")));

    [Fact]
    public void AnEntryThatIsWrongIsSkippedAndTheRestSurvive()
    {
        var entries = StoreCatalogService.Parse(Gzip(
            """{"version":1,"projects":{"bad":"nope","also-bad":[],"ok":{"t":"qol"},"":{"t":"qol"}}}"""));

        Assert.Equal(new[] { "ok" }, entries.Keys);
    }

    [Fact]
    public void TextFromTheInternetIsMadeSafeForACard()
    {
        // A sentence with a line break or a control character, or one that never ends, is cut down
        // to one line of reasonable length before it can reach a card.
        var long400 = new string('x', 900);
        var json = "{\"version\":1,\"projects\":{\"p\":{\"t\":\"qol\",\"c\":7,\"s\":{\"en\":\"one\\ntwo\\u0007  three\"," +
                   "\"es\":\"" + long400 + "\",\"xx\":\"unknown language\",\"de\":\"   \"}}}}";

        var entry = StoreCatalogService.Parse(Gzip(json))["p"];

        Assert.Equal("one two three", entry.Summaries["en"]);
        Assert.Equal(400, entry.Summaries["es"].Length);
        Assert.False(entry.Summaries.ContainsKey("xx"));
        Assert.False(entry.Summaries.ContainsKey("de"));
        Assert.Equal(1, entry.Confidence);
    }

    // --- Asking for it -------------------------------------------------------------------------

    [Fact]
    public async Task ADownloadedCatalogueIsInstalledAndKeptOnDisk()
    {
        var (service, handler) = Service();
        handler.Respond = _ => Ok(Gzip(Sodium));

        var installed = await service.RefreshAsync();

        Assert.True(installed);
        Assert.Equal("performance", service.Find("AANobbMI")!.Topic);
        // A fresh instance, as after a restart and with no connection, finds the same answers.
        Assert.Equal("performance", new StoreCatalogService(_folder, new HttpClient(new FakeHandler()))
            .Find("AANobbMI")!.Topic);
    }

    [Fact]
    public async Task ItIsAskedForAtMostOnceADayAndOncePerHourWhenItFails()
    {
        var (service, handler) = Service();
        handler.Respond = _ => Ok(Gzip(Sodium));
        await service.RefreshAsync();

        _now += TimeSpan.FromHours(2);
        await service.RefreshAsync();
        Assert.Single(handler.Requests);          // the copy is two hours old: nothing to ask

        _now += TimeSpan.FromHours(24);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        await service.RefreshAsync();
        await service.RefreshAsync();
        Assert.Equal(2, handler.Requests.Count);  // a day later it asks, and not twice in a row
    }

    [Fact]
    public async Task TheEtagGoesBackAndNotModifiedKeepsTheCopy()
    {
        var (service, handler) = Service();
        handler.Respond = _ => Ok(Gzip(Sodium), "\"first\"");
        await service.RefreshAsync();

        _now += TimeSpan.FromDays(2);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        var installed = await service.RefreshAsync();

        Assert.False(installed);
        Assert.Equal("\"first\"", handler.Requests[1].Headers.IfNoneMatch.ToString());
        Assert.NotNull(service.Find("AANobbMI"));
    }

    [Fact]
    public async Task ABrokenDownloadReplacesNothing()
    {
        var (service, handler) = Service();
        handler.Respond = _ => Ok(Gzip(Sodium));
        await service.RefreshAsync();

        _now += TimeSpan.FromDays(2);
        handler.Respond = _ => Ok(Gzip("""{"version":9,"projects":{}}"""));
        var installed = await service.RefreshAsync();

        Assert.False(installed);
        Assert.Equal("performance", service.Find("AANobbMI")!.Topic);
    }

    [Fact]
    public async Task OfflineOrAServerErrorLeavesTheLastCopyAlone()
    {
        var (service, handler) = Service();
        handler.Respond = _ => Ok(Gzip(Sodium));
        await service.RefreshAsync();

        _now += TimeSpan.FromDays(2);
        handler.Respond = _ => throw new HttpRequestException("no connection");
        Assert.False(await service.RefreshAsync());

        _now += TimeSpan.FromHours(2);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.False(await service.RefreshAsync());

        Assert.NotNull(service.Find("AANobbMI"));
    }

    [Fact]
    public async Task AFileThatClaimsToBeHugeIsNotDownloaded()
    {
        var (service, handler) = Service();
        handler.Respond = _ =>
        {
            var response = Ok(Gzip(Sodium));
            response.Content.Headers.ContentLength = StoreCatalogService.MaxDownloadBytes + 1;
            return response;
        };

        Assert.False(await service.RefreshAsync());
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public void WithNothingDownloadedThereAreNoAnswersAndNoCrash()
    {
        var (service, _) = Service();

        Assert.Equal(0, service.Count);
        Assert.Null(service.Find("anything"));
        Assert.Null(service.Find(null));
        Assert.Null(service.SummaryFor("anything", "es"));
    }

    [Fact]
    public void ACorruptFileOnDiskIsNoCatalogue()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "store-catalog.json.gz"), new byte[] { 1, 2, 3 });

        Assert.Equal(0, Service().Service.Count);
    }

    // --- Where it ranks ------------------------------------------------------------------------

    private async Task<StoreCatalogService> Downloaded(string json)
    {
        var (service, handler) = Service();
        handler.Respond = _ => Ok(Gzip(json));
        await service.RefreshAsync();
        return service;
    }

    private static StoreItem Item(string id, string title, string description, string projectType = "mod",
        string? clientSide = null, params string[] categories) => new()
    {
        Id = id, Slug = id, Title = title, Description = description, ProjectType = projectType,
        ClientSide = clientSide, Categories = categories
    };

    [Fact]
    public async Task TheCatalogueDecidesTheTopicWhereTheRulesCouldNot()
    {
        // The rules see "armor" and the category "equipment"; the model read the whole text.
        var catalogue = await Downloaded("""{"version":1,"projects":{"p":{"t":"storage","x":["qol"]}}}""");
        var tags = new StoreTagService(Path.Combine(_folder, "none.json"), catalogue);
        var backpack = Item("p", "Backpacks", "Backpacks and armor", categories: new[] { "equipment" });

        var topics = tags.Classify(backpack, onlyKind: StoreTagService.TopicKind).Select(t => t.Id);

        Assert.Equal(new[] { "storage", "qol" }, topics);
    }

    [Fact]
    public async Task WhereTheCatalogueHasNothingTheRulesAnswer()
    {
        var catalogue = await Downloaded("""{"version":1,"projects":{"other":{"t":"storage"}}}""");
        var tags = new StoreTagService(Path.Combine(_folder, "none.json"), catalogue);

        var topics = tags.Classify(Item("p", "Sodium", "A renderer for fps", categories: new[] { "optimization" }),
            onlyKind: StoreTagService.TopicKind).Select(t => t.Id);

        Assert.Contains("performance", topics);
    }

    [Fact]
    public async Task ATopicTheAppDoesNotKnowFallsBackToTheRules()
    {
        // The catalogue can be ahead of the app, or wrong. Neither is shown.
        var catalogue = await Downloaded("""{"version":1,"projects":{"p":{"t":"made-up-tag"}}}""");
        var tags = new StoreTagService(Path.Combine(_folder, "none.json"), catalogue);

        var topics = tags.Classify(Item("p", "Sodium", "A renderer for fps", categories: new[] { "optimization" }),
            onlyKind: StoreTagService.TopicKind).Select(t => t.Id);

        Assert.Contains("performance", topics);
    }

    [Fact]
    public async Task APluginOnlyTopicIsNotGivenToAMod()
    {
        var catalogue = await Downloaded("""{"version":1,"projects":{"p":{"t":"economy","x":["qol"]}}}""");
        var tags = new StoreTagService(Path.Combine(_folder, "none.json"), catalogue);

        var forMod = tags.Classify(Item("p", "X", "Y", projectType: "mod"), onlyKind: StoreTagService.TopicKind);
        var forPlugin = tags.Classify(Item("p", "X", "Y", projectType: "plugin"), onlyKind: StoreTagService.TopicKind);

        Assert.DoesNotContain(forMod, t => t.Id == "economy");
        Assert.Equal("economy", forPlugin[0].Id);
    }

    [Fact]
    public async Task HowItIsInstalledStaysTheRulesEvenWhenTheCatalogueChoosesTheTopic()
    {
        var catalogue = await Downloaded("""{"version":1,"projects":{"p":{"t":"qol"}}}""");
        var tags = new StoreTagService(Path.Combine(_folder, "none.json"), catalogue);

        var all = tags.Classify(Item("p", "X", "Y", clientSide: "required")).Select(t => t.Id).ToArray();

        Assert.Equal("qol", all[0]);
        Assert.Contains("needs-client", all);
    }

    [Fact]
    public async Task ASentenceFromAPersonBeatsOneFromTheCatalogue()
    {
        var catalogue = await Downloaded("""{"version":1,"projects":{"p":{"s":{"en":"From the model.","es":"Del modelo."}}}}""");
        var handwritten = Path.Combine(_folder, "summaries.json");
        File.WriteAllText(handwritten, """{"version":1,"summaries":{"p":{"slug":"p","es":"De una persona.","en":"From a person."}}}""");
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("es");
        try
        {
            var summaries = new StoreSummaryService(handwritten, catalogue);

            Assert.Equal("De una persona.", summaries.Blurb(Item("p", "X", "Y")).Summary);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task TheCatalogueSpeaksTheLanguageAndFallsBackToEnglish()
    {
        var catalogue = await Downloaded("""{"version":1,"projects":{"p":{"s":{"en":"From the model.","es":"Del modelo."}}}}""");
        var summaries = new StoreSummaryService(Path.Combine(_folder, "none.json"), catalogue);
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("es");
            Assert.Equal("Del modelo.", summaries.Blurb(Item("p", "X", "Y")).Summary);

            CultureInfo.CurrentUICulture = new CultureInfo("fr");
            Assert.Equal("From the model.", summaries.Blurb(Item("p", "X", "Y")).Summary);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task TheAuthorsLineStillShowsUnderTheCataloguesSentence()
    {
        var catalogue = await Downloaded("""{"version":1,"projects":{"p":{"s":{"en":"From the model."}}}}""");
        var summaries = new StoreSummaryService(Path.Combine(_folder, "none.json"), catalogue);

        var blurb = summaries.Blurb(Item("p", "X", "What the author wrote"));

        Assert.Equal("From the model.", blurb.Summary);
        Assert.Equal("What the author wrote", blurb.Tagline);
    }
}
