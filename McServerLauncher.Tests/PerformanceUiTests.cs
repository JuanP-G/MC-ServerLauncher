using System.Text.RegularExpressions;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The Performance card, its band and the solutions page: that what the markup binds exists, and that
/// the page offers what the advice says and changes what it promises to.
/// </summary>
/// <remarks>
/// The markup is read rather than rendered, for the reason <see cref="ServerCardBindingTests"/> gives.
/// The page's view model runs with real view models, on a Vanilla server so that nothing asks Modrinth.
/// </remarks>
[Collection("avalonia")]
public sealed class PerformanceUiTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "perfui-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousHistory = MemoryHistoryStore.RootDirectory;

    public void Dispose()
    {
        MemoryHistoryStore.RootDirectory = _previousHistory;
        try { Directory.Delete(_folder, true); } catch { /* temp */ }
    }

    private static string Markup(string relative) =>
        File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", relative));

    private ServerViewModel Server(ServerType type = ServerType.Vanilla, string properties = "simulation-distance=10\nview-distance=12\n")
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "server.properties"), properties);
        MemoryHistoryStore.RootDirectory = Path.Combine(_folder, "memory");
        return new ServerViewModel(new ServerConfig
        {
            Name = "t", Id = Guid.NewGuid().ToString("N"), FolderPath = _folder, Type = type, GameVersion = "1.21.1",
            MemoryMode = MemoryMode.Manual, MinRamGb = 2, MaxRamGb = 4
        });
    }

    private static PerformanceStatus Saturated(PerformanceCause cause = PerformanceCause.Ticks) =>
        new(PerformanceLevel.Saturated, cause, 12.0, 80, 0);

    // --- markup ---

    [Fact]
    public void EveryBindingOfTheCardAndTheBandExistsOnTheServerViewModel()
    {
        var xaml = Markup(Path.Combine("Views", "MainWindow.axaml"));
        var start = xaml.IndexOf("Perf_Label", StringComparison.Ordinal);
        Assert.True(start > 0, "la tarjeta de Rendimiento ya no está");
        var card = xaml[xaml.LastIndexOf("<Border", start, StringComparison.Ordinal)..xaml.IndexOf("</Border>", start, StringComparison.Ordinal)];

        var bandStart = xaml.IndexOf("Classes=\"band perf\"", StringComparison.Ordinal);
        Assert.True(bandStart > 0, "la banda de Rendimiento ya no está");
        var band = xaml[bandStart..xaml.IndexOf("</Border>", bandStart, StringComparison.Ordinal)];

        foreach (var name in Regex.Matches(card + band, @"\{Binding (\w+)\}").Select(m => m.Groups[1].Value).Distinct())
            Assert.True(typeof(ServerViewModel).GetProperty(name) is not null,
                $"ServerViewModel no tiene una propiedad '{name}', y la tarjeta o la banda la enlaza");
    }

    [Fact]
    public void TheBandIsOneLine()
    {
        var xaml = Markup(Path.Combine("Views", "MainWindow.axaml"));
        var band = xaml[xaml.IndexOf("Classes=\"band perf\"", StringComparison.Ordinal)..];
        band = band[..band.IndexOf("</Border>", StringComparison.Ordinal)];

        // Cut with an ellipsis, never wrapped: it must not take room from what is below.
        Assert.Contains("TextWrapping=\"NoWrap\"", band);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", band);
    }

    [Fact]
    public void EveryBindingOfTheSolutionsPageExists()
    {
        var xaml = Markup(Path.Combine("Views", "PerformanceSolutionsView.axaml"));
        var names = Regex.Matches(xaml, @"\{Binding (\w+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.Contains("BackCommand", names);
        foreach (var name in names)
        {
            var known = typeof(PerformanceSolutionsViewModel).GetProperty(name) is not null
                        || typeof(SolutionRow).GetProperty(name) is not null;
            Assert.True(known, $"ni PerformanceSolutionsViewModel ni SolutionRow tienen '{name}', y la página lo enlaza");
        }
    }

    // --- the page ---

    [Fact]
    public void TheSettingsOfAServerThatIsTooHeavyAreOffered() =>
        ui.Run(() =>
        {
            var page = new PerformanceSolutionsViewModel(Server(), Saturated());

            Assert.Equal(2, page.Settings.Count);
            Assert.Contains("10", page.Settings[0].Title);
            Assert.Contains("4", page.Settings[0].Title);   // saturated: down to 4
            Assert.True(page.IsVanilla);
            Assert.False(page.HasContent);
        });

    [Fact]
    public void ApplyingASettingChangesServerPropertiesAndSaysItNeedsARestart() =>
        ui.Run(() =>
        {
            var page = new PerformanceSolutionsViewModel(Server(), Saturated());

            page.Settings[0].ApplyCommand.Execute(null);
            page.Settings[1].ApplyCommand.Execute(null);

            var written = File.ReadAllText(Path.Combine(_folder, "server.properties"));
            Assert.Contains("simulation-distance=4", written);
            Assert.Contains("view-distance=8", written);
            Assert.True(page.Settings[0].HasState);
            Assert.False(page.Settings[0].ShowButton);
        });

    [Fact]
    public void ARaiseOnAManualServerIsKeptAsItsMaximum() =>
        ui.Run(() =>
        {
            var host = Server();
            var page = new PerformanceSolutionsViewModel(host, Saturated(PerformanceCause.OutOfMemory), 32768, 20000);

            var ram = page.Settings.Single(r => r.Detail == Localization.Localizer.Get("Sol_RamDetailManual"));

            ram.ApplyCommand.Execute(null);

            Assert.Equal(6, host.Config.MaxRamGb);
        });

    [Fact]
    public void ARaiseOnAnAutomaticServerIsOnlyForTheNextStart() =>
        ui.Run(() =>
        {
            var host = Server();
            host.Config.MemoryMode = MemoryMode.Automatic;
            var page = new PerformanceSolutionsViewModel(host, Saturated(PerformanceCause.OutOfMemory), 32768, 20000);

            var ram = page.Settings.Single(r => r.Detail == Localization.Localizer.Get("Sol_RamDetailAuto"));

            ram.ApplyCommand.Execute(null);

            Assert.Equal(4, host.Config.MaxRamGb);   // the saved maximum is untouched
            Assert.Equal(PerformanceAdvice.RaiseByMb, MemoryHistoryStore.For(host.Config.Id).TakeNextStartBoost());
        });

    [Fact]
    public void ADistancePaperHasFixedInItsOwnConfigIsNotOfferedAsAChange() =>
        ui.Run(() =>
        {
            var host = Server(ServerType.Paper);
            File.WriteAllText(Path.Combine(_folder, "spigot.yml"),
                "world-settings:\n  default:\n    simulation-distance: 8\n    view-distance: default\n");

            var page = new PerformanceSolutionsViewModel(host, Saturated());

            var sim = page.Settings.First(r => r.Title.Contains("10"));
            Assert.False(sim.ShowButton);
            Assert.True(sim.HasState);
        });

    [Fact]
    public void ASolutionsPageOpensAndClosesInPlaceOfTheTabs() =>
        ui.Run(() =>
        {
            var host = Server();
            Assert.True(host.ShowingTabs);

            host.ShowPerformanceSolutionsCommand.Execute(null);
            Assert.True(host.ShowingSolutions);
            Assert.False(host.ShowingTabs);

            host.Solutions!.BackCommand.Execute(null);
            Assert.True(host.ShowingTabs);
            Assert.Null(host.Solutions);
        });
}
