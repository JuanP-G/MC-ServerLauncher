using System.Xml.Linq;

namespace McServerLauncher.Tests;

/// <summary>
/// Catches two children of a multi-column grid landing in the same cell unconditionally.
/// </summary>
/// <remarks>
/// <para>
/// This exists because it happened, twice, in one change. A search-and-replace meant for one row
/// of the window stripped <c>Grid.Column</c> from every <c>TextBlock</c> in the file. The attribute
/// defaults to zero when it is missing, so nothing failed and nothing warned — the connected-player
/// count simply started drawing on top of the server's name, and the update banner's text on top of
/// its own icon.
/// </para>
/// <para>
/// Neither showed up in a screenshot: the player count is only visible while a server is running,
/// and the banner only while an update is waiting. Both were found by a person looking at the real
/// app, which is the most expensive way to find anything.
/// </para>
/// <para>
/// Omitting <c>Grid.Column</c> on a genuine column-zero child is normal and stays allowed. What is
/// flagged is <i>two</i> children sharing column zero of a grid that has several — layering like
/// that belongs in a <c>Panel</c>, and here it has always been an accident.
/// </para>
/// </remarks>
public class GridColumnTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NoTwoChildrenAreDrawnInTheSameCellByAccident()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher"),
                     "*.axaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            XDocument doc;
            try { doc = XDocument.Load(file, LoadOptions.SetLineInfo); }
            catch { continue; }   // App.axaml and the style dictionaries have no grids to check

            foreach (var grid in doc.Descendants(Avalonia + "Grid"))
            {
                // FILAS tambien, no solo columnas. Esta prueba solo miraba rejillas de varias
                // columnas, asi que una rejilla de UNA columna y seis filas —que es exactamente la
                // forma del detalle del servidor desde el reparto de dos pisos— podia perder un
                // Grid.Row y apilar dos cosas sin que nadie dijera nada. Se descubrio sabotenadola:
                // quitarle el Grid.Row al aviso de "sin tunel" no rompia ninguna prueba.
                var columns = ColumnCount(grid);
                var rows = RowCount(grid);
                if (columns < 2 && rows < 2) continue;

                // Property-element children (<Grid.ColumnDefinitions>, <Grid.Styles>) are not
                // laid out and must not be counted as things sitting in a column.
                var children = grid.Elements()
                    .Where(e => !e.Name.LocalName.Contains('.'))
                    // A Popup draws in its own window, not in the cell it is written in.
                    .Where(e => e.Name.LocalName != "Popup")
                    .ToList();

                // By CELL, not by column. The first version of this test keyed on the column
                // alone and reported five things that were perfectly fine — a settings dialog with
                // one label per row has every label in column zero, and that is what rows are for.
                // A test that cries wolf gets muted, which is worse than not having it.
                foreach (var cell in children.GroupBy(Cell)
                             .Where(g => g.Count() > 1)
                             // Sharing a cell on purpose is a real technique: a warning icon and a
                             // tick in the same slot, or two sections swapped by the rail. Every
                             // child saying when it is drawn is what distinguishes that from the
                             // accident — an overlap nobody meant has a child that is always drawn.
                             //
                             // "When it is drawn" is IsVisible or Opacity, not just IsVisible. The
                             // two sections cross-fade, which means they have to stay mounted and
                             // overlapping, so they say it with Opacity instead — and reading only
                             // IsVisible flagged the one overlap in the app that is most deliberate.
                             .Where(g => g.Any(c => c.Attribute("IsVisible") is null &&
                                                    c.Attribute("Opacity") is null)))
                    offenders.Add(
                        $"{name}: {cell.Count()} hijos en la celda fila {cell.Key.Row} " +
                        $"columna {cell.Key.Column} de una rejilla de {rows}x{columns} — " +
                        string.Join(", ", cell.Select(Describe)));
            }
        }

        Assert.True(offenders.Count == 0,
            "Hijos apilados en la misma columna (probablemente falta Grid.Column):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>How many columns a grid declares, however it declares them.</summary>
    private static int RowCount(XElement grid)
    {
        var shorthand = grid.Attribute("RowDefinitions")?.Value;
        if (!string.IsNullOrWhiteSpace(shorthand))
            return shorthand.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

        return grid.Element(Avalonia + "Grid.RowDefinitions")?
            .Elements(Avalonia + "RowDefinition").Count() ?? 1;
    }

    private static int ColumnCount(XElement grid)
    {
        var shorthand = grid.Attribute("ColumnDefinitions")?.Value;
        if (!string.IsNullOrWhiteSpace(shorthand))
            return shorthand.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

        return grid.Element(Avalonia + "Grid.ColumnDefinitions")?
            .Elements(Avalonia + "ColumnDefinition").Count() ?? 1;
    }

    /// <summary>Where a child sits. Both attributes default to zero when absent.</summary>
    private static (int Row, int Column) Cell(XElement e) =>
        (Number(e.Attribute("Grid.Row")?.Value), Number(e.Attribute("Grid.Column")?.Value));

    private static int Number(string? value) => int.TryParse(value, out var n) ? n : 0;

    private static string Describe(XElement e)
    {
        var line = (e as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;
        var text = e.Attribute("Text")?.Value
                ?? e.Attribute(Xaml + "Name")?.Value
                ?? e.Attribute("Classes")?.Value
                ?? string.Empty;
        return text.Length > 0 ? $"{e.Name.LocalName} «{text}» (línea {line})"
                               : $"{e.Name.LocalName} (línea {line})";
    }

    [Fact]
    public void ACrossplayServerShowsBothOfItsPorts()
    {
        // Java is TCP and Bedrock is UDP, and they are different numbers. Showing one of them under
        // a label that just says "Port" told people the wrong thing — and a Bedrock player has to
        // type their port by hand, so it is the one they most need to see.
        //
        // Asserted on the RELATIONSHIP and not on how many times a string appears. The first version
        // counted occurrences and broke the moment the header was restructured, without anything
        // actually being wrong: a test like that only teaches you to update the number.
        var doc = XDocument.Load(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));

        List<XElement> ShownWhen(string binding) => doc.Descendants()
            .Where(e => e.Attribute("IsVisible")?.Value == "{Binding " + binding + "}")
            .ToList();

        string Labels(IEnumerable<XElement> group) => string.Concat(group
            .SelectMany(e => e.DescendantsAndSelf())
            .Select(e => e.Attribute("Text")?.Value ?? string.Empty));

        var withoutCrossplay = ShownWhen("!IsCrossplayOn");
        var withCrossplay = ShownWhen("IsCrossplayOn");

        // Java-only: the single port, and no sign of the Bedrock one.
        var solo = Labels(withoutCrossplay);
        Assert.Contains("{Binding PortText}", solo, StringComparison.Ordinal);
        Assert.DoesNotContain("BedrockLocalPortText", solo, StringComparison.Ordinal);

        // Crossplay: BOTH numbers, and each from its own binding. This is the assertion that
        // matters and the one the original bug needed: binding both to PortText would print the
        // Java port twice and nobody would notice, because two identical numbers look deliberate.
        //
        // It no longer asks for the labels "Port_Java" and "Port_Bedrock". The two ports are now
        // one piece — "Puertos 20005 / 19133" — because they are one fact and, measured, seven
        // separate pieces did not fit in a row. Asserting the old labels would have been asserting
        // a layout decision, which is exactly what broke this test the last time.
        var crossplay = Labels(withCrossplay);
        Assert.Contains("{Binding PortText}", crossplay, StringComparison.Ordinal);
        Assert.Contains("{Binding BedrockLocalPortText}", crossplay, StringComparison.Ordinal);
    }

}
