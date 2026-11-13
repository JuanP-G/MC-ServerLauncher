using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The signed notices file as the window uses it: when it is looked at, who it is read for, the strip and
/// the notification an urgent notice raises.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="MainViewModel"/> over a temporary data folder and a notices service over a fake
/// handler, with <c>StartNews()</c> standing in for <c>Activate()</c> (which also asks GitHub, starts the
/// Playit agent and opens sockets). Nothing here reaches the network, and the toast is a list: no test
/// opens a window.
/// </para>
/// <para>
/// What each case holds is a way the pieces could each be right and the whole wrong: a request made
/// before the window is up or with announcements off, a notice shown to somebody it is not for, a
/// failed look read as "no notices", a strip that does not go where the user put it, or a notification
/// that comes twice.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class NewsFeedWiringTests : IDisposable
{
    private const string Url = "https://notices.test/news.json";

    private readonly AvaloniaFixture _ui;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mcl-newswire-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeHandler _handler = new();
    private readonly List<(string Heading, string Message, Icon Icon, NotificationLevel Level)> _toasts = [];
    private bool _windowInactive = true;

    /// <summary>What the notices service takes for "now"; null is the real clock.</summary>
    private DateTimeOffset? _clockOverride;

    public NewsFeedWiringTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch { /* a temporary folder is not worth failing a test over */ }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var url = request.RequestUri!.ToString();
            return Task.FromResult(Routes.TryGetValue(url, out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // ---- the file ----

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    private static string Notice(string id = "playit-1", string kind = "incident", string status = "active",
        string only = "")
    {
        var now = DateTimeOffset.UtcNow;
        var ended = status == "resolved" ? $"\"resolvedAt\":\"{Iso(now - TimeSpan.FromMinutes(5))}\"," : "";
        return "{\"id\":\"" + id + "\",\"kind\":\"" + kind + "\",\"status\":\"" + status + "\"," + ended +
               "\"from\":\"" + Iso(now - TimeSpan.FromHours(1)) + "\",\"until\":\"" + Iso(now + TimeSpan.FromDays(2)) + "\"," +
               only + "\"text\":{\"en\":{\"title\":\"Playit is down\",\"body\":\"Tunnels do not connect\"}}}";
    }

    private static byte[] Feed(int seq, params string[] items) =>
        FeedExpiring(seq, DateTimeOffset.UtcNow + TimeSpan.FromDays(30), items);

    /// <summary>A file that expires at <paramref name="expires"/>; issued a minute ago, or a week before it when that is already past.</summary>
    private static byte[] FeedExpiring(int seq, DateTimeOffset expires, params string[] items)
    {
        var now = DateTimeOffset.UtcNow;
        var issued = expires > now ? now - TimeSpan.FromMinutes(1) : expires - TimeSpan.FromDays(7);
        return Encoding.UTF8.GetBytes(
            "{\"schema\":1,\"seq\":" + seq + ",\"issued\":\"" + Iso(issued) +
            "\",\"expires\":\"" + Iso(expires) + "\",\"items\":[" + string.Join(",", items) + "]}\n");
    }

    private string Sign(byte[] raw) => Convert.ToBase64String(_key.SignData(
        UpdateSignature.NewsMessage(Convert.ToHexString(SHA256.HashData(raw))),
        HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n";

    /// <summary>Makes the server answer with <paramref name="raw"/> (signed) from now on.</summary>
    private void Serve(byte[] raw, string etag)
    {
        _handler.Routes[Url] = request => request.Headers.IfNoneMatch.Any(t => t.Tag == etag)
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : WithEtag(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) }, etag);
        var signature = Encoding.UTF8.GetBytes(Sign(raw));
        _handler.Routes[Url + ".sig"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(signature) };
    }

    private static HttpResponseMessage WithEtag(HttpResponseMessage response, string etag)
    {
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    // ---- the window ----

    private MainViewModel Open(bool usesTunnels = true)
    {
        MainViewModel? main = null;
        _ui.Run(() =>
        {
            var service = new NewsFeedService(Path.Combine(_dataDir, "cache"), new HttpClient(_handler), [Url],
                [Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())],
                clock: () => _clockOverride ?? DateTimeOffset.UtcNow);
            main = new MainViewModel(_dataDir, service)
            {
                UsesTunnelsOverride = () => usesTunnels,
                WindowInactive = () => _windowInactive,
                ToastSink = (heading, _, message, icon, level) => _toasts.Add((heading, message, icon, level)),
            };
        });
        return main!;
    }

    private T Read<T>(Func<T> read)
    {
        T value = default!;
        _ui.Run(() => value = read());
        return value;
    }

    private async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 250; i++)
        {
            if (Read(condition)) return;
            await Task.Delay(20);
        }
        Assert.Fail("The window did not reach the expected state in time.");
    }

    // ---- when it is looked at ----

    [Fact]
    public async Task NothingIsAskedBeforeTheWindowIsUp()
    {
        Serve(Feed(1, Notice()), "\"v1\"");

        var main = Open();
        await Task.Delay(150);

        Assert.Equal(0, _handler.Requests);
        Assert.Empty(Read(() => main.News.Notices));
    }

    [Fact]
    public async Task WithAnnouncementsOffNothingIsAskedEvenOnceTheWindowIsUp()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(() => main.Settings.ShowAnnouncements = false);

        _ui.Run(main.StartNews);
        _ui.Run(() => _ = main.CheckNewsAsync());     // even a look somebody asks for is no look
        await Task.Delay(150);

        Assert.Equal(0, _handler.Requests);
        Assert.False(Read(() => main.ShowNewsBand));
    }

    [Fact]
    public async Task TurningAnnouncementsOffHidesTheStripAndStopsTheLooking()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(main.StartNews);
        await Until(() => main.ShowNewsBand);
        var asked = _handler.Requests;

        _ui.Run(() => main.Settings.ShowAnnouncements = false);
        _ui.Run(() => _ = main.CheckNewsAsync());
        await Task.Delay(150);

        Assert.False(Read(() => main.ShowNewsBand));
        Assert.Equal(asked, _handler.Requests);
    }

    // ---- what it shows ----

    [Fact]
    public async Task AGoodFileShowsItsNoticesAndTheStrip()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();

        _ui.Run(main.StartNews);
        await Until(() => main.News.Notices.Count == 1);

        Assert.Equal("Playit is down", Read(() => main.News.Notices[0].Title));
        Assert.True(Read(() => main.ShowNewsBand));
        Assert.Equal("Playit is down", Read(() => main.News.BandTitle));
        Assert.False(Read(() => main.News.HasFeedProblem));
        Assert.False(Read(() => main.News.NoNotices));
    }

    [Fact]
    public async Task ANoticeForWhoeverUsesTunnelsIsNotShownToWhoeverDoesNot()
    {
        Serve(Feed(1, Notice(only: "\"only\":{\"usesTunnels\":true},")), "\"v1\"");
        var main = Open(usesTunnels: false);

        _ui.Run(main.StartNews);
        await Until(() => _handler.Requests >= 2 && main.News.NoNotices);

        Assert.Empty(Read(() => main.News.Notices));
        Assert.False(Read(() => main.ShowNewsBand));
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task AFailedLookIsSaidAndNeverShownAsNoNotices()
    {
        _handler.Routes[Url] = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var main = Open();

        _ui.Run(main.StartNews);
        await Until(() => main.News.HasFeedProblem);

        Assert.False(Read(() => main.News.NoNotices));
        Assert.False(string.IsNullOrWhiteSpace(Read(() => main.News.FeedProblemText)));
    }

    [Fact]
    public async Task AFileThatHasExpiredIsSaidAsOutOfDateWithItsOwnWordsAndNeverShownAsNoNotices()
    {
        Serve(FeedExpiring(1, DateTimeOffset.UtcNow - TimeSpan.FromDays(1), Notice()), "\"v1\"");
        var main = Open();

        _ui.Run(main.StartNews);
        await Until(() => main.News.HasFeedProblem);

        Assert.False(Read(() => main.News.NoNotices));
        Assert.Equal(Localizer.Get("News_FeedExpiredNoCopy"), Read(() => main.News.FeedProblemText));
        Assert.NotEqual(Localizer.Get("News_FeedFailedNoCopy"), Read(() => main.News.FeedProblemText));
    }

    [Fact]
    public async Task ACopyThatExpiresWhileTheWindowIsOpenStartsSayingSo()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(main.StartNews);
        await Until(() => main.News.Notices.Count == 1);
        Assert.False(Read(() => main.News.HasFeedProblem));

        // A month later the server still answers "nothing newer": true, and no use.
        _clockOverride = DateTimeOffset.UtcNow + TimeSpan.FromDays(31);
        _ui.Run(() => _ = main.CheckNewsAsync());
        await Until(() => main.News.HasFeedProblem);

        // The expired wording with the time of the last confirmation filled in: its fixed beginning.
        Assert.StartsWith(Localizer.Get("News_FeedExpiredFmt").Split("{0}")[0], Read(() => main.News.FeedProblemText));
        Assert.False(Read(() => main.News.NoNotices));
    }

    [Fact]
    public async Task ASiteWithNoFileYetIsJustEmpty()
    {
        var main = Open();     // every address answers 404: nothing has been published

        _ui.Run(main.StartNews);
        await Until(() => main.News.NoNotices && _handler.Requests >= 1);

        Assert.False(Read(() => main.News.HasFeedProblem));
    }

    // ---- the strip ----

    [Fact]
    public async Task ClosingTheStripKeepsTheNoticeListedAndIsRemembered()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(main.StartNews);
        await Until(() => main.ShowNewsBand);

        _ui.Run(() => main.News.CloseBandCommand.Execute(null));

        Assert.False(Read(() => main.ShowNewsBand));
        Assert.Single(Read(() => main.News.Notices));
        Assert.True(new NewsState(Path.Combine(_dataDir, "news-state.json")).IsClosed("playit-1"));
    }

    [Fact]
    public async Task OpeningTheSectionReadsTheNoticeAndTheStripGoes()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(main.StartNews);
        await Until(() => main.ShowNewsBand);

        _ui.Run(() => main.ShowNewsCommand.Execute(null));

        Assert.False(Read(() => main.ShowNewsBand));
        Assert.Single(Read(() => main.News.Notices));
        Assert.True(Read(() => main.News.Notices[0].Marked));     // still marked, for this visit
        Assert.Equal(0, Read(() => main.News.UnreadCount));
    }

    [Fact]
    public async Task ANoticeThatComesWhileTheSectionIsOpenGetsNoStrip()
    {
        var main = Open();                                         // nothing published yet: 404
        _ui.Run(main.StartNews);
        await Until(() => main.News.NoNotices && _handler.Requests >= 1);
        _ui.Run(() => main.ShowNewsCommand.Execute(null));

        Serve(Feed(1, Notice()), "\"v1\"");
        _ui.Run(() => _ = main.CheckNewsAsync());
        await Until(() => main.News.Notices.Count == 1);

        Assert.False(Read(() => main.ShowNewsBand));               // the user is looking at it already
    }

    // ---- when the window comes back ----

    [Fact]
    public async Task ComingBackToTheWindowLooksAgainOnlyIfTheCopyIsNotRecent()
    {
        var start = DateTimeOffset.UtcNow;
        _clockOverride = start;
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(main.StartNews);
        await Until(() => main.News.Notices.Count == 1);
        var asked = _handler.Requests;

        _clockOverride = start + TimeSpan.FromSeconds(90);
        _ui.Run(main.OnWindowActivated);
        await Task.Delay(150);
        Assert.Equal(asked, _handler.Requests);                    // confirmed a minute and a half ago: enough

        _clockOverride = start + TimeSpan.FromMinutes(3);
        _ui.Run(main.OnWindowActivated);
        await Until(() => _handler.Requests > asked);              // three minutes: look again
    }

    [Fact]
    public async Task ComingBackAsksNothingBeforeTheWindowIsUpOrWithAnnouncementsOff()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();

        _ui.Run(main.OnWindowActivated);                           // nothing has started yet
        _ui.Run(() => main.Settings.ShowAnnouncements = false);
        _ui.Run(main.StartNews);
        _ui.Run(main.OnWindowActivated);
        await Task.Delay(150);

        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public void TheTimerSkipsALookRightAfterAnotherButNeverAlwaysSkips()
    {
        // The timer ticks every five minutes with an age a little under that, so a look a minute before
        // covers it and an idle window never does; a window coming back uses the shorter, fresher age.
        Assert.True(MainViewModel.NewsTickAge < TimeSpan.FromMinutes(5));
        Assert.True(MainViewModel.NewsFreshAge <= MainViewModel.NewsTickAge);
    }

    [Fact]
    public async Task TheStripOnlyShowsWhereTheUserChose()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(main.StartNews);
        await Until(() => main.News.HasBand);

        Assert.True(Read(() => main.ShowNewsBand));      // everywhere, the default

        _ui.Run(() => main.Settings.SelectedStripOption = main.Settings.StripOptions[1]);
        Assert.False(Read(() => main.ShowNewsBand));     // only in Tunnels, and this is Servers
        _ui.Run(() => main.Section = AppSection.Tunnels);
        Assert.True(Read(() => main.ShowNewsBand));

        _ui.Run(() => main.Settings.SelectedStripOption = main.Settings.StripOptions[2]);
        Assert.False(Read(() => main.ShowNewsBand));     // none
    }

    // ---- the notification ----

    [Fact]
    public async Task AnUrgentNoticeIsAnnouncedOnceWhenTheWindowIsOutOfSight()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();

        _ui.Run(main.StartNews);
        await Until(() => _toasts.Count >= 1);
        _ui.Run(() => _ = main.CheckNewsAsync());                      // looking again changes nothing
        _ui.Run(() => main.Settings.SelectedBadgeOption = main.Settings.BadgeOptions[1]);   // nor does a setting
        await Task.Delay(150);

        var toast = Assert.Single(_toasts);
        Assert.Equal("Playit is down", toast.Heading);
        Assert.Equal("Tunnels do not connect", toast.Message);
        Assert.Equal(Icon.Warning, toast.Icon);
        Assert.Equal(NotificationLevel.Warning, toast.Level);
    }

    [Fact]
    public async Task ASecurityNoticeIsAnErrorWithItsOwnIcon()
    {
        Serve(Feed(1, Notice("fix-1", "security")), "\"v1\"");
        var main = Open();

        _ui.Run(main.StartNews);
        await Until(() => _toasts.Count >= 1);

        var toast = Assert.Single(_toasts);
        Assert.Equal(Icon.ShieldError, toast.Icon);
        Assert.Equal(NotificationLevel.Error, toast.Level);
    }

    [Fact]
    public async Task NoToastWhenTheStripIsInFrontOfTheUser()
    {
        _windowInactive = false;
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();

        _ui.Run(main.StartNews);
        await Until(() => main.ShowNewsBand);
        await Task.Delay(100);

        Assert.Empty(_toasts);
        // Said once in the strip is said: it does not turn up later when the window goes out of sight.
        _windowInactive = true;
        _ui.Run(() => _ = main.CheckNewsAsync());
        await Task.Delay(100);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task WithTheStripOffTheNoticeStillSpeaksUpWhileTheWindowIsInFront()
    {
        _windowInactive = false;
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(() => main.Settings.SelectedStripOption = main.Settings.StripOptions[2]);

        _ui.Run(main.StartNews);
        await Until(() => _toasts.Count >= 1);

        Assert.Single(_toasts);
        Assert.False(Read(() => main.ShowNewsBand));
    }

    [Fact]
    public async Task NoToastWhenTheUserTurnedThemOff()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(() => main.Settings.NewsToast = false);

        _ui.Run(main.StartNews);
        await Until(() => main.News.Notices.Count == 1);
        await Task.Delay(100);

        Assert.Empty(_toasts);
        // And turning them on later does not bring that one back: it was handled.
        _ui.Run(() => main.Settings.NewsToast = true);
        await Task.Delay(100);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task AnIncidentThatEndsAfterTheUserWasToldIsAnnouncedAsResolvedOnce()
    {
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(main.StartNews);
        await Until(() => _toasts.Count >= 1);

        Serve(Feed(2, Notice(status: "resolved")), "\"v2\"");
        _ui.Run(() => _ = main.CheckNewsAsync());
        await Until(() => _toasts.Count >= 2);
        _ui.Run(() => _ = main.CheckNewsAsync());
        await Task.Delay(150);

        Assert.Equal(2, _toasts.Count);
        Assert.Equal(Icon.CheckmarkCircle, _toasts[1].Icon);
        Assert.Equal(NotificationLevel.Success, _toasts[1].Level);
        Assert.False(Read(() => main.ShowNewsBand));     // the strip went with the incident
    }

    [Fact]
    public async Task AnIncidentThatAppearsBecauseASettingMovedIsAnnouncedExactlyOnce()
    {
        // Incidents off: the file is read, the notice is not listed. Turning incidents on lists it and
        // announces it in the same breath; two passes over the same list used to announce it twice.
        Serve(Feed(1, Notice()), "\"v1\"");
        var main = Open();
        _ui.Run(() => main.Settings.NewsIncidents = false);
        _ui.Run(main.StartNews);
        await Until(() => _handler.Requests >= 2);     // the file and its signature
        await Task.Delay(100);
        Assert.Empty(_toasts);
        Assert.Empty(Read(() => main.News.Notices));

        _ui.Run(() => main.Settings.NewsIncidents = true);
        await Until(() => _toasts.Count >= 1);
        await Task.Delay(150);

        Assert.Single(_toasts);
        Assert.Single(Read(() => main.News.Notices));
    }

    [Theory]
    [InlineData("es", "es")]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("fr", "fr")]
    [InlineData("pt", "pt")]
    [InlineData("pt-BR", "pt")]
    [InlineData("en-GB", "en")]
    [InlineData("it", "es")]       // no translation of the app: the window is Spanish, so the notices are
    [InlineData("ja-JP", "es")]
    [InlineData("", "es")]         // the invariant culture
    public void NoticesAreReadInTheLanguageTheWindowIsIn(string culture, string expected)
    {
        Assert.Equal(expected, MainViewModel.NewsLanguageFor(System.Globalization.CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public async Task ATipNeverSpeaksUp()
    {
        Serve(Feed(1, Notice("tip-1", "tip")), "\"v1\"");
        var main = Open();

        _ui.Run(main.StartNews);
        await Until(() => main.News.Notices.Count == 1);
        await Task.Delay(100);

        Assert.Empty(_toasts);
        Assert.False(Read(() => main.ShowNewsBand));
    }
}
