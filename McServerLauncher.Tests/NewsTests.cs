using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>What Novedades remembers between runs.</summary>
public class NewsStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-news-" + Guid.NewGuid().ToString("N"));

    private string StateFile => Path.Combine(_dir, "news-state.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    [Fact]
    public void NothingIsReadAtFirst()
    {
        var state = new NewsState(StateFile);

        Assert.False(state.IsRead("update:9.9.9"));
        Assert.False(File.Exists(StateFile));   // looking is not writing
    }

    [Fact]
    public void WhatIsMarkedReadStaysReadAfterARestart()
    {
        new NewsState(StateFile).MarkRead(new[] { "update:1.23.0", "incident:2026-11-07:active" });

        var again = new NewsState(StateFile);

        Assert.True(again.IsRead("update:1.23.0"));
        Assert.True(again.IsRead("incident:2026-11-07:active"));
        Assert.False(again.IsRead("update:1.24.0"));
    }

    [Fact]
    public void MarkingWhatIsAlreadyReadChangesNothing()
    {
        var state = new NewsState(StateFile);

        Assert.True(state.MarkRead(new[] { "a" }));
        Assert.False(state.MarkRead(new[] { "a", " ", "" }));
    }

    [Fact]
    public void ACorruptFileStartsEmptyInsteadOfFailing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StateFile, "{ this is not json");

        var state = new NewsState(StateFile);

        Assert.False(state.IsRead("anything"));
        Assert.True(state.MarkRead(new[] { "anything" }));   // and it can be written over
        Assert.True(new NewsState(StateFile).IsRead("anything"));
    }

    [Fact]
    public void AFolderThatCannotBeWrittenNeverTakesTheScreenDown()
    {
        // A file where the folder should be: creating the directory fails.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");

        var state = new NewsState(Path.Combine(blocker, "news-state.json"));

        Assert.True(state.MarkRead(new[] { "a" }));   // known for this run
        Assert.True(state.IsRead("a"));
    }

    [Fact]
    public void OnlyTheNewestKeysAreKept()
    {
        var state = new NewsState(StateFile);
        var keys = Enumerable.Range(0, NewsState.MaxKeys + 5).Select(i => $"k{i}").ToList();

        state.MarkRead(keys);

        Assert.False(state.IsRead("k0"));
        Assert.False(state.IsRead("k4"));
        Assert.True(state.IsRead("k5"));
        Assert.True(state.IsRead($"k{NewsState.MaxKeys + 4}"));

        var again = new NewsState(StateFile);
        Assert.False(again.IsRead("k4"));
        Assert.True(again.IsRead("k5"));
    }
}

/// <summary>What the Novedades screen lists and what counts as unread.</summary>
public class NewsBoardTests
{
    private static readonly Version Current = new(1, 22, 0);

    private static IReadOnlyList<Changelog.Release> Releases => Changelog.All(Current);

    private static NewsBoardModel Build(Version? lastSeen = null, string? offer = null, bool beta = false,
        Func<string, bool>? isRead = null) =>
        NewsBoard.Build(Releases, lastSeen, offer, beta, isRead ?? (_ => false));

    [Fact]
    public void EveryReleaseIsListedNewestFirst()
    {
        var board = Build();

        Assert.Equal(Releases.Count, board.Versions.Count);
        Assert.Equal(Current, board.Versions[0].Release.Version);
        Assert.Equal(board.Versions.Select(v => v.Release.Version).OrderByDescending(v => v),
            board.Versions.Select(v => v.Release.Version));
    }

    [Fact]
    public void WithNothingNewOnlyTheVersionRunningIsOpen()
    {
        var board = Build(lastSeen: Current);

        var open = Assert.Single(board.Versions, v => v.Open);
        Assert.Equal(NewsTag.Installed, open.Tag);
        Assert.Same(board.Versions[0], open);
        Assert.DoesNotContain(board.Versions, v => v.Tag == NewsTag.New);
    }

    [Fact]
    public void AnUpdateOpensEveryVersionItBrought()
    {
        // From 1.21.4 to 1.22.0: 1.21.5, 1.21.6 and 1.22.0 are the news.
        var board = Build(lastSeen: new Version(1, 21, 4));

        var news = board.Versions.Where(v => v.Tag == NewsTag.New).Select(v => v.Release.Label).ToList();
        Assert.Equal(new[] { "1.22.0", "1.21.6", "1.21.5" }, news);
        Assert.All(board.Versions, v => Assert.Equal(v.Tag == NewsTag.New, v.Open));
    }

