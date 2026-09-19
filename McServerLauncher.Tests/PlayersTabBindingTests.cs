using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Every name the Players tab and the player profile bind to exists on the view model they bind to.
/// </summary>
/// <remarks>
/// Both views are <c>x:CompileBindings="False"</c>: a mistyped name raises nothing, and the field
/// just stays empty — indistinguishable from the history recording nothing.
/// </remarks>
public class PlayersTabBindingTests
{
    private static string View(string file) =>
        File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", file));

    private static IEnumerable<string> BoundNames(string markup) =>
        Regex.Matches(markup, @"\{Binding !?([A-Za-z0-9_.]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct();

    private static void AssertResolves(Type type, string path, string where)
    {
        var current = type;
        foreach (var part in path.Split('.'))
        {
            var property = current.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"{current.Name} has no property «{part}», which {where} binds to ({path})");
            current = property!.PropertyType;
        }
    }

    [Fact]
    public void EveryNameTheProfileBindsToExists()
    {
        var view = View("PlayerDetailsView.axaml");
        var template = Regex.Match(view, @"<DataTemplate>[\s\S]*?</DataTemplate>").Value;
        Assert.NotEmpty(template);

        foreach (var name in BoundNames(view.Replace(template, "")))
            AssertResolves(typeof(PlayerDetailsViewModel), name, "PlayerDetailsView");
        foreach (var name in BoundNames(template))
            AssertResolves(typeof(PlayerEventRowViewModel), name, "a line of a player's activity");
    }

    [Fact]
    public void EveryNameThePlayerListBindsToExists()
    {
        var main = View("MainWindow.axaml");
        var start = main.IndexOf("<!-- Everyone who has been on the server", StringComparison.Ordinal);
        var end = main.IndexOf("<!-- Operators -->", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "the player list card was not found in MainWindow.axaml");
        var card = main[start..end];

        // Outside the item template the context is the server; inside it, one row.
        var template = Regex.Match(card, @"<DataTemplate>[\s\S]*?</DataTemplate>").Value;
        foreach (var name in BoundNames(card.Replace(template, "")))
            AssertResolves(typeof(ServerViewModel), name, "the Players tab");
        foreach (var name in BoundNames(template).Where(n => !n.StartsWith("DataContext.", StringComparison.Ordinal)))
            AssertResolves(typeof(PlayerRowViewModel), name, "a row of the player list");

        Assert.Contains("DataContext.History.OpenCommand", template);
        AssertResolves(typeof(ServerViewModel), "History.OpenCommand", "a row of the player list");
    }

    [Fact]
    public void TheProfileAndTheListsTakeTurns()
    {
        var main = View("MainWindow.axaml");
        Assert.Contains("IsVisible=\"{Binding History.HasDetails}\"", main);
        Assert.Contains("IsVisible=\"{Binding !History.HasDetails}\"", main);
        AssertResolves(typeof(ServerViewModel), "History.HasDetails", "the Players tab");
        AssertResolves(typeof(ServerViewModel), "History.Details", "the Players tab");
    }
}
