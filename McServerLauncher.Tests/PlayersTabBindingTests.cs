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

    /// <summary>Every item template in a view, which each bind to a row rather than to the server.</summary>
    private static IReadOnlyList<string> Templates(string markup) =>
        Regex.Matches(markup, @"<DataTemplate[^>]*>[\s\S]*?</DataTemplate>").Select(m => m.Value).ToList();

    private static string Outside(string markup, IEnumerable<string> templates)
    {
        foreach (var template in templates) markup = markup.Replace(template, "");
        return markup;
    }

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
        var templates = Templates(view);
        Assert.NotEmpty(templates);

        foreach (var name in BoundNames(Outside(view, templates)))
            AssertResolves(typeof(PlayerDetailsViewModel), name, "PlayerDetailsView");
        foreach (var template in templates)
            foreach (var name in BoundNames(template))
                AssertResolves(typeof(PlayerEventRowViewModel), name, "a line of a player's activity");
    }

    /// <summary>
    /// The whole tab now, not just the one card it used to be worth checking.
    /// </summary>
    /// <remarks>
    /// It became worth checking all of it when the tab moved out of MainWindow.axaml into its own
    /// view: there is no longer any need to cut a card out of a nine-hundred-line file to know what
    /// the context is. Outside an item template the context is the server; inside one it is a row,
    /// except for the reaches back up through <c>DataContext.</c>, which are the server again.
    /// </remarks>
    [Fact]
    public void EveryNameThePlayersTabBindsToExists()
    {
        var view = View("PlayersTabView.axaml");
        var templates = Templates(view);
        Assert.NotEmpty(templates);

        foreach (var name in BoundNames(Outside(view, templates)))
            AssertResolves(typeof(ServerViewModel), name, "the Players tab");

        foreach (var template in templates)
            foreach (var name in BoundNames(template))
            {
                if (name.StartsWith("DataContext.", StringComparison.Ordinal))
                    AssertResolves(typeof(ServerViewModel), name["DataContext.".Length..], "a row of the Players tab");
                else if (template.Contains("History.OpenCommand", StringComparison.Ordinal))
                    AssertResolves(typeof(PlayerRowViewModel), name, "a row of the player list");
            }

        Assert.Contains("DataContext.History.OpenCommand", view);
    }

    [Fact]
    public void TheProfileAndTheListsTakeTurns()
    {
        var view = View("PlayersTabView.axaml");
        Assert.Contains("IsVisible=\"{Binding History.HasDetails}\"", view);
        Assert.Contains("IsVisible=\"{Binding !History.HasDetails}\"", view);
        AssertResolves(typeof(ServerViewModel), "History.HasDetails", "the Players tab");
        AssertResolves(typeof(ServerViewModel), "History.Details", "the Players tab");
    }

    /// <summary>
    /// The tab is the only thing that builds the player list, and it says when it is on screen.
    /// </summary>
    /// <remarks>
    /// If this view stopped calling <c>EnsureLoaded</c> the list would simply be empty, and a list
    /// that is empty because nobody asked for it looks exactly like a history that recorded nothing.
    /// </remarks>
    [Fact]
    public void TheTabAsksForItsListWhenItIsShown()
    {
        var code = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher",
            "Views", "PlayersTabView.axaml.cs"));
        Assert.Contains("EnsureLoaded()", code);
        Assert.Contains("DataContextChanged", code);
    }
}