    [Fact]
    public void ANewInstallHasNothingNew()
    {
        var board = Build(lastSeen: null);

        Assert.DoesNotContain(board.Versions, v => v.Tag == NewsTag.New);
        Assert.True(board.Versions[0].Open);
    }

    [Fact]
    public void ANewerVersionOnOfferIsUnreadUntilItIsRead()
    {
        var unread = Build(offer: "1.23.0");

        Assert.NotNull(unread.Offer);
        Assert.True(unread.Offer!.Unread);
        Assert.Equal(new[] { "update:1.23.0" }, unread.UnreadKeys);
        Assert.Equal(1, unread.Unread);

        var read = Build(offer: "1.23.0", isRead: key => key == "update:1.23.0");

        Assert.NotNull(read.Offer);      // still listed
        Assert.False(read.Offer!.Unread);
        Assert.Equal(0, read.Unread);
    }

    [Fact]
    public void WithNoOfferThereIsNothingToRead()
    {
        var board = Build();

        Assert.Null(board.Offer);
        Assert.Equal(0, board.Unread);
    }

    [Fact]
    public void ABetaIsSaidToBeOne()
    {
        Assert.True(Build(offer: "1.23.0-beta.1", beta: true).Offer!.IsBeta);
        Assert.False(Build(offer: "1.23.0").Offer!.IsBeta);
    }

    [Fact]
    public void EachOfferedVersionIsReadOnItsOwn()
    {
        // Reading 1.23.0 must not read 1.24.0 when it arrives.
        var board = Build(offer: "1.24.0", isRead: key => key == "update:1.23.0");

        Assert.True(board.Offer!.Unread);
    }
}

