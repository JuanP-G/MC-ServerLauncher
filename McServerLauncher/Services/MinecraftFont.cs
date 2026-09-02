namespace McServerLauncher.Services;

/// <summary>
/// How wide a sign will be drawn in Minecraft's own font, in its own pixels.
/// </summary>
/// <remarks>
/// <para>
/// The point of this is to warn before a line is cut off in the server list, and a character count
/// cannot do that: Minecraft's font is variable-width, so <c>llllllllll</c> and <c>MMMMMMMMMM</c> are
/// the same ten characters and nowhere near the same width. Counting characters would cry wolf on
/// one and stay silent on the other.
/// </para>
/// <para>
/// The numbers are the font's real advances, not an estimate: nearly every glyph is 6 px wide (5 of
/// ink plus 1 of spacing) and the exceptions below are the ones that are not. Bold adds one pixel
/// per glyph because the game draws it twice, offset by one.
/// </para>
/// <para>
/// It is still an approximation of what a player sees — a resource pack, a non-Latin script or a
/// different GUI scale all change the answer — so the warning that uses it says "se puede cortar",
/// not "se corta". Promising precision we do not have would be worse than not warning at all.
/// </para>
/// </remarks>
public static class MinecraftFont
{
    /// <summary>
    /// Room for the description in the multiplayer list, in font pixels.
    /// </summary>
    /// <remarks>
    /// The entry is 305 px wide with a 32 px icon and padding on both sides; what is left for the
    /// text is about this. Past it the client trims with an ellipsis.
    /// </remarks>
    public const int ListWidth = 270;

    /// <summary>Everything not in <see cref="Narrow"/>.</summary>
    private const int Default = 6;

    private static readonly Dictionary<char, int> Narrow = new()
    {
        ['!'] = 2, [','] = 2, ['.'] = 2, [':'] = 2, [';'] = 2, ['i'] = 2, ['|'] = 2,
        ['\''] = 3, ['l'] = 3, ['`'] = 3,
        [' '] = 4, ['['] = 4, [']'] = 4, ['t'] = 4, ['I'] = 4,
        ['"'] = 5, ['('] = 5, [')'] = 5, ['*'] = 5, ['<'] = 5, ['>'] = 5,
        ['{'] = 5, ['}'] = 5, ['f'] = 5, ['k'] = 5,
    };

    /// <summary>Width of one stretch of text, in font pixels.</summary>
    public static int Width(string text, bool bold)
    {
        var total = 0;
        foreach (var c in text)
            total += (Narrow.TryGetValue(c, out var w) ? w : Default) + (bold ? 1 : 0);

        return total;
    }

    /// <summary>
    /// Width of every line of a sign, so each can be judged on its own.
    /// </summary>
    /// <remarks>
    /// Per line and not per sign: the list wraps nothing, it trims, so a short first line does not
    /// buy the second one any room.
    /// </remarks>
    public static IReadOnlyList<int> LineWidths(IEnumerable<MotdRun> runs)
    {
        var widths = new List<int> { 0 };

        foreach (var run in runs)
        {
            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) widths.Add(0);
                widths[^1] += Width(parts[i], run.Bold);
            }
        }

        return widths;
    }

    /// <summary>The widest line, which is the one that decides whether to warn.</summary>
    public static int WidestLine(IEnumerable<MotdRun> runs) => LineWidths(runs).Max();
}
