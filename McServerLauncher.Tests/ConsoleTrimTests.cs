using Avalonia.Controls;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// What happens to a person's selection when the console trims itself.
/// </summary>
/// <remarks>
/// The console caps at 2000 lines and drops the oldest in blocks. That is invisible bookkeeping —
/// except that it used to rebuild the bound list, and a rebuilt list is a list whose selection is
/// gone. Somebody reading a stack trace, having selected the line they wanted to copy, would watch
/// it deselect itself for no reason they could see, every couple of hundred lines a busy server
/// printed.
///
/// Driven through a real <see cref="ListBox"/> rather than asserting on the collection, because the
/// collection was never the thing that was broken: it is the control's reaction to the notification
/// that differs, and only a control can be asked about that.
/// </remarks>
[Collection("avalonia")]
public class ConsoleTrimTests(AvaloniaFixture avalonia)
{
    private static (ListBox List, BulkObservableCollection<string> Items) Bound()
    {
        var items = new BulkObservableCollection<string>();
        for (var i = 0; i < 40; i++) items.Add("linea " + i);

        var list = new ListBox { ItemsSource = items, SelectionMode = SelectionMode.Multiple };
        new Window { Content = list }.Show();
        AvaloniaFixture.Pump();

        return (list, items);
    }

    [Fact]
    public void TrimmingTheTopLeavesTheSelectionAlone()
    {
        avalonia.Run(() =>
        {
            var (list, items) = Bound();

            list.SelectedItem = "linea 30";
            AvaloniaFixture.Pump();

            var antes = list.ContainerFromIndex(20);

            items.RemoveFromStartKeepingSelection(10);
            AvaloniaFixture.Pump();

            // The same line, still selected, and — the part that matters — still drawn by the SAME
            // container. Nothing was rebuilt, so nothing flickers and nothing re-animates.
            Assert.Equal("linea 30", list.SelectedItem);
            Assert.Equal(30, items.Count);
            Assert.Equal("linea 10", items[0]);
            Assert.Same(antes, list.ContainerFromIndex(10));
        });
    }

    [Fact]
    public void ReplacingTheWholeListIsWhatLosesIt()
    {
        // The other half of the pair, and it measures the thing that actually differs.
        //
        // Two earlier versions of this test asserted the wrong thing, and both were guesses: first
        // that a Reset clears the selection, then that it moves it somewhere else. Neither is true —
        // Avalonia re-resolves the selected item by value and it survives. What does NOT survive is
        // the containers: a Reset throws every one away and builds new ones, which is the flicker,
        // and is what makes a per-item animation replay on lines that never changed.
        avalonia.Run(() =>
        {
            var (list, items) = Bound();

            var antes = list.ContainerFromIndex(20);
            Assert.NotNull(antes);

            items.ReplaceAll(items.Skip(10).ToList());
            AvaloniaFixture.Pump();

            // Same line, different container: it was destroyed and rebuilt for nothing.
            Assert.NotSame(antes, list.ContainerFromIndex(10));
        });
    }

    [Fact]
    public void TheConsoleTrimActuallyUsesIt()
    {
        // Found by sabotage: the two tests above prove the mechanism works, and both kept passing
        // with the console switched back to rebuilding itself. They test a collection; nothing was
        // testing that the console reaches for it.
        //
        // Read from the source because the alternative is driving a real ServerViewModel past 2200
        // lines through a private method — a lot of machinery to assert one call. What matters is
        // that the automatic trim never rebuilds: every other caller of RebuildVisibleConsole is a
        // deliberate user action (typing a filter, toggling a category), where a Reset is honest.
        var source = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "ViewModels", "ServerViewModel.cs"));

        var start = source.IndexOf("if (ConsoleLines.Count > MaxConsoleLines", StringComparison.Ordinal);
        Assert.True(start > 0, "No encuentro el bloque que recorta la consola.");

        var end = source.IndexOf("TrackPlayers", start, StringComparison.Ordinal);
        Assert.True(end > start, "No encuentro el final del bloque que recorta la consola.");

        // Sin comentarios. La primera version fallaba contra el comentario que hay dentro del
        // bloque, que dice literalmente "NOT RebuildVisibleConsole()" para explicar por que no se
        // llama: una prueba que lee texto tiene que leer codigo, o acaba discutiendo con la prosa
        // que la explica.
        var trim = string.Join("\n", source[start..end]
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("RemoveFromStartKeepingSelection", trim, StringComparison.Ordinal);
        Assert.DoesNotContain("RebuildVisibleConsole", trim, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingNothingIsNotAnEvent()
    {
        // The trim asks for zero whenever no visible line fell off the top — which is the common
        // case with a filter on. Raising a Remove of nothing would be a notification that says
        // nothing happened, and a ListBox is entitled to react to it.
        avalonia.Run(() =>
        {
            var (list, items) = Bound();

            list.SelectedItem = "linea 5";
            AvaloniaFixture.Pump();

            items.RemoveFromStartKeepingSelection(0);
            items.RemoveFromStartKeepingSelection(-3);
            AvaloniaFixture.Pump();

            Assert.Equal("linea 5", list.SelectedItem);
            Assert.Equal(40, items.Count);
        });
    }
}