/// <summary>What the Novedades screen holds for itself and for the rail.</summary>
public class NewsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-newsvm-" + Guid.NewGuid().ToString("N"));

    private string StateFile => Path.Combine(_dir, "news-state.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private (NewsViewModel Vm, AppSettings Settings, List<string> Log) Make(AppSettings? settings = null,
        NewsState? state = null)
    {
        settings ??= new AppSettings { LastVersionSeen = "1.22.0" };
        var log = new List<string>();
        var vm = new NewsViewModel(settings, state ?? new NewsState(StateFile),
            new RelayCommand(() => log.Add("update")), () => log.Add("save"), () => log.Add("settings"),
            current: new Version(1, 22, 0));
        return (vm, settings, log);
    }

    [Fact]
    public void AnOfferMarksTheRailUntilTheSectionIsOpened()
    {
        var (vm, _, _) = Make();
        Assert.False(vm.HasUnread);

        vm.SetOffer("1.23.0", isBeta: false);

        Assert.True(vm.HasOffer);
        Assert.True(vm.HasUnread);
        Assert.Equal(1, vm.UnreadCount);
        Assert.Equal("1", vm.BadgeText);
        Assert.Equal(NewsBadgeStyle.Number, vm.BadgeStyle);
    }

    [Fact]
    public void OpeningTheSectionReadsItButKeepsTheMarkForTheVisit()
    {
        var (vm, _, _) = Make();
        vm.SetOffer("1.23.0", isBeta: false);

        vm.Opened();

        Assert.False(vm.HasUnread);        // the rail is clean at once
        Assert.True(vm.OfferMarked);       // the card is still easy to find
        Assert.True(vm.HasMarks);

        vm.Closed();

        Assert.False(vm.OfferMarked);      // and it is an ordinary card afterwards
        Assert.False(vm.HasMarks);
        Assert.True(vm.HasOffer);
    }

    [Fact]
    public void MarkAllAsReadClearsTheMarksWithoutLeaving()
    {
        var (vm, _, _) = Make();
        vm.SetOffer("1.23.0", isBeta: false);
        vm.Opened();

        vm.MarkAllReadCommand.Execute(null);

        Assert.False(vm.OfferMarked);
        Assert.False(vm.HasUnread);
    }

    [Fact]
    public void AnOfferThatArrivesWhileLookingIsReadAtOnce()
    {
        var (vm, _, _) = Make();
        vm.Opened();

        vm.SetOffer("1.23.0", isBeta: false);

        Assert.False(vm.HasUnread);
        Assert.True(vm.OfferMarked);
    }

    [Fact]
    public void WhatWasReadStaysReadAfterARestartAndANewerOfferIsUnread()
    {
        var first = Make().Vm;
        first.SetOffer("1.23.0", isBeta: false);
        first.Opened();

        var (again, _, _) = Make();
        again.SetOffer("1.23.0", isBeta: false);
        Assert.False(again.HasUnread);

        again.SetOffer("1.24.0", isBeta: false);
        Assert.True(again.HasUnread);
    }

    [Fact]
    public void WithTheAnnouncementsOffNothingMarksTheRailAndTheNoticesAreOff()
    {
        var (vm, _, _) = Make(new AppSettings { LastVersionSeen = "1.22.0", ShowAnnouncements = false });

        vm.SetOffer("1.23.0", isBeta: false);

        Assert.False(vm.HasUnread);
        Assert.Equal(0, vm.UnreadCount);
        Assert.Equal(NewsBadgeStyle.None, vm.BadgeStyle);
        Assert.False(vm.NoticesEnabled);
        Assert.True(vm.NoticesDisabled);
        Assert.True(vm.HasOffer);   // the version on offer is still told, in its card
    }

    [Fact]
    public void TheMarkFollowsTheStyleChosenAndTheScreenIsToldWhenItMoves()
    {
        var (vm, settings, _) = Make();
        vm.SetOffer("1.23.0", isBeta: false);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.NewsBadge = NewsBadgeStyle.Pulse;
        vm.OnSettingsChanged();

        Assert.Equal(NewsBadgeStyle.Pulse, vm.BadgeStyle);
        Assert.Contains(nameof(NewsViewModel.BadgeStyle), changed);
        Assert.Contains(nameof(NewsViewModel.HasUnread), changed);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(10, "9+")]
    [InlineData(250, "9+")]
    public void ThePillCapsBigNumbers(int count, string shown) =>
        Assert.Equal(shown, NewsViewModel.BadgeLabel(count));

    [Fact]
    public void TheCardAboutTheNewPlaceIsShownUntilItIsAnswered()
    {
        var (vm, settings, log) = Make();
        Assert.True(vm.ShowMoveNotice);

        vm.DismissMoveNoticeCommand.Execute(null);

        Assert.False(vm.ShowMoveNotice);
        Assert.True(settings.NewsMoveNoticeSeen);
        Assert.Equal(new[] { "save" }, log);

        vm.DismissMoveNoticeCommand.Execute(null);   // nothing more to write
        Assert.Equal(new[] { "save" }, log);
    }

    [Fact]
    public void GoingToSettingsFromTheCardAnswersItAndOpensThem()
    {
        var (vm, settings, log) = Make();

        vm.OpenNoticeSettingsCommand.Execute(null);

        Assert.True(settings.NewsMoveNoticeSeen);
        Assert.Equal(new[] { "save", "settings" }, log);
    }

    [Fact]
    public void TheOtherWayToSettingsLeavesTheCardAlone()
    {
        var (vm, settings, log) = Make();

        vm.OpenSettingsCommand.Execute(null);

        Assert.False(settings.NewsMoveNoticeSeen);
        Assert.Equal(new[] { "settings" }, log);
    }

    [Fact]
    public void OnlyTheVersionsThatAreOpenLookUpTheirNotes()
    {
        var (vm, _, _) = Make();

        Assert.False(string.IsNullOrWhiteSpace(vm.Versions[0].Notes));   // the running one starts open
        Assert.Equal(string.Empty, vm.Versions[1].Notes);

        vm.Versions[1].IsExpanded = true;
        Assert.False(string.IsNullOrWhiteSpace(vm.Versions[1].Notes));

        vm.Versions[1].IsExpanded = false;
        Assert.Equal(string.Empty, vm.Versions[1].Notes);
    }

    [Fact]
    public void WhatCameWithTheUpdateIsKnownBeforeTheStartUpStoresTheVersion()
    {
        var settings = new AppSettings { LastVersionSeen = "1.21.4" };
        var (vm, _, _) = Make(settings);

        settings.LastVersionSeen = "1.22.0";   // what MainViewModel.OpenNewsIfUpdated does next

        Assert.True(vm.HasFreshVersions);
        Assert.Equal(3, vm.Versions.Count(v => v.IsNew));
        Assert.All(vm.Versions.Where(v => v.IsNew), v => Assert.True(v.IsExpanded));
    }

    [Fact]
    public void ANewInstallHasNoFreshVersions()
    {
        var (vm, _, _) = Make(new AppSettings());

        Assert.False(vm.HasFreshVersions);
    }
}

