using System.Text.RegularExpressions;
using McServerLauncher.Behaviors;

namespace McServerLauncher.Tests;

/// <summary>
/// One monospaced font stack for the whole app. It had spread to a dozen places in three spellings,
/// and the code blocks of a mod's description did not get the font the console did.
/// </summary>
public class MonoFontTests
{
    private static string App(string relative) => Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", relative);

    private static IEnumerable<string> Sources(string pattern) =>
        Directory.EnumerateFiles(App(""), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void TheResourceInAppAxamlIsTheSameStackAsTheOneInCode()
    {
        var xaml = File.ReadAllText(App("App.axaml"));
        var match = Regex.Match(xaml, "<FontFamily x:Key=\"MonoFont\">([^<]+)</FontFamily>");

        Assert.True(match.Success, "App.axaml ya no define el recurso MonoFont");
        Assert.Equal(MonoFont.Stack, match.Groups[1].Value);
    }

    [Fact]
    public void NoOtherFileSpellsTheStackOut()
    {
        var offenders = Sources("*.axaml").Concat(Sources("*.cs"))
            .Where(f => Path.GetFileName(f) is not ("App.axaml" or "MonoFont.cs"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "Consolas|Cascadia Mono", RegexOptions.None)
                        && File.ReadLines(f).Any(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                                      && !l.TrimStart().StartsWith("<!--", StringComparison.Ordinal)
                                                      && Regex.IsMatch(l, "Consolas|Cascadia Mono")))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Usa el recurso MonoFont (o MonoFont.Family en código): " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheStackEndsOnTheGenericFamilySoEveryPlatformGetsSomething() =>
        Assert.EndsWith("monospace", MonoFont.Stack);
}
