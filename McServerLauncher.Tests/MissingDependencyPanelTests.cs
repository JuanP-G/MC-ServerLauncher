using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The panel that offers to install the library mods an existing server is missing.
/// </summary>
/// <remarks>
/// <para>
/// <c>ServerModsView</c> is declared <c>x:CompileBindings="False"</c>, so a mistyped binding raises
/// nothing at all: the panel would simply never appear, and the app would look exactly as it did
/// before the feature was written. That is the failure this file exists for.
/// </para>
/// <para>
/// It checks the names in the XAML against the view model rather than rendering the view. Rendering
/// it needs the icon font, which the headless font manager cannot create — the panel is in the same
/// view as sixteen <c>SymbolIcon</c>s — and switching the whole harness to Skia to get one test is
/// a worse trade than reading the two names it would have proved.
/// </para>
/// </remarks>
public class MissingDependencyPanelTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-deps-" + Guid.NewGuid().ToString("N"));

    private ServerModsViewModel Panel()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "mods"));
        return new ServerModsViewModel(new ServerConfig
        {
            Name = "test", FolderPath = _folder, Type = ServerType.Fabric, GameVersion = "1.21.1"
        });
    }

    private static ModDependencyService.Plan PlanFor(params string[] fileNames) =>
        new(fileNames.Select(f => new ModDependencyService.Needed(
                Path.GetFileNameWithoutExtension(f),
                new VersionResult(),
                new VersionFile { Filename = f, Url = "https://example/" + f }))
            .ToList(),
            Array.Empty<string>());

    /// <summary>The block of XAML this panel is, found by the property that only it binds.</summary>
    private static string PanelMarkup()
    {
        var view = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "ServerModsView.axaml");
        var xaml = File.ReadAllText(view);

        var start = xaml.IndexOf("<Border IsVisible=\"{Binding HasMissingDependencies}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "el panel de dependencias que faltan ya no está en ServerModsView.axaml");

        var end = xaml.IndexOf("</Border>", start, StringComparison.Ordinal);
        return xaml[start..(end + "</Border>".Length)];
    }

    [Fact]
    public void EveryNameThePanelBindsToExistsOnTheViewModel()
    {
        var names = Regex.Matches(PanelMarkup(), @"\{Binding\s+!?([A-Za-z0-9_]+)\s*\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // Four: the visibility, the text, the button's command and its enabled state. A panel that
        // binds fewer has lost one of them to an edit, which is the same silent failure.
        Assert.Equal(4, names.Count);

        var type = typeof(ServerModsViewModel);
        foreach (var name in names)
        {
            var member = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                         ?? (MemberInfo?)type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(member is not null, $"ServerModsViewModel no expone \"{name}\", al que se enlaza el panel");
        }
    }

    [Fact]
    public void TheOfferCarriesTheNamesAndNotJustACount()
    {
        // "2 dependencies are missing" with no names is a message the user cannot act on anywhere
        // else either: the whole point is being able to go and look at what is about to arrive.
        var vm = Panel();
        vm.ShowMissingDependencies(PlanFor("fabric-api-0.116.jar", "cristallib-3.1.3.jar"));

        Assert.True(vm.HasMissingDependencies);
        Assert.Contains("fabric-api-0.116.jar", vm.MissingDependencyText);
        Assert.Contains("cristallib-3.1.3.jar", vm.MissingDependencyText);
        Assert.Contains("2", vm.MissingDependencyText);
    }

    [Fact]
    public void NothingIsOfferedWhenNothingIsMissing()
    {
        var vm = Panel();

        Assert.False(vm.HasMissingDependencies);

        vm.ShowMissingDependencies(PlanFor("fabric-api-0.116.jar"));
        Assert.True(vm.HasMissingDependencies);

        // And the offer goes away again: a scan that comes back clean must not leave the previous
        // warning on screen next to a list that no longer matches it.
        vm.ShowMissingDependencies(PlanFor());
        Assert.False(vm.HasMissingDependencies);
        Assert.Null(vm.MissingDependencyText);
    }

    [Fact]
    public void TheNameShownIsTheJarNotAPath()
    {
        // The file name comes from the API. Anything with a separator in it must not reach either
        // the label or, more to the point, the path the download is written to.
        var needed = new ModDependencyService.Needed("x", new VersionResult(),
            new VersionFile { Filename = "../../evil.jar" });

        Assert.Equal("evil.jar", needed.Label);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