/// <summary>The section in the real main view model: opening it, leaving it and the first start after an update.</summary>
[Collection("avalonia")]
public class NewsSectionTests : IDisposable
{
    private readonly AvaloniaFixture _ui;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-newssec-" + Guid.NewGuid().ToString("N"));

    public NewsSectionTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private static string RunningVersion => Changelog.Format(Changelog.Running());

    private void SaveSettings(AppSettings settings) => new AppSettingsService(_dir).Save(settings);

    private AppSettings LoadSettings() => new AppSettingsService(_dir).Load();

    [Fact]
    public void AfterAnUpdateTheSectionOpensOnWhatCameWithItAndTheVersionIsStored() =>
        _ui.Run(() =>
        {
            SaveSettings(new AppSettings { LastVersionSeen = "1.21.0" });
            var main = new MainViewModel(_dir);

            main.OpenNewsIfUpdated();

            Assert.Equal(AppSection.News, main.Section);
            Assert.True(main.IsNewsSection);
            Assert.True(main.News.ShowMoveNotice);          // they used to get a window
            Assert.Equal(RunningVersion, LoadSettings().LastVersionSeen);
        });

    [Fact]
    public void ItHappensOnceNotEveryTimeTheWindowComesBack() =>
        _ui.Run(() =>
        {
            SaveSettings(new AppSettings { LastVersionSeen = "1.21.0" });
            var main = new MainViewModel(_dir);
            main.OpenNewsIfUpdated();
            main.ShowServersCommand.Execute(null);

            main.OpenNewsIfUpdated();   // Loaded fires again when the window returns from the tray

            Assert.Equal(AppSection.Servers, main.Section);
        });

    [Fact]
    public void ANewInstallStaysOnTheServersAndHasNothingToBeToldAboutMoving() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dir);

            main.OpenNewsIfUpdated();

            Assert.Equal(AppSection.Servers, main.Section);
            Assert.False(main.News.ShowMoveNotice);
            var stored = LoadSettings();
            Assert.Equal(RunningVersion, stored.LastVersionSeen);
            Assert.True(stored.NewsMoveNoticeSeen);
        });

    [Fact]
    public void WithTheAnnouncementsOffAnUpdateDoesNotOpenTheSection() =>
        _ui.Run(() =>
        {
            SaveSettings(new AppSettings { LastVersionSeen = "1.21.0", ShowAnnouncements = false });
            var main = new MainViewModel(_dir);

            main.OpenNewsIfUpdated();

            Assert.Equal(AppSection.Servers, main.Section);
            Assert.Equal(RunningVersion, LoadSettings().LastVersionSeen);   // told once, not forever
        });

    [Fact]
    public void OpeningTheSectionReadsWhatWasUnreadAndLeavingItEndsTheVisit() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dir);
            main.News.SetOffer("9.9.9", isBeta: false);
            Assert.True(main.News.HasUnread);

            main.ShowNewsCommand.Execute(null);

            Assert.Equal(AppSection.News, main.Section);
            Assert.False(main.News.HasUnread);
            Assert.True(main.News.OfferMarked);
            Assert.True(File.Exists(Path.Combine(_dir, "news-state.json")));   // in the data folder, not the user's

            main.ShowServersCommand.Execute(null);

            Assert.False(main.News.OfferMarked);
        });

    [Fact]
    public void TheCardPointingAtSettingsOpensTheAnnouncementsPage() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dir);
            main.ShowNewsCommand.Execute(null);

            main.News.OpenSettingsCommand.Execute(null);

            Assert.Equal(AppSection.Settings, main.Section);
            Assert.True(main.Settings.IsNewsPage);
        });

    [Fact]
    public void TheSettingsScreenMovesTheMarkOnTheRail() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dir);
            main.News.SetOffer("9.9.9", isBeta: false);
            Assert.Equal(NewsBadgeStyle.Number, main.News.BadgeStyle);

            main.Settings.SelectedBadgeOption = main.Settings.BadgeOptions.Single(o => o.Style == NewsBadgeStyle.Dot);
            Assert.Equal(NewsBadgeStyle.Dot, main.News.BadgeStyle);

            main.Settings.ShowAnnouncements = false;
            Assert.False(main.News.HasUnread);
        });
}

/// <summary>The mark on the rail, in each of its styles.</summary>
[Collection("avalonia")]
public class NewsBadgeTests(AvaloniaFixture ui)
{
    private static (bool Pill, bool Dot, bool Halo) Shown(NewsBadge badge) =>
        (badge.FindControl<Border>("Pill")!.IsVisible, badge.FindControl<Border>("Dot")!.IsVisible,
            badge.FindControl<Border>("Halo")!.IsVisible);

