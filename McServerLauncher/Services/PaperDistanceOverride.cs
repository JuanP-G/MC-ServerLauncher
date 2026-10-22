using System.IO;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Whether Spigot's own configuration overrides the distances in <c>server.properties</c>.
/// </summary>
/// <remarks>
/// Paper and Purpur read <c>view-distance</c> and <c>simulation-distance</c> from
/// <c>server.properties</c>, unless <c>spigot.yml</c> gives a world a number of its own; then the
/// properties file is ignored for that world and lowering it would change nothing. The page says so
/// instead of offering a change that does nothing.
/// </remarks>
public static class PaperDistanceOverride
{
    /// <summary>Which distances <c>spigot.yml</c> sets to a number.</summary>
    public static (bool View, bool Simulation) Read(string serverFolder)
    {
        try
        {
            var path = Path.Combine(serverFolder, "spigot.yml");
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : (false, false);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <summary>The same, from the file's text.</summary>
    public static (bool View, bool Simulation) Parse(string spigotYml) =>
        (IsSet(spigotYml, "view-distance"), IsSet(spigotYml, "simulation-distance"));

    private static bool IsSet(string yml, string key)
    {
        foreach (Match m in Entry(key).Matches(yml))
        {
            var value = m.Groups["v"].Value.Trim().Trim('\'', '"');
            if (value.Length > 0 && !value.Equals("default", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static Regex Entry(string key) =>
        new($@"^\s+{Regex.Escape(key)}:\s*(?<v>[^#\r\n]*)", RegexOptions.Multiline);
}
