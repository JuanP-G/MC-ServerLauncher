using System.Text.RegularExpressions;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The names the main window feeds the server card, checked against the view models they come from.
/// </summary>
/// <remarks>
/// <c>MainWindow.axaml</c> is <c>x:CompileBindings="False"</c>, so a mistyped name in the card's
/// bindings raises nothing: the header would simply draw an empty card. The card is not rendered
/// here for the reason <c>MissingDependencyPanelTests</c> gives (the headless font manager cannot
/// create the icon font), so the names are read from the markup instead.
/// </remarks>
public class ServerCardBindingTests
{
    private static string CardMarkup()
    {
        var path = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml");
        var xaml = File.ReadAllText(path);

        var start = xaml.IndexOf("<views:ServerCardView", StringComparison.Ordinal);
        Assert.True(start >= 0, "la tarjeta del servidor ya no está en MainWindow.axaml");

        var end = xaml.IndexOf("/>", start, StringComparison.Ordinal);
        return xaml[start..end];
    }

    [Fact]
    public void EveryBindingOnTheCardExistsOnTheServerViewModel()
    {
        var names = Regex.Matches(CardMarkup(), @"=""\{Binding (\w+)\}""").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.True(typeof(ServerViewModel).GetProperty(name) is not null,
                $"ServerViewModel no tiene una propiedad '{name}', y la tarjeta la enlaza");
    }

    [Fact]
    public void TheEditCommandExistsOnTheMainViewModel()
    {
        var match = Regex.Match(CardMarkup(), @"EditCommand=""\{Binding DataContext\.(\w+),");

        Assert.True(match.Success, "la tarjeta ya no enlaza su comando de edición con la ventana");
        Assert.True(typeof(MainViewModel).GetProperty(match.Groups[1].Value) is not null,
            $"MainViewModel no tiene '{match.Groups[1].Value}'");
    }
}
