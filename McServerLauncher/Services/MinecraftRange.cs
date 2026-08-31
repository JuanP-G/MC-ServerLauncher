using System;
using System.Collections.Generic;
using System.Linq;

namespace McServerLauncher.Services;

/// <summary>
/// Does this Minecraft version satisfy the range a mod declares?
/// </summary>
/// <remarks>
/// <para>
/// Fabric mods state their requirement in <c>fabric.mod.json</c> as <c>"minecraft": "&gt;=26.2"</c>
/// or similar. What appears there is handled: a wildcard, a comparison, an exact version, a
/// component wildcard like <c>1.21.x</c>, several alternatives separated by <c>||</c> or commas,
/// and several conditions separated by spaces. Anything else is treated as unsatisfied rather than
/// guessed at — refusing to install is recoverable, installing a mod the server cannot load is a
/// silent failure the user has to diagnose.
/// </para>
/// <para>
/// The two separators mean opposite things and used to be treated as one. A space is
/// <strong>and</strong>: <c>"&gt;=1.21 &lt;1.22"</c> is a window, and splitting it into
/// alternatives made 1.22 satisfy a range that excludes it. <c>||</c> and a comma are
/// <strong>or</strong>. And an operator may be written apart from its version — <c>"&gt;= 1.21"</c>
/// — which the old split turned into two fragments, neither of which satisfied anything.
/// </para>
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
        var alternatives = range.Split(new[] { "||", "," },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return alternatives.Any(alternative => SatisfiesAll(actual, alternative));
    }

    /// <summary>Every condition in one alternative, all of which have to hold.</summary>
    private static bool SatisfiesAll(Version actual, string alternative)
    {
        var conditions = Conditions(alternative);
        return conditions.Count > 0 && conditions.All(c => SatisfiesOne(actual, c));
    }

    /// <summary>
    /// Splits an alternative into conditions, rejoining an operator that was written apart from its
    /// version so that "&gt;= 1.21" stays one condition rather than becoming two useless fragments.
    /// </summary>
    private static List<string> Conditions(string alternative)
    {
        var tokens = alternative.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var conditions = new List<string>(tokens.Length);

        for (var i = 0; i < tokens.Length; i++)
        {
            if (Operators.Contains(tokens[i]) && i + 1 < tokens.Length)
            {
                conditions.Add(tokens[i] + tokens[i + 1]);
                i++;
            }
            else
            {
                conditions.Add(tokens[i]);
            }
        }

        return conditions;
    }

    /// <summary>The operators that can appear on their own, separated from the version.</summary>
    private static readonly string[] Operators = { ">=", "<=", ">", "<", "=", "~", "^" };

    private static bool SatisfiesOne(Version actual, string part)
    {
        if (part is "*" or "x" or "X" or "") return true;

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

        rest = rest.Trim();

        // "1.21.x" is an exact match on the components before the wildcard. Only meaningful for
        // equality: ">=1.21.x" is not something anyone writes, and reading it as ">=1.21" is the
        // closest honest answer.
        var wildcard = WildcardPrefix(rest);
        if (wildcard is not null)
            return op == "=" ? StartsWithComponents(actual, wildcard) : SatisfiesOne(actual, op + wildcard);

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

    /// <summary>The part before a component wildcard ("1.21.x" -&gt; "1.21"), or null if there is none.</summary>
    private static string? WildcardPrefix(string text)
    {
        var parts = text.Split('.');
        var at = Array.FindIndex(parts, p => p is "x" or "X" or "*");
        if (at < 0) return null;
        if (at == 0) return string.Empty;

        return string.Join('.', parts.Take(at));
    }

    /// <summary>Whether <paramref name="actual"/> begins with the components of <paramref name="prefix"/>.</summary>
    private static bool StartsWithComponents(Version actual, string prefix)
    {
        if (prefix.Length == 0) return true;                       // "x" on its own is any version
        if (Parse(prefix) is not { } required) return false;

        var wanted = prefix.Split('.').Length;
        var actualParts = new[] { actual.Major, actual.Minor, actual.Build, actual.Revision };
        var requiredParts = new[] { required.Major, required.Minor, required.Build, required.Revision };

        for (var i = 0; i < wanted && i < actualParts.Length; i++)
            if (actualParts[i] != requiredParts[i])
                return false;

        return true;
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
