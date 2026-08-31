using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Showing and hiding console lines, counting them, and copying them out again.
/// </summary>
/// <remarks>
/// The console had no test of any kind before this. It now holds a typed line instead of a string,
/// which is what makes the colours and the filters possible — and which quietly threatens the one
/// thing people do with a console once something goes wrong, which is select it and paste it
/// somewhere.
/// </remarks>
public class ConsoleFilterTests
{
    private static IReadOnlyDictionary<ConsoleLineKind, ConsoleKindFilter> Kinds(
        params ConsoleLineKind[] off)
    {
        var all = Enum.GetValues<ConsoleLineKind>().Select(k => new ConsoleKindFilter(k)).ToList();
        foreach (var filter in all.Where(f => off.Contains(f.Kind))) filter.IsOn = false;
        return all.ToDictionary(f => f.Kind);
    }

    private static ConsoleLine Line(string text, ConsoleLineKind kind = ConsoleLineKind.Info) =>
        new(text, kind);

    // --- The line survives the clipboard ---

    [Fact]
    public void ALineStringifiesToItsText()
    {
        // The console's copy — the context menu and Ctrl+C — goes through ToString over the selected
        // items. A positional record's generated one prints "ConsoleLine { Text = …, Kind = … }",
        // which compiles perfectly and fills the clipboard with rubbish. This is that guard.
        Assert.Equal("[12:00:00] [Server thread/INFO]: hola",
            Line("[12:00:00] [Server thread/INFO]: hola").ToString());
    }

    [Fact]
    public void TheCopyPathNamesTheTextInsteadOfTrustingToString()
    {
        // Belt as well as braces: the two would have to be broken together to lose the clipboard.
        var source = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("o is ConsoleLine line ? line.Text", source, StringComparison.Ordinal);
    }

    // --- Search ---

    [Fact]
    public void AnEmptySearchKeepsEverything()
    {
        var kinds = Kinds();
        Assert.True(ConsoleKindFilter.Matches(Line("cualquier cosa"), null, kinds));
        Assert.True(ConsoleKindFilter.Matches(Line("cualquier cosa"), "   ", kinds));
    }

    [Fact]
    public void TheSearchIgnoresCaseAndSurroundingSpaces()
    {
        var kinds = Kinds();
        Assert.True(ConsoleKindFilter.Matches(Line("Loading Fabric API"), "fabric", kinds));
        Assert.True(ConsoleKindFilter.Matches(Line("Loading Fabric API"), "  Fabric  ", kinds));
        Assert.False(ConsoleKindFilter.Matches(Line("Loading Fabric API"), "forge", kinds));
    }

    // --- Categories ---

    [Fact]
    public void SwitchingACategoryOffHidesOnlyThatCategory()
    {
        var kinds = Kinds(off: ConsoleLineKind.Chat);

        Assert.False(ConsoleKindFilter.Matches(Line("<Alice> hola", ConsoleLineKind.Chat), null, kinds));
        Assert.True(ConsoleKindFilter.Matches(Line("algo", ConsoleLineKind.Info), null, kinds));
    }

    [Fact]
    public void ACategoryThatIsOffBeatsAMatchingSearch()
    {
        var kinds = Kinds(off: ConsoleLineKind.Chat);

        // Otherwise typing a word would resurrect lines the user had explicitly hidden.
        Assert.False(ConsoleKindFilter.Matches(Line("<Alice> fabric", ConsoleLineKind.Chat), "fabric", kinds));
    }

    // --- Counting ---

    [Fact]
    public void EveryKindIsCounted()
    {
        var filters = Enum.GetValues<ConsoleLineKind>().Select(k => new ConsoleKindFilter(k)).ToList();

        ConsoleKindFilter.Recount(new[]
        {
            Line("a", ConsoleLineKind.Error),
            Line("b", ConsoleLineKind.Error),
            Line("c", ConsoleLineKind.Chat),
        }, filters);

        Assert.Equal(2, filters.Single(f => f.Kind == ConsoleLineKind.Error).Count);
        Assert.Equal(1, filters.Single(f => f.Kind == ConsoleLineKind.Chat).Count);
        Assert.Equal(0, filters.Single(f => f.Kind == ConsoleLineKind.Info).Count);
    }

    [Fact]
    public void CountingAgainAfterATrimDoesNotAccumulate()
    {
        // The buffer is trimmed from the top every couple of hundred lines. Counters kept by hand at
        // both ends would drift, and the symptom would be a switch quietly claiming there are three
        // errors when they scrolled off an hour ago.
        var filters = Enum.GetValues<ConsoleLineKind>().Select(k => new ConsoleKindFilter(k)).ToList();

        var everything = new[] { Line("a", ConsoleLineKind.Error), Line("b", ConsoleLineKind.Error) };
        ConsoleKindFilter.Recount(everything, filters);
        Assert.Equal(2, filters.Single(f => f.Kind == ConsoleLineKind.Error).Count);

        // What survives a trim.
        ConsoleKindFilter.Recount(everything.Skip(1), filters);
        Assert.Equal(1, filters.Single(f => f.Kind == ConsoleLineKind.Error).Count);

        ConsoleKindFilter.Recount(Array.Empty<ConsoleLine>(), filters);
        Assert.All(filters, f => Assert.Equal(0, f.Count));
    }

