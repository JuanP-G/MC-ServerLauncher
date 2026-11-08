using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Downloading the notices file: what is accepted, what is refused, and what a failure leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// Every case is one way the download could do the wrong thing without anyone noticing: install a file
/// whose signature does not check, take an old file for a new one, answer "no notices" when the truth
/// is "could not look", or keep a stale ETag that stops a corrected file ever being picked up.
/// </para>
/// <para>
/// No test touches the network or the real data folder: a fake handler answers, the keys are made per
/// test, the clock is a field and the cache is a temporary folder.
/// </para>
/// </remarks>
public class NewsFeedServiceTests : IDisposable
{
    private const string Primary = "https://primary.test/news.json";
    private const string Fallback = "https://fallback.test/news.json";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcsl-news-" + Path.GetRandomFileName());
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_folder, recursive: true); }
        catch { /* a temporary folder is not worth failing a test over */ }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            return Task.FromResult(Routes.TryGetValue(url, out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static byte[] Feed(int seq, string items = "") => Encoding.UTF8.GetBytes(
        "{\"schema\":1,\"seq\":" + seq + ",\"issued\":\"2026-11-07T10:00:00Z\",\"items\":[" + items + "]}\n");

    private const string TipItem =
        "{\"id\":\"tip-1\",\"kind\":\"tip\",\"text\":{\"en\":{\"title\":\"A tip\",\"body\":\"Words\"}}}";

    private string Sign(byte[] raw, ECDsa? key = null) => Convert.ToBase64String((key ?? _key).SignData(
        UpdateSignature.NewsMessage(Convert.ToHexString(SHA256.HashData(raw))),
        HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n";

    private static HttpResponseMessage Body(byte[] bytes, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (etag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    /// <summary>Serves a file and its signature at <paramref name="url"/>.</summary>
    private void Serve(FakeHandler handler, string url, byte[] raw, string? signature = null, string etag = "\"v1\"")
    {
        handler.Routes[url] = request => request.Headers.IfNoneMatch.Any(t => t.Tag == etag)
            ? Status(HttpStatusCode.NotModified)
            : Body(raw, etag);
        handler.Routes[url + ".sig"] = _ => Body(Encoding.UTF8.GetBytes(signature ?? Sign(raw)));
    }

    private (NewsFeedService Service, FakeHandler Handler) Make(params string[] urls)
    {
        var handler = new FakeHandler();
        var service = new NewsFeedService(_folder, new HttpClient(handler),
            urls.Length > 0 ? urls : [Primary], [PublicKey(_key)], () => _now);
        return (service, handler);
    }

    private NewsFeedService Reopen(FakeHandler? handler = null) => new(_folder,
        new HttpClient(handler ?? new FakeHandler()), [Primary], [PublicKey(_key)], () => _now);

    private static IEnumerable<string> IfNoneMatch(FakeHandler handler) =>
        handler.Requests.SelectMany(r => r.Headers.IfNoneMatch.Select(t => t.Tag));

    // ---- a first download ----

    [Fact]
    public async Task AFileWithAGoodSignatureIsInstalled()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));

        var status = await service.RefreshAsync();

        Assert.Equal(NewsFeedStatus.Ok, status);
        Assert.Equal(5, service.Feed!.Seq);
        Assert.Equal("tip-1", Assert.Single(service.Feed.Items).Id);
        Assert.Equal(5, service.AcceptedSeq);
        Assert.Equal(_now, service.CheckedAt);
        Assert.True(File.Exists(Path.Combine(_folder, "news.json")));
        Assert.True(File.Exists(Path.Combine(_folder, "news.json.sig")));
    }

    [Fact]
    public async Task OnlyTheFileAndItsSignatureAreAskedForAndNothingAboutTheUser()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(1));

        await service.RefreshAsync();

        Assert.Equal([Primary, Primary + ".sig"], handler.Requests.Select(r => r.RequestUri!.ToString()));
        foreach (var request in handler.Requests)
        {
            Assert.Equal(string.Empty, request.RequestUri!.Query);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("Accept-Language"));
        }
    }

    [Fact]
    public async Task AnUnchangedFileIsAskedForWithItsETagAndAnswers304()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();
        handler.Requests.Clear();
        _now += TimeSpan.FromMinutes(30);

        var status = await service.RefreshAsync();

        Assert.Equal(NewsFeedStatus.NotModified, status);
        Assert.Equal(["\"v1\""], IfNoneMatch(handler));
        Assert.Single(handler.Requests);                     // no second request for the signature
        Assert.Equal(_now, service.CheckedAt);
        Assert.NotNull(service.Feed);
    }

    // ---- what is refused ----

    [Fact]
    public async Task ABodyChangedAfterItWasSignedIsRefusedAndNothingIsInstalled()
    {
        var (service, handler) = Make();
        var signedFor = Feed(5, TipItem);
        Serve(handler, Primary, Encoding.UTF8.GetBytes("{\"schema\":1,\"seq\":6,\"issued\":\"2026-11-07T10:00:00Z\",\"items\":[]}\n"),
            signature: Sign(signedFor));

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());

        Assert.Null(service.Feed);
        Assert.False(File.Exists(Path.Combine(_folder, "news.json")));
    }

    [Fact]
    public async Task AFileSignedByAnotherKeyIsRefused()
    {
        var (service, handler) = Make();
        using var theirs = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var raw = Feed(5, TipItem);
        Serve(handler, Primary, raw, signature: Sign(raw, theirs));

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
        Assert.Null(service.Feed);
    }

    [Fact]
    public async Task AMissingSignatureIsRefused()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        handler.Routes[Primary + ".sig"] = _ => Status(HttpStatusCode.NotFound);

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
        Assert.Null(service.Feed);
    }

    [Fact]
    public async Task ASignatureThatIsNotBase64IsRefusedWithoutAnException()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5), signature: "<html>502 Bad Gateway</html>");

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
    }

    [Fact]
    public async Task AFileTooBigIsRefused()
    {
        var (service, handler) = Make();
        var padding = new string(' ', (int)NewsFeedService.MaxFeedBytes);
        var raw = Encoding.UTF8.GetBytes("{\"schema\":1,\"seq\":1,\"issued\":\"2026-11-07T10:00:00Z\",\"items\":[]}" + padding);
        Serve(handler, Primary, raw);

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
        Assert.Null(service.Feed);
    }

    [Fact]
    public async Task ASignatureTooBigIsRefused()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5), signature: new string('A', (int)NewsFeedService.MaxSignatureBytes + 1));

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
    }

    [Fact]
    public async Task AFileWithValidSignatureThatIsNotANoticesFileIsRefusedAndTheCopyStays()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();

        var junk = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}\n");
        Serve(handler, Primary, junk, etag: "\"v2\"");
        _now += TimeSpan.FromMinutes(30);

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
        Assert.Equal(5, service.Feed!.Seq);                  // the last good copy
    }

    // ---- the ETag is for a body that was accepted ----

    [Fact]
    public async Task TheETagOfARefusedBodyIsNeverKept()
    {
        // The case the order of things exists for. v2 arrives while the signature is still the old
        // one (the two files deploy at different moments). If its ETag were stored, every later request
        // would answer 304 and the corrected signature would never be fetched.
        var (service, handler) = Make();
        var v1 = Feed(5, TipItem);
        Serve(handler, Primary, v1, etag: "\"v1\"");
        await service.RefreshAsync();

        var v2 = Feed(6, TipItem);
        Serve(handler, Primary, v2, signature: Sign(v1), etag: "\"v2\"");       // new body, old signature
        _now += TimeSpan.FromMinutes(30);
        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
        Assert.Equal(5, service.Feed!.Seq);

        // The deploy finishes: same body, now with its own signature.
        Serve(handler, Primary, v2, etag: "\"v2\"");
        handler.Requests.Clear();
        _now += TimeSpan.FromMinutes(30);

        Assert.Equal(NewsFeedStatus.Ok, await service.RefreshAsync());
        Assert.Equal(6, service.Feed!.Seq);
        Assert.Equal(["\"v1\""], IfNoneMatch(handler));      // asked with the ETag of what it held, not of what it refused
    }

    // ---- failure is not "no notices" ----

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AServerErrorIsAFailureAndTheCopyIsKept(HttpStatusCode code)
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();

        handler.Routes[Primary] = _ => Status(code);
        _now += TimeSpan.FromMinutes(30);

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
        Assert.Equal(NewsFeedStatus.Failed, service.LastStatus);
        Assert.Equal("tip-1", Assert.Single(service.Feed!.Items).Id);
    }

    [Fact]
    public async Task NoNetworkIsAFailureAndNotAnException()
    {
        var (service, handler) = Make();
        handler.Routes[Primary] = _ => throw new HttpRequestException("no route to host");

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
    }

    [Fact]
    public async Task ATimeoutIsAFailure()
    {
        var (service, handler) = Make();
        handler.Routes[Primary] = _ => throw new TaskCanceledException("timed out");

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
    }

    [Fact]
    public async Task NoFileOnAnyHostIsNotAFailure()
    {
        // A project with nothing published. Not the same as a server that did not answer.
        var (service, _) = Make(Primary, Fallback);

        Assert.Equal(NewsFeedStatus.NoFeed, await service.RefreshAsync());
        Assert.Null(service.Feed);
    }

    [Fact]
    public async Task OneHostMissingTheFileAndAnotherFailingIsAFailure()
    {
        var (service, handler) = Make(Primary, Fallback);
        handler.Routes[Fallback] = _ => Status(HttpStatusCode.BadGateway);

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
    }

    // ---- two hosts ----

    [Fact]
    public async Task TheSecondHostIsUsedWhenTheFirstFails()
    {
        var (service, handler) = Make(Primary, Fallback);
        handler.Routes[Primary] = _ => Status(HttpStatusCode.BadGateway);
        Serve(handler, Fallback, Feed(5, TipItem));

        Assert.Equal(NewsFeedStatus.Ok, await service.RefreshAsync());
        Assert.Equal(5, service.Feed!.Seq);
    }

    [Fact]
    public async Task TheSecondHostIsUsedWhenTheFirstServesSomethingUntrustworthy()
    {
        var (service, handler) = Make(Primary, Fallback);
        using var theirs = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evil = Feed(9, TipItem);
        Serve(handler, Primary, evil, signature: Sign(evil, theirs));
        Serve(handler, Fallback, Feed(5, TipItem));

        Assert.Equal(NewsFeedStatus.Ok, await service.RefreshAsync());
        Assert.Equal(5, service.Feed!.Seq);
    }

    [Fact]
    public async Task TheSecondHostIsNotAskedWhenTheFirstAnswers()
    {
        var (service, handler) = Make(Primary, Fallback);
        Serve(handler, Primary, Feed(5));
        Serve(handler, Fallback, Feed(6));

        await service.RefreshAsync();

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "fallback.test");
    }

    [Fact]
    public async Task AFileOnTheFirstHostMissingButOnTheSecondIsUsed()
    {
        var (service, handler) = Make(Primary, Fallback);
        Serve(handler, Fallback, Feed(5, TipItem));

        Assert.Equal(NewsFeedStatus.Ok, await service.RefreshAsync());
    }

    // ---- replay ----

    [Fact]
    public async Task AFileWithALowerSeqThanAlreadyAcceptedIsRefused()
    {
        // Signed correctly, once. Served again to take the notices back to how they were.
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem), etag: "\"v5\"");
        await service.RefreshAsync();

        Serve(handler, Primary, Feed(3), etag: "\"v3\"");
        _now += TimeSpan.FromMinutes(30);

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync());
        Assert.Equal(5, service.Feed!.Seq);
        Assert.Equal(5, service.AcceptedSeq);
    }

    [Fact]
    public async Task TheSameSeqIsAccepted()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem), etag: "\"a\"");
        await service.RefreshAsync();

        Serve(handler, Primary, Feed(5), etag: "\"b\"");
        _now += TimeSpan.FromMinutes(30);

        Assert.Equal(NewsFeedStatus.Ok, await service.RefreshAsync());
        Assert.Empty(service.Feed!.Items);
    }

    [Fact]
    public async Task TheHighestSeqSurvivesARestart()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();

        var restarted = Reopen(new FakeHandler());
        Assert.Equal(5, restarted.AcceptedSeq);

        var replay = new FakeHandler();
        Serve(replay, Primary, Feed(2), etag: "\"old\"");
        var again = Reopen(replay);
        Assert.Equal(NewsFeedStatus.Failed, await again.RefreshAsync());
    }

    [Fact]
    public async Task TheSeqSurvivesTheCopyBeingLost()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();

        File.Delete(Path.Combine(_folder, "news.json"));
        var replay = new FakeHandler();
        Serve(replay, Primary, Feed(2), etag: "\"old\"");
        var again = Reopen(replay);

        Assert.Null(again.Feed);
        Assert.Equal(NewsFeedStatus.Failed, await again.RefreshAsync());
    }

    // ---- the copy on disk ----

    [Fact]
    public async Task TheCopyIsAvailableAfterARestartWithNoNetwork()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();

        var restarted = Reopen();

        Assert.Equal("tip-1", Assert.Single(restarted.Feed!.Items).Id);
        Assert.Equal(_now, restarted.CheckedAt);
    }

    [Fact]
    public async Task ACopyThatWasTamperedWithOnDiskIsNotTrusted()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();

        // Anything on the machine can write that folder.
        File.WriteAllBytes(Path.Combine(_folder, "news.json"), Feed(5,
            "{\"id\":\"evil\",\"kind\":\"incident\",\"until\":\"2026-11-20T00:00:00Z\",\"text\":{\"en\":{\"title\":\"Click\"}}}"));

        Assert.Null(Reopen().Feed);
    }

    [Fact]
    public async Task ACopyThatCannotBeTrustedEarnsNoIfNoneMatch()
    {
        // A 304 would mean "keep what you have", and there is nothing worth keeping.
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();
        File.WriteAllText(Path.Combine(_folder, "news.json.sig"), "AAAA");

        var next = new FakeHandler();
        Serve(next, Primary, Feed(5, TipItem));
        var again = Reopen(next);
        Assert.Equal(NewsFeedStatus.Ok, await again.RefreshAsync());

        Assert.Empty(IfNoneMatch(next));
    }

    [Fact]
    public void AFolderWithNothingInItIsAnEmptyStart()
    {
        var (service, _) = Make();

        Assert.Null(service.Feed);
        Assert.Equal(0, service.AcceptedSeq);
        Assert.Null(service.CheckedAt);
        Assert.Null(service.LastStatus);
    }

    // ---- not asking too often ----

    [Fact]
    public async Task ACopyConfirmedRecentlyIsNotAskedAgainWhenAskedNotToBeBothersome()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();
        handler.Requests.Clear();
        _now += TimeSpan.FromMinutes(4);

        var status = await service.RefreshAsync(onlyIfOlderThan: TimeSpan.FromMinutes(10));

        Assert.Equal(NewsFeedStatus.NotModified, status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnOldEnoughCopyIsAskedAgain()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5, TipItem));
        await service.RefreshAsync();
        handler.Requests.Clear();
        _now += TimeSpan.FromMinutes(11);

        await service.RefreshAsync(onlyIfOlderThan: TimeSpan.FromMinutes(10));

        Assert.NotEmpty(handler.Requests);
    }

    [Fact]
    public async Task AFailureIsNotRetriedAtOnceByWhoeverIsNotOnATimer()
    {
        var (service, handler) = Make();
        handler.Routes[Primary] = _ => Status(HttpStatusCode.BadGateway);
        await service.RefreshAsync();
        handler.Requests.Clear();

        // Opening the section again a minute later, offline: no new wait for a timeout.
        _now += TimeSpan.FromMinutes(1);
        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync(onlyIfOlderThan: TimeSpan.FromMinutes(10)));
        Assert.Empty(handler.Requests);

        // Past the gap it tries again; and the timer (no argument) always does.
        _now += TimeSpan.FromMinutes(2);
        await service.RefreshAsync(onlyIfOlderThan: TimeSpan.FromMinutes(10));
        Assert.NotEmpty(handler.Requests);
        handler.Requests.Clear();
        await service.RefreshAsync();
        Assert.NotEmpty(handler.Requests);
    }

    [Fact]
    public async Task ACancelledWaitReturnsAFailureInsteadOfThrowing()
    {
        var (service, handler) = Make();
        Serve(handler, Primary, Feed(5));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Equal(NewsFeedStatus.Failed, await service.RefreshAsync(ct: cancelled.Token));
    }

    // ---- the addresses ----

    [Fact]
    public void TheTwoAddressesAreHttpsOnHostsTheProjectOwns()
    {
        Assert.Equal(2, NewsFeedService.DefaultUrls.Count);
        foreach (var url in NewsFeedService.DefaultUrls)
        {
            Assert.EndsWith("/news.json", url);
            Assert.True(AppHttp.IsAllowedUrl(url, NewsFeedParser.LinkHosts), url);
        }
        Assert.NotEqual(new Uri(NewsFeedService.DefaultUrls[0]).Host, new Uri(NewsFeedService.DefaultUrls[1]).Host);
    }
}
