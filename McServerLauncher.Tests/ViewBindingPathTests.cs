using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Every binding in the views' markup, resolved against the view model it binds to.
/// </summary>
/// <remarks>
/// The safety net for splitting the big view models: a property moved out of
/// <see cref="ServerViewModel"/> or <see cref="MainViewModel"/> but still named by a view is a binding
/// that finds nothing and says nothing, and these tests fail on it. They complement
/// <c>ViewBindingTests</c>, which hosts the views but only sees the bindings that are alive in that
/// moment (not the rows of an empty list, and not <c>MainWindow</c>, which cannot be hosted).
/// </remarks>
public class ViewBindingPathTests
{
    private static string View(string file) =>
        File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", file));

    private static void NoMisses(string file, Type root, int atLeast = 5)
    {
        var report = XamlBindingChecker.Inspect(View(file), root);
        Assert.True(report.Misses.Count == 0, file + ":" + Environment.NewLine + string.Join(Environment.NewLine,
            report.Misses.Select(m => $"  {{Binding {m.Path}}} on <{m.Element}>: {m.OnType} has no such property")));
        // A checker that could follow nothing (a markup change it no longer understands) must not pass.
        Assert.True(report.Followed >= atLeast, $"{file}: solo se pudieron comprobar {report.Followed} bindings");
    }

    [Fact]
    public void TheMainWindowBindsOnlyToWhatItsViewModelsHave() =>
        NoMisses("MainWindow.axaml", typeof(MainViewModel), atLeast: 100);

    [Theory]
    [InlineData("PlayersTabView.axaml", typeof(ServerViewModel))]
    [InlineData("ServerModsView.axaml", typeof(ServerModsViewModel))]
    [InlineData("ServerBackupsView.axaml", typeof(ServerBackupsViewModel))]
    [InlineData("TunnelsView.axaml", typeof(TunnelsViewModel))]
    [InlineData("NewsView.axaml", typeof(NewsViewModel))]
    [InlineData("SettingsView.axaml", typeof(SettingsViewModel))]
    [InlineData("AboutView.axaml", typeof(MainViewModel))]
    public void EachViewBindsOnlyToWhatItsViewModelHas(string file, Type root) => NoMisses(file, root);

    // ---- the checker itself: without these, a checker that sees nothing would pass everything ----

    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"";

    private sealed class Row { public string Label { get; set; } = ""; }

    private sealed class Screen
    {
        public string Title { get; set; } = "";
        public List<Row> Rows { get; set; } = new();
        public Screen? Child { get; set; }
    }

    [Fact]
    public void ABindingToAMissingPropertyIsReported()
    {
        var misses = XamlBindingChecker.Check($"<Grid {Ns}><TextBlock Text=\"{{Binding Nope}}\"/></Grid>",
            typeof(MainViewModel));
        Assert.Equal("Nope", Assert.Single(misses).Path);
    }

    [Fact]
    public void ABindingThroughAPathIsFollowedToItsEnd()
    {
        var ok = XamlBindingChecker.Check($"<Grid {Ns}><TextBlock Text=\"{{Binding News.Versions}}\"/></Grid>",
            typeof(MainViewModel));
        var bad = XamlBindingChecker.Check($"<Grid {Ns}><TextBlock Text=\"{{Binding News.Nope}}\"/></Grid>",
            typeof(MainViewModel));
        Assert.Empty(ok);
        Assert.Equal("News.Nope", Assert.Single(bad).Path);
    }

    [Fact]
    public void ADataContextBindingChangesWhatTheChildrenAreCheckedAgainst()
    {
        var xaml = $"<Grid {Ns} DataContext=\"{{Binding News}}\"><TextBlock Text=\"{{Binding Versions}}\"/>" +
                   "<TextBlock Text=\"{Binding SelectedServer}\"/></Grid>";
        // Versions is on NewsViewModel and not on MainViewModel; SelectedServer is the other way round.
        var misses = XamlBindingChecker.Check(xaml, typeof(MainViewModel));
        Assert.Equal("SelectedServer", Assert.Single(misses).Path);
    }

    [Fact]
    public void ARowTemplateIsCheckedAgainstTheItemsOfItsList()
    {
        var xaml = $"<ListBox {Ns} ItemsSource=\"{{Binding Servers}}\"><ListBox.ItemTemplate><DataTemplate>" +
                   "<TextBlock Text=\"{Binding Config}\"/><TextBlock Text=\"{Binding Nope}\"/>" +
                   "</DataTemplate></ListBox.ItemTemplate></ListBox>";
        var misses = XamlBindingChecker.Check(xaml, typeof(MainViewModel));
        Assert.Equal("Nope", Assert.Single(misses).Path);
    }

    [Fact]
    public void AnAncestorBindingReadsTheContextOfThatAncestor()
    {
        var xaml = $"<Window {Ns}><ListBox ItemsSource=\"{{Binding Servers}}\"><ListBox.ItemTemplate><DataTemplate>" +
                   "<Button Command=\"{Binding DataContext.UpdateNowCommand, RelativeSource={RelativeSource AncestorType=Window}}\"/>" +
                   "<Button Command=\"{Binding DataContext.Nope, RelativeSource={RelativeSource AncestorType=Window}}\"/>" +
                   "</DataTemplate></ListBox.ItemTemplate></ListBox></Window>";
        var misses = XamlBindingChecker.Check(xaml, typeof(MainViewModel));
        Assert.Equal("Nope", Assert.Single(misses).Path.Replace("DataContext.", ""));
    }

    [Fact]
    public void WhatCannotBeFollowedIsSkippedNotGuessed()
    {
        var xaml = $"<Grid {Ns}><TextBlock Text=\"{{Binding Text, ElementName=Box}}\"/>" +
                   "<TextBlock Text=\"{Binding $parent[Window].Title}\"/><TextBlock Text=\"{Binding}\"/></Grid>";
        Assert.Empty(XamlBindingChecker.Check(xaml, typeof(MainViewModel)));
    }
}