    // --- Something arriving while its switch is off ---

    [Fact]
    public void TurningACategoryBackOnClearsItsUnseenMark()
    {
        var filter = new ConsoleKindFilter(ConsoleLineKind.Error) { IsOn = false, HasUnseen = true };

        // Switching it on is the user looking at it. Leaving the mark would make it permanent.
        filter.IsOn = true;
        Assert.False(filter.HasUnseen);
    }

    // --- Colours ---

    [Fact]
    public void EveryKindHasAColourAndTheImportantOnesComeFromTheNotificationSettings()
    {
        var levels = new NotificationSettings
        {
            ColorError = "#AA0000", ColorWarning = "#BB0000", ColorInfo = "#CC0000"
        };

        // Red should mean the same thing in a toast and in the console; asking somebody to pick the
        // error colour twice is asking for an app that contradicts itself.
        Assert.Equal("#AA0000", ConsoleColors.HexFor(ConsoleLineKind.Error, levels, null, null));
        Assert.Equal("#BB0000", ConsoleColors.HexFor(ConsoleLineKind.Warn, levels, null, null));
        Assert.Equal("#CC0000", ConsoleColors.HexFor(ConsoleLineKind.Launcher, levels, null, null));

        foreach (var kind in Enum.GetValues<ConsoleLineKind>())
            Assert.True(NotificationPalette.IsValid(ConsoleColors.HexFor(kind, levels, null, null)),
                $"{kind} no tiene un color que se pueda dibujar");
    }

    [Fact]
    public void OrdinaryOutputStaysGrey()
    {
        // On purpose. It is the vast majority of every log, and colouring all of it would be exactly
        // as useful as colouring none of it.
        Assert.Equal(ConsoleColors.Info, ConsoleColors.HexFor(ConsoleLineKind.Info, null, null, null));
    }

    [Fact]
    public void ATypedColourThatCannotBeDrawnFallsBackInsteadOfBreaking()
    {
        Assert.Equal(ConsoleColors.DefaultChat,
            ConsoleColors.HexFor(ConsoleLineKind.Chat, null, "#nope", null));

        Assert.Equal(ConsoleColors.DefaultPlayers,
            ConsoleColors.HexFor(ConsoleLineKind.Players, null, null, "   "));
    }

    [Fact]
    public void TheTwoConsoleColoursAreTheOnesTheUserCanSet()
    {
        Assert.Equal("#123456", ConsoleColors.HexFor(ConsoleLineKind.Chat, null, "#123456", null));
        Assert.Equal("#654321", ConsoleColors.HexFor(ConsoleLineKind.Players, null, null, "#654321"));
    }

    // --- The names the view binds to ---

    [Fact]
    public void EveryNameTheConsoleBindsToExists()
    {
        // MainWindow.axaml is x:CompileBindings="False": a mistyped name raises nothing at all, and
        // the console would simply render blank rows.
        var xaml = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));

        var list = xaml.IndexOf("ItemsSource=\"{Binding ConsoleKinds}\"", StringComparison.Ordinal);
        Assert.True(list >= 0, "los interruptores de la consola ya no están en MainWindow.axaml");

        // From the item template on: the ItemsSource itself binds to the server's view model, and
        // everything inside the template binds to one switch.
        var start = xaml.IndexOf("<DataTemplate>", list, StringComparison.Ordinal);
        var end = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        var block = xaml[start..end];

        foreach (Match m in Regex.Matches(block, @"\{Binding ([A-Za-z0-9_]+)[,}]"))
        {
            var name = m.Groups[1].Value;
            Assert.True(
                typeof(ConsoleKindFilter).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null,
                $"ConsoleKindFilter no tiene ninguna propiedad «{name}», que MainWindow.axaml enlaza");
        }
    }

    [Fact]
    public void EveryCategoryHasANameInEveryLanguage()
    {
        // "Console_Kind" + kind is assembled at run time, so LocalizationTests cannot see these
        // seven: it only scans string literals.
        foreach (var kind in Enum.GetValues<ConsoleLineKind>())
        {
            var key = "Console_Kind" + kind;
            var label = new ConsoleKindFilter(kind).Label;

            Assert.NotEqual(key, label);   // Localizer.Get returns the key when it is missing
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }
}
