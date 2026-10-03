using System.Text.RegularExpressions;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Searching the list of installed mods and plugins.
/// </summary>
/// <remarks>
/// The list shows a filtered copy, never a filtered <c>InstalledMods</c>: that collection is also
/// what counts pending updates and what "update all" walks, so a search box that filtered it in
/// place would quietly hide updates from both.
/// </remarks>
public class InstalledModsFilterTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-filter-" + Guid.NewGuid().ToString("N"));

    private static readonly string[] Jars =
    {
        "sodium-fabric-0.6.0+mc1.21.1.jar",
        "sodium-extra-0.6.0+mc1.21.1.jar",
        "fabric-api-0.102.0+1.21.1.jar",
        "Geyser-Fabric-2.4.2.jar",
        "lithium-fabric-0.13.0.jar.disabled",
    };

    private ServerModsViewModel ModsWith(params string[] jars)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "mods"));
        foreach (var jar in jars) File.WriteAllText(Path.Combine(_folder, "mods", jar), "");
        return new ServerModsViewModel(new ServerConfig
        {
            Name = "test", FolderPath = _folder, Type = ServerType.Fabric, GameVersion = "1.21.1"
        });
    }

    private static string[] Shown(ServerModsViewModel mods) =>
        mods.VisibleInstalledMods.Select(m => m.FileName).OrderBy(n => n).ToArray();

    [Fact]
    public void AnEmptyBoxShowsEverything()
    {
        var mods = ModsWith(Jars);

        Assert.Equal(5, mods.VisibleInstalledMods.Count);
        Assert.False(mods.HasNoMatches);
        Assert.Equal("5", mods.InstalledCountText);
    }

    [Fact]
    public void AWordFilters()
    {
        var mods = ModsWith(Jars);

        mods.InstalledFilter = "sodium";

        Assert.Equal(new[] { "sodium-extra-0.6.0+mc1.21.1.jar", "sodium-fabric-0.6.0+mc1.21.1.jar" }, Shown(mods));
    }

    [Theory]
    [InlineData("sodium extra")]
    [InlineData("extra sodium")]      // order does not matter
    [InlineData("sodium-extra")]      // typed the way the file is named
    [InlineData("SODIUM_Extra")]      // case and underscores do not matter either
    public void SeveralWordsFindTheSameJar(string search)
    {
        var mods = ModsWith(Jars);

        mods.InstalledFilter = search;

        Assert.Equal("sodium-extra-0.6.0+mc1.21.1.jar", Assert.Single(mods.VisibleInstalledMods).FileName);
    }

    [Fact]
    public void ADisabledModIsFoundByItsName()
    {
        // The row shows the name without ".disabled", and that is what people search for.
        var mods = ModsWith(Jars);

        mods.InstalledFilter = "lithium";

        Assert.False(Assert.Single(mods.VisibleInstalledMods).IsEnabled);
    }

    [Fact]
    public void NothingMatchingSaysSoAndCountsIt()
    {
        var mods = ModsWith(Jars);

        mods.InstalledFilter = "create";

        Assert.Empty(mods.VisibleInstalledMods);
        Assert.True(mods.HasNoMatches);
        Assert.Equal(string.Format(McServerLauncher.Localization.Localizer.Get("Mods_InstalledCountFmt"), 0, 5),
            mods.InstalledCountText);
    }

    [Fact]
    public void AnEmptyFolderIsNotANoMatch()
    {
        // "Nothing is installed" and "nothing matches" are different messages.
        var mods = ModsWith();

        mods.InstalledFilter = "sodium";

        Assert.False(mods.HasNoMatches);
    }

    [Fact]
    public void SearchingHidesNoUpdates()
    {
        var mods = ModsWith(Jars);
        foreach (var mod in mods.InstalledMods)
            mod.Update = new ModUpdateInfo("9.9.9", "https://example.invalid", mod.FileName, null, null);
        var pending = mods.PendingUpdateCount;

        mods.InstalledFilter = "geyser";

        Assert.Single(mods.VisibleInstalledMods);
        Assert.Equal(5, mods.InstalledMods.Count);
        Assert.Equal(pending, mods.PendingUpdateCount);
    }

    [Fact]
    public void ASearchSurvivesARescan()
    {
        var mods = ModsWith(Jars);
        mods.InstalledFilter = "sodium";

        File.WriteAllText(Path.Combine(_folder, "mods", "sodium-shadowy-path-blocks-1.0.jar"), "");
        mods.ReloadInstalled();

        Assert.Equal(3, mods.VisibleInstalledMods.Count);
        Assert.All(mods.VisibleInstalledMods, m => Assert.Contains("sodium", m.FileName));
    }

    [Fact]
    public void ClearingTheBoxBringsEverythingBack()
    {
        var mods = ModsWith(Jars);
        mods.InstalledFilter = "sodium";

        mods.InstalledFilter = "   ";

        Assert.Equal(5, mods.VisibleInstalledMods.Count);
    }

    // --- The panel ---

    [Fact]
    public void EveryNameTheInstalledPanelBindsToExists()
    {
        // ServerModsView.axaml is x:CompileBindings="False": a mistyped name raises nothing, and a
        // search box bound to nothing looks exactly like one that finds nothing.
        var markup = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "ServerModsView.axaml"));
        var start = markup.IndexOf("<!-- Right: installed -->", StringComparison.Ordinal);
        Assert.True(start >= 0, "el panel de instalados ya no está en ServerModsView.axaml");
        var panel = markup[start..];
        var template = Regex.Match(panel, @"<DataTemplate[\s\S]*?</DataTemplate>").Value;

        foreach (Match m in Regex.Matches(panel.Replace(template, ""), @"\{Binding !{0,2}([A-Za-z0-9_.]+)"))
        {
            var type = typeof(ServerModsViewModel);
            foreach (var part in m.Groups[1].Value.Split('.'))
            {
                var property = type.GetProperty(part);
                Assert.True(property is not null, $"{type.Name} no tiene «{part}» ({m.Groups[1].Value})");
                type = property!.PropertyType;
            }
        }

        Assert.Contains("{Binding VisibleInstalledMods}", panel);
        Assert.Contains("{Binding InstalledFilter, Mode=TwoWay}", panel);
    }

    [Theory]
    [InlineData("", "anything.jar", true)]
    [InlineData("geyser", "Geyser-Spigot.jar", true)]
    [InlineData("geyser spigot", "Geyser-Spigot.jar", true)]
    [InlineData("geyser paper", "Geyser-Spigot.jar", false)]
    [InlineData("1.21", "fabric-api-0.102.0+1.21.1.jar", true)]
    public void TheRuleOnItsOwn(string search, string file, bool expected) =>
        Assert.Equal(expected, InstalledModFilter.Matches(file, InstalledModFilter.Words(search)));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
