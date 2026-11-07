using System.Text.RegularExpressions;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The names the rail, the tunnels screen and About bind to, checked against what they bind to.
/// </summary>
/// <remarks>
/// All three views are <c>x:CompileBindings="False"</c>, so a mistyped name raises nothing: the
/// screen just shows nothing, or a button silently does nothing. The views cannot be rendered here
/// (the headless font manager cannot create the icon font), so the names are read from the markup.
/// </remarks>
public class SectionBindingTests
{
    private static string View(string file) =>
        File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", file));

    /// <summary>
    /// The plain property or command names bound with <c>{Binding Name}</c> in a piece of markup.
    /// </summary>
    private static IEnumerable<string> Bound(string xaml) =>
        Regex.Matches(xaml, @"\{Binding (!?)(\w+)[,}]").Select(m => m.Groups[2].Value).Distinct();

    private static void AssertAllExist(Type type, IEnumerable<string> names)
    {
        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.True(type.GetProperty(name) is not null,
                $"{type.Name} no tiene '{name}', y la vista lo enlaza");
    }

    [Fact]
    public void TunnelsViewBindsToTheTunnelsViewModelOrItsRows()
    {
        var xaml = View("TunnelsView.axaml");

        // Each item template binds to the row, the suggestion or the way into the account; the rest to
        // the screen itself.
        var rowStart = xaml.IndexOf("<ItemsControl ItemsSource=\"{Binding Rows}\">",
            StringComparison.Ordinal);
        var rowEnd = xaml.IndexOf("</ItemsControl>", rowStart, StringComparison.Ordinal);
        var sugStart = xaml.IndexOf("<ItemsControl ItemsSource=\"{Binding Suggestions}\">",
            StringComparison.Ordinal);
        var sugEnd = xaml.IndexOf("</ItemsControl>", sugStart, StringComparison.Ordinal);
        var laneStart = xaml.IndexOf("<ItemsControl ItemsSource=\"{Binding Lanes}\">", StringComparison.Ordinal);
        var laneEnd = xaml.IndexOf("</ItemsControl>", laneStart, StringComparison.Ordinal);
        Assert.True(rowStart > 0 && sugStart > 0 && laneStart > 0,
            "ya no están las listas de la pantalla de túneles");

        var rows = xaml[rowStart..rowEnd];
        var suggestions = xaml[sugStart..sugEnd];
        var lanes = xaml[laneStart..laneEnd];
        var screen = xaml.Replace(rows, "").Replace(suggestions, "").Replace(lanes, "");

        AssertAllExist(typeof(TunnelsViewModel), Bound(screen));
        AssertAllExist(typeof(TunnelRowViewModel), Bound(rows.Replace("{Binding Rows}", "")));
        AssertAllExist(typeof(TunnelSuggestionViewModel),
            Bound(suggestions.Replace("{Binding Suggestions}", "")));
        AssertAllExist(typeof(TunnelLaneViewModel), Bound(lanes.Replace("{Binding Lanes}", "")));
    }

    [Fact]
    public void AboutViewBindsToNamesThatExistOnTheMainViewModel() =>
        AssertAllExist(typeof(MainViewModel), Bound(View("AboutView.axaml")));

