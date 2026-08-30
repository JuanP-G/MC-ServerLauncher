using System.Linq;

namespace McServerLauncher.Services;

/// <summary>
/// Does this Minecraft version satisfy the range a mod declares?
/// </summary>
/// <remarks>
/// Fabric mods state their requirement in <c>fabric.mod.json</c> as <c>"minecraft": "&gt;=26.2"</c>
/// or similar. Only what actually appears there is handled: a wildcard, a comparison, an exact
/// version, or several alternatives separated by spaces or commas. Anything else is treated as
/// unsatisfied rather than guessed at — refusing to install is recoverable, installing a mod the
/// server cannot load is a silent failure the user has to diagnose.
/// </remarks>
public static class MinecraftRange
{
    /// <summary>Whether <paramref name="mcVersion"/> is inside <paramref name="range"/>.</summary>
    public static bool Satisfies(string? mcVersion, string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return true;   // no requirement stated
        if (string.IsNullOrWhiteSpace(mcVersion)) return false;
        if (Parse(mcVersion) is not { } actual) return false;

        // "1.21 || 1.21.1" and "1.21, 1.21.1" both appear in the wild: any alternative passing wins.
        var alternatives = range.Split(new[] { "||", ",", " " },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return alternatives.Any(part => SatisfiesOne(actual, part));
    }

    private static bool SatisfiesOne(Version actual, string part)
    {
        if (part is "*" or "") return true;

        var (op, rest) = part switch
        {
            _ when part.StartsWith(">=") => (">=", part[2..]),
            _ when part.StartsWith("<=") => ("<=", part[2..]),
            _ when part.StartsWith('>') => (">", part[1..]),
            _ when part.StartsWith('<') => ("<", part[1..]),
            _ when part.StartsWith('=') => ("=", part[1..]),
            // "~1.21" and "^1.21" mean "compatible with": for Minecraft that is close enough to
            // ">=", and being generous here only risks a mod the server then reports as incompatible.
            _ when part.StartsWith('~') || part.StartsWith('^') => (">=", part[1..]),
            _ => ("=", part)
        };

        if (Parse(rest) is not { } required) return false;

        return op switch
        {
            ">=" => actual >= required,
            ">" => actual > required,
            "<=" => actual <= required,
            "<" => actual < required,
            _ => actual == required
        };
    }

    /// <summary>
    /// A Minecraft version as something comparable, or null when it is not a plain number series.
    /// </summary>
    /// <remarks>
    /// Snapshots ("26w05a") and pre-releases have no ordering that <see cref="Version"/> can express,
    /// and returning null for them makes the caller refuse rather than compare nonsense.
    /// </remarks>
    private static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 4) return null;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i])) return null;

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }
}