    [Fact]
    public void EachStyleShowsItsOwnShapeAndNoneShowsNothing() =>
        ui.Run(() =>
        {
            var badge = new NewsBadge { Text = "3" };
            var host = new Window { Content = badge };
            host.Show();   // the halo only counts as shown while the mark is on screen
            try
            {
                badge.Mode = NewsBadgeStyle.Number;
                Assert.Equal((true, false, false), Shown(badge));
                Assert.Equal("3", badge.FindControl<TextBlock>("Number")!.Text);

                badge.Mode = NewsBadgeStyle.Dot;
                Assert.Equal((false, true, false), Shown(badge));

                badge.Mode = NewsBadgeStyle.Pulse;
                Assert.Equal((false, true, true), Shown(badge));

                badge.Mode = NewsBadgeStyle.None;
                Assert.Equal((false, false, false), Shown(badge));
            }
            finally
            {
                host.Close();
            }
        });

    [Fact]
    public void ThePulseOnlyRunsWhileTheMarkIsReallyOnScreen()
    {
        // Its animation never ends and starts on the halo's own IsVisible, which does not follow a
        // parent being hidden. Hidden because nothing is unread, or on a Settings page nobody looks
        // at, a pulse that kept going would keep the render clock awake for nothing.
        ui.Run(() =>
        {
            var badge = new NewsBadge { Mode = NewsBadgeStyle.Pulse, Text = "1" };
            var halo = badge.FindControl<Border>("Halo")!;
            Assert.False(halo.IsVisible);   // not in any window yet

            var holder = new Border { Child = badge };
            var host = new Window { Content = holder };
            host.Show();
            try
            {
                Assert.True(halo.IsVisible);

                badge.IsVisible = false;                    // the rail hides it when nothing is unread
                Assert.False(halo.IsVisible);
                badge.IsVisible = true;
                Assert.True(halo.IsVisible);

                holder.IsVisible = false;                   // a Settings page that is not the one shown
                Assert.False(halo.IsVisible);
                holder.IsVisible = true;
                Assert.True(halo.IsVisible);

                badge.Mode = NewsBadgeStyle.Dot;            // another style has no halo at all
                Assert.False(halo.IsVisible);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void TheMarkDoesNotClipItsHaloToItsOwnTenPixels()
    {
        // A UserControl clips to its own bounds by default. The pulse opens out 7 px past the dot, and
        // with the clip on it showed as a faint square flashing behind the dot instead of a ring.
        ui.Run(() => Assert.False(new NewsBadge().ClipToBounds));
    }
}

/// <summary>The settings the section adds, as the file keeps them.</summary>
public class NewsSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-newsset-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    [Fact]
    public void ByDefaultTheAnnouncementsAreOnMarkedWithANumberAndTheCardIsPending()
    {
        var settings = new AppSettings();

        Assert.True(settings.ShowAnnouncements);
        Assert.Equal(NewsBadgeStyle.Number, settings.NewsBadge);
        Assert.False(settings.NewsMoveNoticeSeen);
    }

    [Fact]
    public void AFileFromBeforeTheSectionReadsAsTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ \"Language\": \"es\" }");

        var settings = new AppSettingsService(_dir).Load();

        Assert.True(settings.ShowAnnouncements);
        Assert.Equal(NewsBadgeStyle.Number, settings.NewsBadge);
    }

    [Fact]
    public void WhatTheUserChoseIsWrittenAndReadBack()
    {
        var service = new AppSettingsService(_dir);
        service.Save(new AppSettings
        {
            ShowAnnouncements = false,
            NewsBadge = NewsBadgeStyle.Pulse,
            NewsMoveNoticeSeen = true,
        });

        var back = service.Load();

        Assert.False(back.ShowAnnouncements);
        Assert.Equal(NewsBadgeStyle.Pulse, back.NewsBadge);
        Assert.True(back.NewsMoveNoticeSeen);
    }

    [Fact]
    public void TheCopyUsedForSavingCarriesThemToo()
    {
        var copy = new AppSettings { ShowAnnouncements = false, NewsBadge = NewsBadgeStyle.Dot }.ShallowCopy();

        Assert.False(copy.ShowAnnouncements);
        Assert.Equal(NewsBadgeStyle.Dot, copy.NewsBadge);
    }
}