    [Fact]
    public void TheRailBindsToNamesThatExistOnTheMainViewModel()
    {
        var xaml = View("MainWindow.axaml");
        var start = xaml.IndexOf("<!-- Rail:", StringComparison.Ordinal);
        var end = xaml.IndexOf("<!-- Sections share one cell", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "el rail ya no está en MainWindow.axaml");

        var rail = xaml[start..end];
        AssertAllExist(typeof(MainViewModel), Bound(rail));
        // The class that shows which section is on is bound too, and is not a plain {Binding Name}.
        Assert.Matches(@"Classes\.on=""\{Binding IsTunnelsSection\}""", rail);
        Assert.Matches(@"Classes\.on=""\{Binding IsNewsSection\}""", rail);
        Assert.Contains("Command=\"{Binding ShowNewsCommand}\"", rail);
        // Five buttons, and each in its own row of the rail: two in one row would draw on top of each other.
        var rows = System.Text.RegularExpressions.Regex.Matches(rail, @"<Button Grid\.Row=""(\d)"" Classes=""rail""")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "0", "1", "2", "4", "5" }, rows);
    }

    [Fact]
    public void NewsViewBindsToTheNewsViewModelOrItsRows()
    {
        var xaml = View("NewsView.axaml");

        // The version rows and the notice rows each bind to their own row; the rest of the screen to the
        // screen itself.
        string List(string source)
        {
            var start = xaml.IndexOf($"<ItemsControl ItemsSource=\"{{Binding {source}}}\">", StringComparison.Ordinal);
            var end = xaml.IndexOf("</ItemsControl>", start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > start, $"ya no está la lista {source} de la pantalla de novedades");
            return xaml[start..end];
        }

        var versions = List("Versions");
        var notices = List("Notices");
        var screen = xaml.Replace(versions, "").Replace(notices, "");

        AssertAllExist(typeof(NewsViewModel), Bound(screen));
        AssertAllExist(typeof(NewsVersionRowViewModel), Bound(versions.Replace("{Binding Versions}", "")));
        AssertAllExist(typeof(NewsNoticeViewModel), Bound(notices.Replace("{Binding Notices}", "")));
    }

    [Fact]
    public void TheNoticeStripBindsToNamesThatExistOnTheMainAndNewsViewModels()
    {
        // The strip is a Border of MainWindow bound to MainViewModel.ShowNewsBand; what it says comes from
        // News.*, which the plain-name check does not see (it has a dot).
        var xaml = View("MainWindow.axaml");
        var start = xaml.IndexOf("IsVisible=\"{Binding ShowNewsBand}\"", StringComparison.Ordinal);
        Assert.True(start > 0, "la banda de avisos ya no está en la ventana principal");
        var end = xaml.IndexOf("<Grid Grid.Row=\"2\"", start, StringComparison.Ordinal);
        var strip = xaml[start..end];

        var names = System.Text.RegularExpressions.Regex.Matches(strip, @"\{Binding News\.(\w+)\}")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        AssertAllExist(typeof(NewsViewModel), names);
        Assert.Contains("BandTitle", names);
        Assert.Contains("CloseBandCommand", names);
        Assert.True(typeof(MainViewModel).GetProperty("ShowNewsBand") is not null);
        Assert.True(typeof(MainViewModel).GetProperty("ShowNewsCommand") is not null);
        Assert.Contains("Command=\"{Binding ShowNewsCommand}\"", strip);
    }

    [Fact]
    public void TheRailMarkBindsToNamesThatExistOnTheNewsViewModel()
    {
        // {Binding News.X} has a dot, which the plain-name check above does not see.
        var xaml = View("MainWindow.axaml");
        var names = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{Binding News\.(\w+)\}")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        AssertAllExist(typeof(NewsViewModel), names);
        Assert.Contains("BadgeStyle", names);
        Assert.Contains("HasUnread", names);
    }

    [Fact]
    public void EverySectionHasItsOwnFlagOnTheMainViewModel()
    {
        foreach (var section in Enum.GetValues<AppSection>())
            Assert.True(typeof(MainViewModel).GetProperty($"Is{section}Section") is not null,
                $"MainViewModel no tiene Is{section}Section");
    }

    [Fact]
    public void TheSectionsWithAScreenAreMountedInMainWindow()
    {
        var xaml = View("MainWindow.axaml");

        Assert.Contains("<views:TunnelsView DataContext=\"{Binding Tunnels}\"", xaml);
        Assert.Contains("<views:NewsView DataContext=\"{Binding News}\"", xaml);
        Assert.Contains("<views:AboutView", xaml);
        Assert.Contains("IsVisible=\"{Binding IsTunnelsSection}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding IsNewsSection}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding IsAboutSection}\"", xaml);
    }

    [Fact]
    public void SettingsNoLongerCarriesThePlayitAccount()
    {
        // The account moved to the tunnels screen; left in both, the two would show different states.
        var xaml = View("SettingsView.axaml");

        Assert.DoesNotContain("Playit", xaml);
    }

    [Fact]
    public void SettingsViewBindsToNamesThatExistOnTheSettingsViewModel()
    {
        // Names under Notifications. belong to the model and are checked in NotificationLookTests.
        var names = Bound(View("SettingsView.axaml").Replace("{Binding Name}", ""));
        AssertAllExist(typeof(SettingsViewModel), names);
    }

    [Fact]
    public void SettingsIsAScreenAndTheDialogIsGone()
    {
        var xaml = View("MainWindow.axaml");

        Assert.Contains("<views:SettingsView DataContext=\"{Binding Settings}\"", xaml);
        Assert.Contains("Command=\"{Binding ShowSettingsCommand}\"", xaml);
        Assert.False(File.Exists(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "SettingsDialog.axaml")));
    }

    // --- the update check ---

    private static UpdateService.UpdateInfo Newer() => new("2.0.0", "https://example/release", null, null, null);

    [Fact]
    public async Task NothingNewerMeansUpToDate()
    {
        var (state, info) = await UpdateCheck.RunAsync(() => Task.FromResult<UpdateService.UpdateInfo?>(null));

        Assert.Equal(UpdateCheckState.UpToDate, state);
        Assert.Null(info);
    }

    [Fact]
    public async Task ANewerReleaseIsAvailableAndCarriedBack()
    {
        var (state, info) = await UpdateCheck.RunAsync(() => Task.FromResult<UpdateService.UpdateInfo?>(Newer()));

        Assert.Equal(UpdateCheckState.Available, state);
        Assert.Equal("2.0.0", info!.Version);
    }

    [Fact]
    public async Task NotBeingAbleToAskIsNeverReportedAsUpToDate()
    {
        // The button would otherwise tell someone offline that they have the latest version.
        var (state, info) = await UpdateCheck.RunAsync(
            () => throw new HttpRequestException("no connection"));

        Assert.Equal(UpdateCheckState.Failed, state);
        Assert.Null(info);
    }
}
