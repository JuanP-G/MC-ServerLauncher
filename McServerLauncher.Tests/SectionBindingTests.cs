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

    /// <summary>The plain property or command names bound with <c>{Binding Name}</c> in a piece of markup.</summary>
    private static IEnumerable<string> Bound(string xaml) =>
        Regex.Matches(xaml, @"\{Binding (!?)(\w+)[,}]").Select(m => m.Groups[2].Value).Distinct();

    private static void AssertAllExist(Type type, IEnumerable<string> names)
    {
        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.True(type.GetProperty(name) is not null, $"{type.Name} no tiene '{name}', y la vista lo enlaza");
    }

    [Fact]
    public void TunnelsViewBindsToTheTunnelsViewModelOrItsRows()
    {
        var xaml = View("TunnelsView.axaml");

        // Each item template binds to the row or the suggestion; the rest to the screen itself.
        var rowStart = xaml.IndexOf("<ItemsControl ItemsSource=\"{Binding Rows}\">", StringComparison.Ordinal);
        var rowEnd = xaml.IndexOf("</ItemsControl>", rowStart, StringComparison.Ordinal);
        var sugStart = xaml.IndexOf("<ItemsControl ItemsSource=\"{Binding Suggestions}\">", StringComparison.Ordinal);
        var sugEnd = xaml.IndexOf("</ItemsControl>", sugStart, StringComparison.Ordinal);
        Assert.True(rowStart > 0 && sugStart > 0, "ya no están las listas de la pantalla de túneles");

        var rows = xaml[rowStart..rowEnd];
        var suggestions = xaml[sugStart..sugEnd];
        var screen = xaml.Replace(rows, "").Replace(suggestions, "");

        AssertAllExist(typeof(TunnelsViewModel), Bound(screen));
        AssertAllExist(typeof(TunnelRowViewModel), Bound(rows.Replace("{Binding Rows}", "")));
        AssertAllExist(typeof(TunnelSuggestionViewModel), Bound(suggestions.Replace("{Binding Suggestions}", "")));
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
        Assert.Contains("<views:AboutView", xaml);
        Assert.Contains("IsVisible=\"{Binding IsTunnelsSection}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding IsAboutSection}\"", xaml);
    }

    [Fact]
    public void SettingsNoLongerCarriesThePlayitAccount()
    {
        // The account moved to the tunnels screen; left in both, the two would show different states.
        var xaml = View("SettingsDialog.axaml");

        Assert.DoesNotContain("PlayitDot", xaml);
        Assert.DoesNotContain("ConnectPlayit_Click", xaml);
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
