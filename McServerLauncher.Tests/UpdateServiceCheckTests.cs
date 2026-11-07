using System.Net;
using System.Text;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What the version check tells the app when GitHub does not give it a release list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UpdateService.CheckAsync"/> used to answer null to any status that was not a success,
/// and null is the answer for "nothing newer". So the rate limit (403 or 429) and an outage (5xx)
/// both read as "you are on the latest version", the failure indicator never lit, and a silent
/// background check that hit one even took back an update that was already on offer.
/// </para>
/// <para>
/// The three outcomes are pinned end to end, through <see cref="UpdateCheck.RunAsync"/>, since that
/// is what reaches the screen: a newer release, "up to date" (the same version or an older one) and
/// "could not check" (an error status, no connection, an answer that is not a release list).
/// Nothing here touches the network: GitHub is a fake handler.
/// </para>
/// </remarks>
public class UpdateServiceCheckTests
{
    private static readonly Version Running = new(1, 22, 0);

    /// <summary>Answers every request with whatever the test set, or fails like a dead network.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A release list with one published, stable release at <paramref name="tag"/>.</summary>
    private static string ReleaseList(string tag) => $$"""
        [
          { "tag_name": "{{tag}}", "prerelease": false, "draft": false,
            "html_url": "https://github.com/JuanP-G/MC-ServerLauncher/releases/tag/{{tag}}", "assets": [] }
        ]
        """;

    private static (UpdateService Service, FakeHandler Handler) Service(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler { Respond = respond };
        return (new UpdateService(new HttpClient(handler)), handler);
    }

    private static Task<(UpdateCheckState State, UpdateService.UpdateInfo? Info)> Check(UpdateService service) =>
        UpdateCheck.RunAsync(() => service.CheckAsync(Running));

    // --- A newer release ---------------------------------------------------------------------

    [Fact]
    public async Task ANewerReleaseIsOffered()
    {
        var (service, handler) = Service(_ => Json(ReleaseList("v1.23.0")));

        var (state, info) = await Check(service);

        Assert.Equal(UpdateCheckState.Available, state);
        Assert.Equal("1.23.0", info!.Version);
        // One request, to the release list on GitHub's API.
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.github.com", request.RequestUri!.Host);
    }

    // --- Up to date --------------------------------------------------------------------------

    [Theory]
    [InlineData("v1.22.0")]  // the one running
    [InlineData("v1.21.2")]  // an older one, say a patch to an older line published late
    public async Task TheSameVersionOrAnOlderOneIsUpToDate(string tag)
    {
        var (service, _) = Service(_ => Json(ReleaseList(tag)));

        var (state, info) = await Check(service);

        Assert.Equal(UpdateCheckState.UpToDate, state);
        Assert.Null(info);
    }

    [Fact]
    public async Task AnEmptyReleaseListIsUpToDate()
    {
        // GitHub did answer, and said there is nothing: that is news, and it is good news.
        var (service, _) = Service(_ => Json("[]"));

        var (state, _) = await Check(service);

        Assert.Equal(UpdateCheckState.UpToDate, state);
    }

    // --- Could not check ---------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]           // the rate limit, or a block
    [InlineData(HttpStatusCode.TooManyRequests)]     // the rate limit, the other way it is said
    [InlineData(HttpStatusCode.InternalServerError)] // an outage
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AnErrorStatusIsACheckThatFailedNotUpToDate(HttpStatusCode status)
    {
        // Even when the error carries a body shaped like a release list: the status is what counts.
        var (service, _) = Service(_ => Json(ReleaseList("v1.23.0"), status));

        var (state, info) = await Check(service);

        Assert.Equal(UpdateCheckState.Failed, state);
        Assert.Null(info);
    }

    [Fact]
    public async Task TheRateLimitMessageIsNotTakenForAReleaseList()
    {
        // What GitHub sends with a 403 for an exhausted quota: a JSON object with a message.
        var (service, _) = Service(_ => Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden));

        var (state, _) = await Check(service);

        Assert.Equal(UpdateCheckState.Failed, state);
    }

    [Fact]
    public async Task NoConnectionIsACheckThatFailed()
    {
        var (service, _) = Service(_ => throw new HttpRequestException("No such host is known."));

        var (state, info) = await Check(service);

        Assert.Equal(UpdateCheckState.Failed, state);
        Assert.Null(info);
    }

    [Theory]
    [InlineData("""{"message":"not a list"}""")]  // a 200 whose body is an object
    [InlineData("[{ broken")]                      // JSON that stops half way
    [InlineData("<html>a captive portal</html>")]  // not JSON at all
    [InlineData("")]
    public async Task AnAnswerThatIsNotAReleaseListIsACheckThatFailed(string body)
    {
        var (service, _) = Service(_ => Json(body));

        var (state, info) = await Check(service);

        Assert.Equal(UpdateCheckState.Failed, state);
        Assert.Null(info);
    }

    [Fact]
    public async Task TheServiceItselfThrowsRatherThanAnswerNull()
    {
        // The contract RunAsync leans on: null means "asked, nothing newer", and only that.
        var (service, _) = Service(_ => Json("{}", HttpStatusCode.TooManyRequests));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(Running));
    }
}
