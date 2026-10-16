namespace McServerLauncher.Services;

/// <summary>What a Minecraft Java player name looks like, in one place.</summary>
/// <remarks>
/// Letters, digits and underscore, one to sixteen of them. Five classes each kept their own regex for
/// this; they agreed, but the next one to change would have to find the other four. The rule matters
/// beyond looks: it is what stops a name from carrying a line break into a console command, and from
/// ever becoming part of a path.
/// </remarks>
public static class PlayerNames
{
    /// <summary>The rule as a regex fragment, for patterns that pick a name out of a line.</summary>
    public const string Pattern = "[A-Za-z0-9_]{1,16}";

    /// <summary>Whether <paramref name="name"/> is, exactly, a name Minecraft would accept.</summary>
    /// <remarks>
    /// Not a regex: <c>$</c> also matches before a final line break, so "Alice\n" passed.
    /// </remarks>
    public static bool IsValid(string? name) =>
        name is { Length: >= 1 and <= 16 } && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
            or (>= '0' and <= '9') or '_');
}
