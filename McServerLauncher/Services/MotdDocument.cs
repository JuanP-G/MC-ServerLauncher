using System.Text;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>A stretch of the sign that is all one colour and style.</summary>
/// <param name="Text">Plain text. Never contains a formatting code.</param>
/// <param name="Colour">A Minecraft colour code (<c>0</c>-<c>9</c>, <c>a</c>-<c>f</c>), or null for the default grey.</param>
/// <param name="Bold">§l.</param>
/// <param name="Italic">§o.</param>
/// <param name="Underline">§n.</param>
/// <param name="Strike">§m.</param>
public record MotdRun(
    string Text,
    char? Colour = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strike = false)
{
    /// <summary>Same look, different words. Used when a run is split at an edit boundary.</summary>
    public MotdRun With(string text) => this with { Text = text };

    /// <summary>Whether two runs look identical and can therefore be merged.</summary>
    public bool LooksLike(MotdRun other) =>
        Colour == other.Colour && Bold == other.Bold && Italic == other.Italic &&
        Underline == other.Underline && Strike == other.Strike;
}

/// <summary>Which of the four marks a toolbar button toggles.</summary>
public enum MotdStyle { Bold, Italic, Underline, Strike }

/// <summary>
/// The server's sign, as runs of styled text — and back again as <c>§</c> codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The code string is the only original.</b> That one decision is what makes the editor cheap:
/// there are not two models to keep in step, there is one, and the two boxes in the dialog are two
/// views of it. Pasting somebody else's sign works with no special handling because the lower box
/// simply <i>is</i> the original.
/// </para>
/// <para>
/// No UI here on purpose. Editing in the middle of a coloured stretch, deleting across the join
/// between two, pasting — those are where this sort of thing actually breaks, and they can only be
/// tested properly while they are plain functions over plain data.
/// </para>
/// <para>
/// <see cref="Behaviors.MinecraftMotd"/> renders from this too, so the half that paints and the
/// half that edits cannot come to read a sign differently.
/// </para>
/// </remarks>
public static partial class MotdDocument
{
    /// <summary>The colour codes Minecraft accepts. Anything else is a style or is ignored.</summary>
    private const string ColourCodes = "0123456789abcdef";

    /// <summary>What the client shows where no colour was asked for.</summary>
    public const string DefaultColour = "#AAAAAA";

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscape();

    /// <summary>Turns what is stored in <c>server.properties</c> into real characters.</summary>
    /// <remarks>
    /// The file is line-oriented, so a real newline cannot be stored in it — a two-line sign is
    /// written as a backslash and an <c>n</c>. Java properties also allow <c>\uXXXX</c>.
    /// </remarks>
    public static string Unescape(string value)
    {
        var text = value.Replace("\\n", "\n");
        return UnicodeEscape().Replace(text, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    /// <summary>The inverse, for writing back.</summary>
    public static string Escape(string value) => value.Replace("\r\n", "\n").Replace("\n", "\\n");

    /// <summary>
    /// Reads a sign into runs.
    /// </summary>
    /// <remarks>
    /// Mirrors what the game does, including the parts that look like quirks: a colour code clears
    /// bold and the rest, <c>§r</c> clears everything, <c>§k</c> (obfuscated) is accepted and then
    /// ignored because there is nothing sensible to draw, and an unknown code is swallowed rather
    /// than printed. <c>&amp;</c> is accepted as well as <c>§</c> because half the internet writes
    /// signs that way.
    /// </remarks>
    public static IReadOnlyList<MotdRun> Parse(string? code)
    {
        var runs = new List<MotdRun>();
        var text = Unescape(code ?? string.Empty);
        if (text.Length == 0) return runs;

        var current = new MotdRun(string.Empty);
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            runs.Add(current.With(sb.ToString()));
            sb.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '§' || c == '&') && i + 1 < text.Length)
            {
                var code2 = char.ToLowerInvariant(text[++i]);
                if (ColourCodes.Contains(code2))
                {
                    Flush();
                    current = new MotdRun(string.Empty, code2);
                    continue;
                }

                switch (code2)
                {
                    case 'l': Flush(); current = current with { Bold = true }; break;
                    case 'o': Flush(); current = current with { Italic = true }; break;
                    case 'n': Flush(); current = current with { Underline = true }; break;
                    case 'm': Flush(); current = current with { Strike = true }; break;
                    case 'r': Flush(); current = new MotdRun(string.Empty); break;
                    default: break;   // 'k' and anything unknown: swallowed, exactly as the game does
                }
                continue;
            }

            sb.Append(c);
        }

