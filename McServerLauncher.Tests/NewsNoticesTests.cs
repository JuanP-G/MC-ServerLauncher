using System.IO;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What the board does with the notices of the signed file: which are listed and in what order, what
/// is unread, which one the strip is for and which are owed a notification.
/// </summary>
public class NewsNoticesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly NewsContext Machine = new("windows", new Version(1, 22, 0), true, "es");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-notices-" + Path.GetRandomFileName());

    public NewsNoticesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* a temporary folder is not worth failing a test over */ }
    }

    private string StateFile => Path.Combine(_dir, "news-state.json");

    private static NewsItem Item(string id, NewsKind kind = NewsKind.Incident, NewsStatus status = NewsStatus.Active,
        TimeSpan? startedAgo = null, TimeSpan? endsIn = null, DateTimeOffset? resolvedAt = null,
        NewsAudience? only = null)
    {
        var from = Now - (startedAgo ?? TimeSpan.FromHours(2));
        DateTimeOffset? until = kind == NewsKind.Tip && endsIn is null ? null : Now + (endsIn ?? TimeSpan.FromDays(2));
        if (status == NewsStatus.Resolved) resolvedAt ??= Now - TimeSpan.FromHours(1);

        return new NewsItem(id, kind, status, from, until, kind == NewsKind.Tip ? null : resolvedAt,
            only ?? NewsAudience.Everyone, null,
            new Dictionary<string, NewsText>
            {
                ["es"] = new("Aviso " + id, "Cuerpo"),
                ["en"] = new("Notice " + id, "Body"),
            });
    }

    private static NewsNoticeInput Input(IEnumerable<NewsItem> items, NewsContext? context = null,
        bool incidents = true, bool tips = true, Func<string, bool>? closed = null, Func<string, bool>? toasted = null,
        DateTimeOffset? now = null) =>
        new(items.ToList(), context ?? Machine, now ?? Now, incidents, tips, closed ?? (_ => false), toasted ?? (_ => false));

    private static NewsBoardModel Board(NewsNoticeInput? notices, Func<string, bool>? isRead = null,
        string? offered = null) =>
        NewsBoard.Build(Array.Empty<Changelog.Release>(), null, offered, false, isRead ?? (_ => false), notices);

    // ---- what is listed ----

    [Fact]
    public void WithNoNoticesGivenTheBoardHasNone()
    {
        var board = Board(null);

        Assert.Empty(board.Notices);
        Assert.Null(board.Band);
        Assert.Empty(board.Toasts);
        Assert.Equal(0, board.Unread);
    }

    [Fact]
    public void WhatIsGoingOnComesFirstThenWhatEnded()
    {
        var board = Board(Input([
            Item("ended", status: NewsStatus.Resolved),
            Item("tip", NewsKind.Tip, startedAgo: TimeSpan.FromHours(1)),
            Item("old-incident", startedAgo: TimeSpan.FromHours(5)),
            Item("new-incident", startedAgo: TimeSpan.FromHours(1)),
        ]));

        // The urgent ones before the tip, and newest first among them.
        Assert.Equal(["new-incident", "old-incident", "tip", "ended"], board.Notices.Select(n => n.Item.Id));
    }

    [Fact]
    public void ANoticeIsListedOnlyWithinItsDates()
    {
        var board = Board(Input([
            Item("future", startedAgo: TimeSpan.FromHours(-1)),                    // starts in an hour
            Item("expired", endsIn: TimeSpan.FromSeconds(-1)),
            Item("now"),
        ]));

        Assert.Equal(["now"], board.Notices.Select(n => n.Item.Id));
    }

    [Fact]
    public void AResolvedIncidentIsRetiredAWeekLater()
    {
        var resolved = Item("done", status: NewsStatus.Resolved, resolvedAt: Now - TimeSpan.FromDays(6));
        var gone = Item("older", status: NewsStatus.Resolved, resolvedAt: Now - TimeSpan.FromDays(8));

        var board = Board(Input([resolved, gone]));

        Assert.Equal(["done"], board.Notices.Select(n => n.Item.Id));
        Assert.True(board.Notices[0].Resolved);
    }

    [Fact]
    public void ANoticeForTunnelUsersIsHiddenFromEveryoneElse()
    {
        var item = Item("playit", only: new NewsAudience(true, [], null, null));

        Assert.Single(Board(Input([item], Machine with { UsesTunnels = true })).Notices);
        Assert.Empty(Board(Input([item], Machine with { UsesTunnels = false })).Notices);
    }

    [Fact]
    public void ANoticeForOneSystemOrVersionRangeIsHiddenFromOthers()
    {
        var item = Item("mac", only: new NewsAudience(null, ["macos"], new Version(1, 22, 0), null));

        Assert.Empty(Board(Input([item], Machine)).Notices);                                      // windows
        Assert.Single(Board(Input([item], Machine with { Os = "macos" })).Notices);
        Assert.Empty(Board(Input([item], Machine with { Os = "macos", AppVersion = new Version(1, 21, 9) })).Notices);
    }

    [Fact]
    public void IncidentsCanBeSwitchedOffButSecurityNoticesCannot()
    {
        var items = new[] { Item("incident"), Item("security", NewsKind.Security), Item("tip", NewsKind.Tip) };

        var board = Board(Input(items, incidents: false));

        Assert.Equal(["security", "tip"], board.Notices.Select(n => n.Item.Id).Order());
    }

    [Fact]
    public void TipsCanBeSwitchedOff()
    {
        var items = new[] { Item("incident"), Item("tip", NewsKind.Tip) };

        Assert.Equal(["incident"], Board(Input(items, tips: false)).Notices.Select(n => n.Item.Id));
    }

    [Fact]
    public void TheWordsAreInTheLanguageOfTheAppOrEnglish()
    {
        var item = Item("x");

        Assert.Equal("Aviso x", Board(Input([item], Machine with { Language = "es" })).Notices[0].Text.Title);
        Assert.Equal("Notice x", Board(Input([item], Machine with { Language = "pt" })).Notices[0].Text.Title);
        Assert.Equal("Notice x", Board(Input([item], Machine with { Language = "ja" })).Notices[0].Text.Title);
    }

    // ---- unread ----

    [Fact]
    public void ANoticeIsUnreadUntilItsKeyIsRead()
    {
        var item = Item("x");

        var fresh = Board(Input([item]));
        Assert.True(fresh.Notices[0].Unread);
        Assert.Equal(["x:active"], fresh.UnreadKeys);

        var read = Board(Input([item]), isRead: key => key == "x:active");
        Assert.False(read.Notices[0].Unread);
        Assert.Equal(0, read.Unread);
    }

    [Fact]
    public void AnIncidentThatIsResolvedIsUnreadAgain()
    {
        // Read while it was going on; the key carries the status, so its end is something new.
        bool IsRead(string key) => key == "playit:active";

        Assert.Equal(0, Board(Input([Item("playit")]), IsRead).Unread);
        Assert.Equal(["playit:resolved"],
            Board(Input([Item("playit", status: NewsStatus.Resolved)]), IsRead).UnreadKeys);
    }

    [Fact]
    public void TheNoticesAndTheUpdateOnOfferCountTogether()
    {
        var board = Board(Input([Item("a"), Item("b", NewsKind.Tip)]), offered: "1.23.0");

        Assert.Equal(3, board.Unread);
        Assert.Contains("update:1.23.0", board.UnreadKeys);
    }

    [Fact]
    public void ANoticeThatIsNotListedNeverCountsAsUnread()
    {
        var board = Board(Input([Item("later", startedAgo: TimeSpan.FromHours(-3))]));

        Assert.Equal(0, board.Unread);
    }

    // ---- the strip ----

    [Fact]
    public void TheStripIsForTheNewestUrgentNoticeStillGoingOn()
    {
        var board = Board(Input([
            Item("tip", NewsKind.Tip),
            Item("old", startedAgo: TimeSpan.FromHours(6)),
            Item("new", NewsKind.Security, startedAgo: TimeSpan.FromHours(1)),
            Item("ended", status: NewsStatus.Resolved),
        ]));

        Assert.Equal("new", board.Band!.Item.Id);
    }

    [Fact]
    public void ATipAndAResolvedIncidentNeverGetAStrip()
    {
        var board = Board(Input([Item("tip", NewsKind.Tip), Item("ended", status: NewsStatus.Resolved)]));

        Assert.Null(board.Band);
    }

    [Fact]
    public void ClosingTheStripLeavesTheNoticeListedAndPassesTheStripToTheNext()
    {
        var items = new[] { Item("new", startedAgo: TimeSpan.FromHours(1)), Item("old", startedAgo: TimeSpan.FromHours(6)) };

        var board = Board(Input(items, closed: id => id == "new"));

        Assert.Equal("old", board.Band!.Item.Id);
        Assert.Equal(2, board.Notices.Count);

        Assert.Null(Board(Input(items, closed: _ => true)).Band);
    }

    [Fact]
    public void TheStripGoesWhenTheIncidentIsResolved()
    {
        var active = Board(Input([Item("playit")]));
        var resolved = Board(Input([Item("playit", status: NewsStatus.Resolved)]));

        Assert.NotNull(active.Band);
        Assert.Null(resolved.Band);
        Assert.Single(resolved.Notices);                    // still listed, in green, for a week
    }

    [Fact]
    public void TheStripGoesWhenTheNoticeExpires()
    {
        var item = Item("playit", endsIn: TimeSpan.FromMinutes(5));

        Assert.NotNull(Board(Input([item])).Band);
        Assert.Null(Board(Input([item], now: Now + TimeSpan.FromMinutes(6))).Band);
    }

    // ---- notifications ----

    [Fact]
    public void AnUrgentNoticeNobodyWasToldAboutIsAnnounced()
    {
        var board = Board(Input([Item("a"), Item("b", NewsKind.Security)]));

        Assert.Equal(["a", "b"], board.Toasts.Select(t => t.Item.Id).Order());
    }

    [Fact]
    public void ATipIsNeverAnnounced()
    {
        Assert.Empty(Board(Input([Item("tip", NewsKind.Tip)])).Toasts);
    }

    [Fact]
    public void ANoticeIsAnnouncedOnceAndOnlyOnce()
    {
        var announced = Board(Input([Item("a")], toasted: key => key == "a:active"));

        Assert.Empty(announced.Toasts);
    }

    [Fact]
    public void ANoticeWhoseStripWasClosedIsNotAnnouncedAfterwards()
    {
        Assert.Empty(Board(Input([Item("a")], closed: id => id == "a")).Toasts);
    }

    [Fact]
    public void TheEndOfAnIncidentTheUserWasToldAboutIsAnnounced()
    {
        var board = Board(Input([Item("a", status: NewsStatus.Resolved)], toasted: key => key == "a:active"));

        Assert.Equal("a:resolved", Assert.Single(board.Toasts).Key);
    }

    [Fact]
    public void TheEndOfAnIncidentTheUserWasNeverToldAboutIsNot()
    {
        Assert.Empty(Board(Input([Item("a", status: NewsStatus.Resolved)])).Toasts);
    }

    [Fact]
    public void TheEndOfAnIncidentIsAnnouncedOnlyOnce()
    {
        var board = Board(Input([Item("a", status: NewsStatus.Resolved)],
            toasted: key => key is "a:active" or "a:resolved"));

        Assert.Empty(board.Toasts);
    }

    [Fact]
    public void AClosedStripDoesNotSilenceTheEndOfTheIncident()
    {
        var board = Board(Input([Item("a", status: NewsStatus.Resolved)],
            closed: _ => true, toasted: key => key == "a:active"));

        Assert.Single(board.Toasts);
    }

    [Fact]
    public void AHiddenKindIsNotAnnounced()
    {
        // Switched off in Settings: not listed, so not announced either.
        Assert.Empty(Board(Input([Item("a")], incidents: false)).Toasts);
    }

    // ---- what is remembered ----

    [Fact]
    public void AClosedStripSurvivesARestart()
    {
        new NewsState(StateFile).Close("playit");

        Assert.True(new NewsState(StateFile).IsClosed("playit"));
        Assert.False(new NewsState(StateFile).IsClosed("other"));
    }

    [Fact]
    public void AnAnnouncedNoticeSurvivesARestart()
    {
        new NewsState(StateFile).MarkToasted("playit:active");

        Assert.True(new NewsState(StateFile).WasToasted("playit:active"));
        Assert.False(new NewsState(StateFile).WasToasted("playit:resolved"));
    }

    [Fact]
    public void ClosingTwiceOrAnnouncingTwiceChangesNothing()
    {
        var state = new NewsState(StateFile);

        Assert.True(state.Close("a"));
        Assert.False(state.Close("a"));
        Assert.True(state.MarkToasted("a:active"));
        Assert.False(state.MarkToasted("a:active"));
        Assert.False(state.Close(" "));
    }

    [Fact]
    public void TheThreeListsAreIndependent()
    {
        var state = new NewsState(StateFile);
        state.MarkRead(["same"]);
        state.Close("other");

        var again = new NewsState(StateFile);
        Assert.True(again.IsRead("same"));
        Assert.False(again.IsClosed("same"));
        Assert.False(again.WasToasted("same"));
        Assert.True(again.IsClosed("other"));
        Assert.False(again.IsRead("other"));
    }

    [Fact]
    public void TheListsAreCappedAndForgetTheOldestFirst()
    {
        var state = new NewsState(StateFile);
        for (var i = 0; i < NewsState.MaxClosed + 5; i++) state.Close("id" + i);

        var again = new NewsState(StateFile);
        Assert.False(again.IsClosed("id0"));
        Assert.True(again.IsClosed("id" + (NewsState.MaxClosed + 4)));
    }

    [Fact]
    public void AFileFromBeforeTheNoticesStillLoads()
    {
        // The file PR 1 wrote has only "Read". Whoever updates keeps what they had read.
        File.WriteAllText(StateFile, "{\"Read\":[\"update:1.22.0\"]}");

        var state = new NewsState(StateFile);

        Assert.True(state.IsRead("update:1.22.0"));
        Assert.False(state.IsClosed("anything"));
        Assert.False(state.WasToasted("anything"));
    }
}
