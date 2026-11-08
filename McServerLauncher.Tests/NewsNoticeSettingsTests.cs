using System.IO;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The settings the notices add to Anuncios: where the strip shows, whether an urgent notice also
/// notifies, and which kinds are wanted. The defaults are the point: with nothing chosen, an urgent
/// notice must be seen.
/// </summary>
public class NewsNoticeSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-nsettings-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private static (SettingsViewModel Vm, AppSettings Settings, List<AppSettings> Saves) Make()
    {
        var settings = new AppSettings();
        var saves = new List<AppSettings>();
        var vm = new SettingsViewModel(null, settings, new AppSettingsService(), () => { }, () => { })
        {
            SaveOverride = s => saves.Add(s),
        };
        return (vm, settings, saves);
    }

    [Fact]
    public void ByDefaultAnUrgentNoticeHasAStripEverywhereAndAnAnnouncement()
    {
        var settings = new AppSettings();

        Assert.Equal(NewsStripStyle.Everywhere, settings.NewsStrip);
        Assert.True(settings.NewsToast);
        Assert.True(settings.NewsIncidents);
        Assert.True(settings.NewsTips);
    }

    [Fact]
    public void AFileFromBeforeTheseSettingsReadsAsTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ \"ShowAnnouncements\": true, \"NewsBadge\": 1 }");

        var settings = new AppSettingsService(_dir).Load();

        Assert.Equal(NewsStripStyle.Everywhere, settings.NewsStrip);
        Assert.True(settings.NewsToast);
        Assert.True(settings.NewsIncidents);
        Assert.True(settings.NewsTips);
    }

    [Fact]
    public void WhatTheUserChoseIsWrittenAndReadBack()
    {
        var service = new AppSettingsService(_dir);
        service.Save(new AppSettings
        {
            NewsStrip = NewsStripStyle.TunnelsOnly,
            NewsToast = false,
            NewsIncidents = false,
            NewsTips = false,
        });

        var back = service.Load();

        Assert.Equal(NewsStripStyle.TunnelsOnly, back.NewsStrip);
        Assert.False(back.NewsToast);
        Assert.False(back.NewsIncidents);
        Assert.False(back.NewsTips);
    }

    [Fact]
    public void TheCopyUsedForSavingCarriesThemToo()
    {
        var copy = new AppSettings { NewsStrip = NewsStripStyle.None, NewsToast = false }.ShallowCopy();

        Assert.Equal(NewsStripStyle.None, copy.NewsStrip);
        Assert.False(copy.NewsToast);
    }

    [Fact]
    public void ThePickerOffersEveryPlaceForTheStripWithAWordForEach()
    {
        var (vm, _, _) = Make();

        Assert.Equal(Enum.GetValues<NewsStripStyle>().OrderBy(s => s), vm.StripOptions.Select(o => o.Style).OrderBy(s => s));
        Assert.All(vm.StripOptions, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Name));
            Assert.DoesNotContain("News_", o.Name);   // a key that was not found comes back as itself
        });
    }

    [Fact]
    public void ThePlacePickedIsSavedByThePickerItself()
    {
        // The view saves the switches it sees flip; a picker saves in its own setter.
        var (vm, settings, saves) = Make();
        Assert.Equal(NewsStripStyle.Everywhere, vm.SelectedStripOption.Style);

        vm.SelectedStripOption = vm.StripOptions.Single(o => o.Style == NewsStripStyle.TunnelsOnly);

        Assert.Equal(NewsStripStyle.TunnelsOnly, settings.NewsStrip);
        Assert.Equal(NewsStripStyle.TunnelsOnly, vm.SelectedStripOption.Style);
        Assert.Single(saves);
    }

    [Fact]
    public void EachSwitchSavesStraightAwayAndOnlyWhenItMoves()
    {
        var (vm, settings, saves) = Make();

        vm.NewsToast = false;
        vm.NewsIncidents = false;
        vm.NewsTips = false;

        Assert.False(settings.NewsToast);
        Assert.False(settings.NewsIncidents);
        Assert.False(settings.NewsTips);
        Assert.Equal(3, saves.Count);

        vm.NewsToast = false;
        vm.NewsIncidents = false;
        vm.NewsTips = false;
        Assert.Equal(3, saves.Count);        // nothing moved: nothing written
    }
}