        Flush();
        return runs;
    }

    /// <summary>Writes runs back out as <c>§</c> codes, with nothing redundant.</summary>
    /// <remarks>
    /// A colour code resets the styles in Minecraft, so the colour is always written first and the
    /// marks after it. Runs that look the same as the one before emit no codes at all, which keeps
    /// a hand-written sign from growing a little every time it is opened and saved.
    /// </remarks>
    public static string ToCode(IEnumerable<MotdRun> runs)
    {
        var sb = new StringBuilder();
        MotdRun? previous = null;

        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;

            if (previous is null || !run.LooksLike(previous))
            {
                // Going back to the default, or dropping a mark, needs a reset: there is no code
                // for "stop being bold".
                var needsReset = run.Colour is null ||
                    (previous is not null && (
                        (previous.Bold && !run.Bold) || (previous.Italic && !run.Italic) ||
                        (previous.Underline && !run.Underline) || (previous.Strike && !run.Strike)));

                if (needsReset && previous is not null) sb.Append("§r");
                if (run.Colour is { } colour) sb.Append('§').Append(colour);
                if (run.Bold) sb.Append("§l");
                if (run.Italic) sb.Append("§o");
                if (run.Underline) sb.Append("§n");
                if (run.Strike) sb.Append("§m");
            }

            sb.Append(run.Text);
            previous = run;
        }

        return sb.ToString();
    }

    /// <summary>The words with no codes at all — what the upper box shows.</summary>
    public static string PlainText(IEnumerable<MotdRun> runs) =>
        string.Concat(runs.Select(r => r.Text));

    /// <summary>
    /// Applies a change to the stretch <paramref name="start"/>..<paramref name="length"/> of the
    /// plain text.
    /// </summary>
    /// <remarks>
    /// Offsets are into the plain text, because that is what the person selected — they never see
    /// the codes, so an offset into the coded string would be an offset into something they cannot
    /// point at. Runs are split at the ends of the selection and merged again afterwards, so
    /// styling half a run and then styling it back leaves the sign exactly as it was.
    /// </remarks>
    public static IReadOnlyList<MotdRun> Restyle(
        IEnumerable<MotdRun> runs, int start, int length, Func<MotdRun, MotdRun> change)
    {
        if (length <= 0) return runs.ToList();

        var end = start + length;
        var result = new List<MotdRun>();
        var at = 0;

        foreach (var run in runs)
        {
            var runEnd = at + run.Text.Length;

            // Wholly outside the selection: untouched.
            if (runEnd <= start || at >= end)
            {
                result.Add(run);
                at = runEnd;
                continue;
            }

            var localStart = Math.Max(0, start - at);
            var localEnd = Math.Min(run.Text.Length, end - at);

            if (localStart > 0) result.Add(run.With(run.Text[..localStart]));
            result.Add(change(run.With(run.Text[localStart..localEnd])));
            if (localEnd < run.Text.Length) result.Add(run.With(run.Text[localEnd..]));

            at = runEnd;
        }

        return Merge(result);
    }

    /// <summary>Paints a stretch. Null puts it back to the default colour.</summary>
    public static IReadOnlyList<MotdRun> Colour(
        IEnumerable<MotdRun> runs, int start, int length, char? colour) =>
        Restyle(runs, start, length, r => r with { Colour = colour });

    /// <summary>Turns one of the four marks on or off over a stretch.</summary>
    public static IReadOnlyList<MotdRun> Style(
        IEnumerable<MotdRun> runs, int start, int length, MotdStyle style, bool on) =>
        Restyle(runs, start, length, r => style switch
        {
            MotdStyle.Bold => r with { Bold = on },
            MotdStyle.Italic => r with { Italic = on },
            MotdStyle.Underline => r with { Underline = on },
            _ => r with { Strike = on },
        });

    /// <summary>Strips every colour and mark from a stretch.</summary>
    public static IReadOnlyList<MotdRun> Clear(IEnumerable<MotdRun> runs, int start, int length) =>
        Restyle(runs, start, length, r => new MotdRun(r.Text));

    /// <summary>
    /// Replaces a stretch of the plain text, carrying the styles either side across.
    /// </summary>
    /// <remarks>
    /// This is the one that decides whether typing feels right. What is inserted takes the look of
    /// the text immediately before it, which is what every editor does and what people expect:
    /// typing at the end of a gold word continues in gold. Deleting is the same call with nothing
    /// inserted.
    /// </remarks>
    public static IReadOnlyList<MotdRun> Replace(
        IEnumerable<MotdRun> runs, int start, int length, string inserted)
    {
        var list = runs.ToList();
        var end = start + length;
        var result = new List<MotdRun>();
        var at = 0;
        var placed = false;

        // The look the new text takes: whatever is at the character just before the change, or the
        // first run when the change is at the very beginning.
        var carrier = StyleAt(list, start);

        foreach (var run in list)
        {
            var runEnd = at + run.Text.Length;

            if (runEnd <= start) { result.Add(run); at = runEnd; continue; }
            if (at >= end)
            {
                if (!placed && inserted.Length > 0) { result.Add(carrier.With(inserted)); placed = true; }
                result.Add(run);
                at = runEnd;
                continue;
            }

            var localStart = Math.Max(0, start - at);
            var localEnd = Math.Min(run.Text.Length, end - at);

            if (localStart > 0) result.Add(run.With(run.Text[..localStart]));
            if (!placed && inserted.Length > 0) { result.Add(carrier.With(inserted)); placed = true; }
            if (localEnd < run.Text.Length) result.Add(run.With(run.Text[localEnd..]));

            at = runEnd;
        }

        // Appending past the end of everything.
        if (!placed && inserted.Length > 0) result.Add(carrier.With(inserted));

        return Merge(result);
    }

    /// <summary>The look at a plain-text offset, for text about to be typed there.</summary>
    private static MotdRun StyleAt(IReadOnlyList<MotdRun> runs, int offset)
    {
        if (runs.Count == 0) return new MotdRun(string.Empty);

        var at = 0;
        foreach (var run in runs)
        {
            // Strictly inside, or exactly at its end: both continue this run's look.
            if (offset > at && offset <= at + run.Text.Length) return run.With(string.Empty);
            at += run.Text.Length;
        }

        return runs[0].With(string.Empty);
    }

    /// <summary>Joins neighbouring runs that look the same, and drops empty ones.</summary>
    /// <remarks>
    /// Without this every edit would leave the sign a little more fragmented than it was, and the
    /// code string would grow with repeated <c>§6§6</c> that change nothing.
    /// </remarks>
    private static List<MotdRun> Merge(IEnumerable<MotdRun> runs)
    {
        var result = new List<MotdRun>();

        foreach (var run in runs.Where(r => r.Text.Length > 0))
        {
            if (result.Count > 0 && result[^1].LooksLike(run))
                result[^1] = result[^1].With(result[^1].Text + run.Text);
            else
                result.Add(run);
        }

        return result;
    }

    /// <summary>Whether a pasted string carries formatting worth importing.</summary>
    /// <remarks>
    /// Only <c>§</c> counts. <c>&amp;</c> is accepted when <i>reading</i> a sign, but nobody types
    /// <c>§</c> by accident whereas <c>&amp;</c> is an ordinary character — and <c>&amp;m</c> is
    /// strikethrough, so importing on sight would eat half of "Juan &amp;Mar".
    /// </remarks>
    public static bool LooksCoded(string? text) =>
        text is not null && text.Contains('§');
}
