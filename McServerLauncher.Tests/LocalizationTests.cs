using System.Xml.Linq;

namespace McServerLauncher.Tests;

/// <summary>
/// The five .resx files, checked against each other.
/// </summary>
/// <remarks>
/// This has to read the XML rather than ask <c>Localizer</c>, and the reason is the trap it exists
/// to catch: <see cref="System.Resources.ResourceManager"/> falls back to the neutral resource when
/// a key is missing from a language, so a forgotten German string would quietly come out in Spanish
/// and every runtime lookup would "pass". Only the files themselves show the gap.
/// </remarks>
public class LocalizationTests
{
    private static readonly string[] Files =
    {
        "Strings.resx", "Strings.en.resx", "Strings.pt.resx", "Strings.fr.resx", "Strings.de.resx"
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McServerLauncher.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Dictionary<string, string> Load(string file)
    {
        var path = Path.Combine(RepoRoot(), "McServerLauncher", "Resources", file);
        var doc = XDocument.Load(path);

        var entries = new Dictionary<string, string>();
        foreach (var data in doc.Root!.Elements("data"))
        {
            var name = data.Attribute("name")?.Value;
            if (name is null) continue;

            // A duplicate key compiles fine and then one of the two silently wins.
            Assert.False(entries.ContainsKey(name), $"clave duplicada '{name}' en {file}");
            entries[name] = data.Element("value")?.Value ?? string.Empty;
        }
        return entries;
    }

    [Fact]
    public void EveryLanguageHasExactlyTheSameKeys()
    {
        var neutral = Load("Strings.resx");

        foreach (var file in Files.Skip(1))
        {
            var other = Load(file);

            var missing = neutral.Keys.Except(other.Keys).OrderBy(k => k).ToList();
            var extra = other.Keys.Except(neutral.Keys).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0, $"{file}: faltan {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{file}: sobran {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void NoValueIsLeftEmpty()
    {
        foreach (var file in Files)
            foreach (var (key, value) in Load(file))
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: '{key}' está vacía");
    }

    [Fact]
    public void PlaceholdersSurviveTranslation()
    {
        // string.Format throws on a missing argument and prints the raw brace on a stray one, so a
        // translator dropping a {0} turns into a crash or a nonsense message at exactly the wrong
        // moment — these strings are the ones shown when something has already gone wrong.
        var neutral = Load("Strings.resx");

        foreach (var file in Files.Skip(1))
        {
            var other = Load(file);
            foreach (var (key, value) in neutral)
            {
                if (!other.TryGetValue(key, out var translated)) continue;

                foreach (var token in new[] { "{0}", "{1}", "{2}" })
                    Assert.True(value.Contains(token) == translated.Contains(token),
                        $"{file}: '{key}' no coincide en {token}");
            }
        }
    }
}
