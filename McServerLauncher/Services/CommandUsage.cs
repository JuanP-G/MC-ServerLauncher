namespace McServerLauncher.Services;

/// <summary>One word of a command's usage line, as the server's <c>help</c> prints it.</summary>
/// <param name="Literals">The words that can be typed here as they are (<c>set</c>, <c>creative</c>).</param>
/// <param name="Args">The names of the values that can go here (<c>targets</c> of <c>&lt;targets&gt;</c>).</param>
/// <param name="Optional">Written in square brackets: the line is complete without it.</param>
/// <param name="Text">The word as printed, for the hint under the box.</param>
public sealed record UsageToken(IReadOnlyList<string> Literals, IReadOnlyList<string> Args, bool Optional, string Text);

/// <summary>
/// Reads Minecraft's usage notation: <c>/gamemode &lt;gamemode&gt; [&lt;target&gt;]</c>,
/// <c>/time (set|add|query)</c>, <c>/difficulty [peaceful|easy|normal|hard]</c>.
/// </summary>
/// <remarks>
/// This is Brigadier's "smart usage", what <c>help</c> prints on vanilla and on every server built
/// on it (Fabric, Forge, NeoForge, Paper's vanilla commands). Angle brackets are a value, round
/// brackets a choice, square brackets something optional, and anything else a word typed as is.
/// </remarks>
public static class CommandUsage
{
    /// <summary>The words of <paramref name="usage"/> after the command name (which may be in it, with or without a slash).</summary>
    public static IReadOnlyList<UsageToken> Parse(string usage, bool includesName = true)
    {
        var words = SplitTopLevel(usage.Trim());
        if (includesName && words.Count > 0) words.RemoveAt(0);
        return words.Select(ParseWord).ToList();
    }

    private static UsageToken ParseWord(string word)
    {
        var optional = word.Length >= 2 && word[0] == '[' && word[^1] == ']';
        var inner = optional ? word[1..^1] : word;
        if (inner.Length >= 2 && inner[0] == '(' && inner[^1] == ')') inner = inner[1..^1];

        var literals = new List<string>();
        var args = new List<string>();
        foreach (var choice in SplitChoices(inner))
        {
            var c = choice.Trim();
            if (c.Length == 0) continue;
            if (c.Length >= 2 && c[0] == '<' && c[^1] == '>') args.Add(c[1..^1]);
            else if (c.IndexOfAny(['<', '(', '[']) < 0) literals.Add(c);
            else args.Add(c);   // something nested this does not need to understand
        }
        return new UsageToken(literals, args, optional, word);
    }

    /// <summary>Splits on spaces that are not inside any bracket.</summary>
    internal static List<string> SplitTopLevel(string text)
    {
        var words = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '(' or '[' or '<') depth++;
            else if (ch is ')' or ']' or '>') depth = Math.Max(0, depth - 1);
            else if (ch == ' ' && depth == 0)
            {
                if (i > start) words.Add(text[start..i]);
                start = i + 1;
            }
        }
        if (start < text.Length) words.Add(text[start..]);
        return words;
    }

    private static IEnumerable<string> SplitChoices(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '(' or '[' or '<') depth++;
            else if (ch is ')' or ']' or '>') depth = Math.Max(0, depth - 1);
            else if (ch == '|' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }
}
