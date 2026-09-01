using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace McServerLauncher.Tests;

/// <summary>
/// Keeps the colours in one place once they have been put there.
/// </summary>
/// <remarks>
/// <para>
/// There were 50 colours written by hand across the views, and the repeats are what gave the game
/// away: <c>#22FFFFFF</c> appeared ten times and <c>#15FFFFFF</c> six. Nothing had gone wrong yet —
/// the problem is that changing one meant finding all ten, and adding an eleventh shade was always
/// easier than hunting for the right existing one. That is how a palette stops meaning anything.
/// </para>
/// <para>
/// Moving them was the easy half. This is the half that makes it stay moved: without a test, the
/// next hurried fix writes <c>#23FFFFFF</c> inline and nothing says a word.
/// </para>
/// </remarks>
public class ColourTokenTests
{
    /// <summary>Colours that stay written out, each for a reason that is not "we forgot".</summary>
    /// <remarks>
    /// <para>
    /// <b>ServerModsView</b> keeps the solid tile of the results list. It is not the translucent
    /// card the rest of the app uses; it used to be called <c>Border.card</c> too, which is exactly
    /// why it now has its own name.
    /// </para>
    /// <para>
    /// <b>MainWindow</b> keeps the console chip colours, which come from
    /// <c>ConsoleColors</c> — a setting the user edits, not a token of the theme.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> Allowed = new()
    {
        "#1A1A1A",   // ServerModsView: the solid tile of a result row
        "#1AFFFFFF", // ServerModsView: that tile's border
        "#40FFFFFF", // ServerModsView: that tile's border while hovered
    };

    /// <summary>Attributes and setters that carry a colour.</summary>
    private static readonly Regex ColourAttribute = new(
        @"(?:Background|BorderBrush|Foreground|Fill|Stroke)=""(#[0-9A-Fa-f]{3,8})""|" +
        @"Property=""(?:Background|BorderBrush|Foreground|Fill|Stroke)""\s+Value=""(#[0-9A-Fa-f]{3,8})""",
        RegexOptions.Compiled);

    private static IEnumerable<string> ViewFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher"), "*.axaml",
            SearchOption.AllDirectories);

    [Fact]
    public void NoViewWritesAColourByHand()
    {
        // Matched against attributes rather than raw text on purpose: a comment is allowed to quote
        // a colour it is explaining, and one of them does exactly that.
        var offenders = new List<string>();

        foreach (var file in ViewFiles())
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in ColourAttribute.Matches(lines[i]))
                {
                    var colour = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (!Allowed.Contains(colour))
                        offenders.Add($"{name}:{i + 1}  {colour}");
                }
        }

        Assert.True(offenders.Count == 0,
            "Estos colores van escritos a mano. Usa una ficha de App.axaml, o añádelo a Allowed " +
            "con el motivo:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryTokenTheViewsAskForIsActuallyDefined()
    {
        // A {StaticResource} naming a key that does not exist throws when the style is applied, not
        // when the project builds — so a typo here ships and only shows up on the screen it breaks.
        var app = XDocument.Load(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "App.axaml"));

        var defined = app.Descendants()
            .Select(e => e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(a => a is not null)
            .Select(a => a!.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(defined);

        var used = new Regex(@"\{StaticResource\s+([A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);
        var missing = new List<string>();

        foreach (var file in ViewFiles())
        {
            if (Path.GetFileName(file) == "App.axaml") continue;
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in used.Matches(lines[i]))
                    if (!defined.Contains(m.Groups[1].Value))
                        missing.Add($"{Path.GetFileName(file)}:{i + 1}  {m.Groups[1].Value}");
        }

        Assert.True(missing.Count == 0,
            "Estas vistas piden una ficha que no existe en App.axaml:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void TheCardStyleLivesInExactlyOnePlace()
    {
        // Border.card used to mean two different things: the translucent card in
        // ServerConfigDialog, and a solid dark tile in ServerModsView. Each lived inside its own
        // view, so Avalonia kept them apart and nobody noticed. The moment the shared ones moved to
        // Styles/Shared.axaml they would have collided in silence — same name, different look, no
        // error. The tile is Border.tile now, and this is what stops the name being reused.
        var declarations = new List<string>();

        foreach (var file in ViewFiles())
        {
            var name = Path.GetFileName(file);
            if (name == "Shared.axaml") continue;
            if (File.ReadAllText(file).Contains("Selector=\"Border.card"))
                declarations.Add(name);
        }

        Assert.True(declarations.Count == 0,
            "Border.card se define en Styles/Shared.axaml y en ningún otro sitio. Si necesitas otro " +
            "aspecto, ponle otro nombre — que es de donde venía el problema:\n  " +
            string.Join("\n  ", declarations));
    }
}
