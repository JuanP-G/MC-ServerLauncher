using System.Text;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>The formatting a Minecraft MOTD can carry besides its colour.</summary>
[Flags]
public enum MotdFormat
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8,
}

/// <summary>
/// How one character looks. <see cref="Color"/> is a Minecraft code (0-9, a-f), <c>'x'</c> for the
/// RGB colour in <see cref="Rgb"/>, or '\0' for the list's default grey.
/// </summary>
public readonly record struct MotdStyle(char Color, MotdFormat Format, int Rgb = -1)
{
    public static readonly MotdStyle Plain = new('\0', MotdFormat.None);

    /// <summary>A free RGB colour, with no formatting.</summary>
    public static MotdStyle OfHex(int rgb) => new('x', MotdFormat.None, rgb & 0xFFFFFF);

    public bool IsHex => Color == 'x';

    /// <summary>The same formatting in another colour: a code, or 'x' with its RGB.</summary>
    public MotdStyle WithColor(char color, int rgb = -1) =>
        this with { Color = color, Rgb = color == 'x' ? rgb & 0xFFFFFF : -1 };
}

/// <summary>
/// A server's MOTD as the two lines the game's server list shows, with a style per character.
/// </summary>
/// <remarks>
/// <para>
/// The editor works on plain text boxes, so the document keeps text and style apart:
/// <see cref="SetText"/> re-aligns the styles after every keystroke instead of the box having to
/// know about <c>§</c> codes. The codes only exist at the two edges — <see cref="FromProperties"/>
/// when reading <c>server.properties</c>, and <see cref="ToProperties"/> / <see cref="ToCodes()"/>
/// when writing it or drawing it.
/// </para>
/// <para>
/// State carries across the line break on purpose. Minecraft keeps the colour and formatting of the
/// first line when it starts the second, so a <c>§6</c> in line one colours line two as well; the
/// document models that rather than pretending the lines are independent, and serialising emits the
/// <c>§r</c> needed to stop it.
/// </para>
/// </remarks>
public sealed class MotdDocument
{
    public const int LineCount = 2;

    /// <summary>The 16 colour codes, in the order the game lists them.</summary>
    public const string ColorCodes = "0123456789abcdef";

    // The game's own values for the 16 codes, as 0xRRGGBB.
    private static readonly int[] CodeRgb =
    [
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xFFAA00, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
    ];

    /// <summary>The colour a code draws, as 0xRRGGBB.</summary>
    public static int RgbOf(char code) => CodeRgb[ColorCodes.IndexOf(code)];

    /// <summary>The code whose colour is closest to <paramref name="rgb"/>.</summary>
    public static char NearestCode(int rgb)
    {
        var best = '0';
        var bestDistance = int.MaxValue;
        foreach (var code in ColorCodes)
        {
            var c = RgbOf(code);
            int dr = ((c >> 16) & 255) - ((rgb >> 16) & 255), dg = ((c >> 8) & 255) - ((rgb >> 8) & 255), db = (c & 255) - (rgb & 255);
            var d = dr * dr * 3 + dg * dg * 4 + db * db * 2; // weighted the way the eye weighs them
            if (d < bestDistance) (best, bestDistance) = (code, d);
        }
        return best;
    }

    /// <summary>
    /// Whether a server of this kind shows free RGB colours in its MOTD.
    /// </summary>
    /// <remarks>
    /// Paper and its fork Purpur read <c>motd=</c> with a parser that knows the long
    /// <c>§x§R§R§G§G§B§B</c> form, from 1.16 on, when the game got RGB text. Vanilla and the mod
    /// loaders read only the 16 codes, and would show the <c>§x</c> run as stray letters, so the
    /// editor only offers it where it works.
    /// </remarks>
    public static bool HexWorksOn(ServerType type, string? gameVersion)
    {
        if (type is not (ServerType.Paper or ServerType.Purpur)) return false;
        if (string.IsNullOrWhiteSpace(gameVersion)) return true;
        var parts = gameVersion.Split('.', '-', ' ');
        if (!int.TryParse(parts[0], out var major)) return true;
        if (major > 1) return true;
        return parts.Length > 1 && int.TryParse(parts[1], out var minor) && minor >= 16;
    }

    private readonly string[] _text = ["", ""];
    private readonly List<MotdStyle>[] _styles = [new(), new()];

    /// <summary>True when the source had a third line or more, which the list cannot show and this document does not keep.</summary>
    public bool HadExtraLines { get; private set; }

    public string GetText(int line) => _text[line];

    public int Length(int line) => _text[line].Length;

    public MotdStyle StyleAt(int line, int index) => _styles[line][index];

    /// <summary>True when some character has a free RGB colour.</summary>
    public bool HasHex => _styles.Any(line => line.Any(s => s.IsHex));

    // ---------------------------------------------------------------- reading

    /// <summary>Parses the raw value of <c>motd=</c>, escapes and <c>§</c> codes included.</summary>
    public static MotdDocument FromProperties(string? raw)
    {
        var doc = new MotdDocument();
        if (string.IsNullOrEmpty(raw)) return doc;

        var text = Unescape(raw);
        var style = MotdStyle.Plain;
        var line = 0;
        var sb = new StringBuilder();

        void CloseLine()
        {
            doc._text[line] = sb.ToString();
            sb.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') continue;

            if (c == '\n')
            {
                CloseLine();
                if (++line >= LineCount) { doc.HadExtraLines = true; return doc; }
                continue;
            }

            if (c == '§')
            {
                if (TryReadHex(text, i, out var rgb))
                {
                    style = MotdStyle.OfHex(rgb); // a colour wipes the formatting, RGB ones too
                    i += 13;
                    continue;
                }
                if (i + 1 < text.Length) style = Apply(style, char.ToLowerInvariant(text[++i]));
                continue;
            }

            sb.Append(c);
            doc._styles[line].Add(style);
        }

        CloseLine();
        return doc;
    }

    /// <summary>Reads <c>§x§R§R§G§G§B§B</c> starting at <paramref name="at"/>.</summary>
    internal static bool TryReadHex(string text, int at, out int rgb)
    {
        rgb = 0;
        if (at + 14 > text.Length || text[at] != '§' || char.ToLowerInvariant(text[at + 1]) != 'x') return false;
        for (var k = 0; k < 6; k++)
        {
            if (text[at + 2 + 2 * k] != '§') return false;
            var h = char.ToLowerInvariant(text[at + 3 + 2 * k]);
            if (!Uri.IsHexDigit(h)) return false;
            rgb = (rgb << 4) | Convert.ToInt32(h.ToString(), 16);
        }
        return true;
    }

    /// <summary>The style after one <c>§</c> code. A colour wipes the formatting, as it does in the game.</summary>
    private static MotdStyle Apply(MotdStyle s, char code) => code switch
    {
        >= '0' and <= '9' or >= 'a' and <= 'f' => new MotdStyle(code, MotdFormat.None),
        'l' => s with { Format = s.Format | MotdFormat.Bold },
        'o' => s with { Format = s.Format | MotdFormat.Italic },
        'n' => s with { Format = s.Format | MotdFormat.Underline },
        'm' => s with { Format = s.Format | MotdFormat.Strike },
        'r' => MotdStyle.Plain,
        _ => s, // §k (obfuscated) and anything unknown: dropped, the text is still there
    };

    /// <summary>
    /// Turns the properties-file escapes into the characters they stand for: <c>\n</c>,
    /// <c>\uXXXX</c> and <c>\\</c>. Any other backslash is dropped, as Java's loader does.
    /// </summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                if (s[i] != '\\') sb.Append(s[i]);
                continue;
            }

            var next = s[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'u' when TryHex(s, i + 1, out var ch):
                    sb.Append(ch);
                    i += 4;
                    break;
                default: sb.Append(next); break;
            }
        }
        return sb.ToString();
    }

    private static bool TryHex(string s, int at, out char ch)
    {
        ch = '\0';
        if (at + 4 > s.Length) return false;
        if (!int.TryParse(s.AsSpan(at, 4), System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        ch = (char)v;
        return true;
    }

    // ---------------------------------------------------------------- writing

    /// <summary>The value to store after <c>motd=</c>: ASCII only, <c>§</c> as <c>\u00a7</c>, the break as <c>\n</c>.</summary>
    /// <remarks>
    /// Escaping everything past ASCII is what the game itself does when it writes the file. Older
    /// versions read <c>server.properties</c> as Latin-1, so a raw "·" or "ñ" would come back mangled;
    /// the escaped form reads the same everywhere.
    /// </remarks>
    public string ToProperties() => Escape(ToCodes());

    /// <summary>Both lines as real <c>§</c> codes joined by a real newline: what the game draws.</summary>
    public string ToCodes()
    {
        var sb = new StringBuilder();
        var state = MotdStyle.Plain;

        AppendLine(sb, 0, ref state);
        if (_text[1].Length > 0)
        {
            sb.Append('\n');
            AppendLine(sb, 1, ref state);
        }
        return sb.ToString();
    }

    /// <summary>One line as <c>§</c> codes, starting from a clean state.</summary>
    public string ToCodes(int line)
    {
        var sb = new StringBuilder();
        var state = MotdStyle.Plain;
        AppendLine(sb, line, ref state);
        return sb.ToString();
    }

    private void AppendLine(StringBuilder sb, int line, ref MotdStyle state)
    {
        var text = _text[line];
        for (var i = 0; i < text.Length; i++)
        {
            Transition(sb, ref state, _styles[line][i]);
            sb.Append(text[i]);
        }
    }

    /// <summary>Emits the fewest codes that take the game's current style to <paramref name="want"/>.</summary>
    private static void Transition(StringBuilder sb, ref MotdStyle have, MotdStyle want)
    {
        if (have == want) return;

        // Formatting can only be switched off by starting over, and so can going back to the default colour.
        var removesFormat = (have.Format & ~want.Format) != MotdFormat.None;
        if (removesFormat || (want.Color == '\0' && have.Color != '\0'))
        {
            sb.Append("§r");
            have = MotdStyle.Plain;
        }

        if (want.Color != have.Color || want.Rgb != have.Rgb)
        {
            if (want.IsHex)
            {
                sb.Append("§x");
                foreach (var digit in want.Rgb.ToString("x6")) sb.Append('§').Append(digit);
            }
            else sb.Append('§').Append(want.Color);
            have = new MotdStyle(want.Color, MotdFormat.None, want.Rgb); // a colour wipes the formatting
        }

        var add = want.Format & ~have.Format;
        if ((add & MotdFormat.Bold) != 0) sb.Append("§l");
        if ((add & MotdFormat.Italic) != 0) sb.Append("§o");
        if ((add & MotdFormat.Underline) != 0) sb.Append("§n");
        if ((add & MotdFormat.Strike) != 0) sb.Append("§m");
        have = want;
    }

    private static string Escape(string codes)
    {
        var sb = new StringBuilder(codes.Length + 16);
        for (var i = 0; i < codes.Length; i++)
        {
            var c = codes[i];
            var edge = i == 0 || i == codes.Length - 1; // Java drops leading whitespace of a value
            if (c == '\n') sb.Append("\\n");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 32 || c > 126 || (c == ' ' && edge)) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- editing

    /// <summary>
    /// Replaces a line's text, keeping every untouched character's style.
    /// </summary>
    /// <param name="line">0 or 1.</param>
    /// <param name="value">The whole new text of the line, as the box now holds it.</param>
    /// <param name="caret">
    /// Where the caret is after the edit. Typing, backspace and delete all leave the edit touching
    /// the caret, which settles the cases a plain diff cannot — typing an "a" next to an "a".
    /// </param>
    /// <param name="typing">The style new characters take; by default that of the character they replace or follow.</param>
    public void SetText(int line, string? value, int? caret = null, MotdStyle? typing = null)
    {
        var next = Clean(value);
        var old = _text[line];
        if (next == old) return;

        var common = Math.Min(old.Length, next.Length);
        var maxSuffix = 0;
        while (maxSuffix < common && old[^(maxSuffix + 1)] == next[^(maxSuffix + 1)]) maxSuffix++;
        var commonPrefix = 0;
        while (commonPrefix < common && old[commonPrefix] == next[commonPrefix]) commonPrefix++;

        var suffix = maxSuffix;
        if (caret is { } c && c >= 0 && c <= next.Length && next.Length - c <= maxSuffix)
            suffix = next.Length - c;
        var prefix = Math.Min(commonPrefix, common - suffix);

        var styles = _styles[line];
        var removed = old.Length - prefix - suffix;
        var inserted = next.Length - prefix - suffix;

        MotdStyle fill;
        if (typing is { } t) fill = t;
        else if (removed > 0) fill = styles[prefix];                 // typing over a selection takes its style
        else if (prefix > 0) fill = styles[prefix - 1];
        else fill = styles.Count > 0 ? styles[0] : MotdStyle.Plain;

        styles.RemoveRange(prefix, removed);
        styles.InsertRange(prefix, Enumerable.Repeat(fill, inserted));
        _text[line] = next;
    }

    /// <summary>Text can hold neither a line break nor a § — both would be read back as codes.</summary>
    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace("\r", "").Replace("\n", "").Replace("§", "");

    /// <summary>Sets the colour of a range, keeping its formatting.</summary>
    public void SetColor(int line, int start, int length, char color) =>
        Transform(line, start, length, s => s.WithColor(color));

    /// <summary>Sets a free RGB colour on a range, keeping its formatting.</summary>
    public void SetHex(int line, int start, int length, int rgb) =>
        Transform(line, start, length, s => s.WithColor('x', rgb));

    /// <summary>
    /// Spreads a colour ramp from <paramref name="fromRgb"/> to <paramref name="toRgb"/> across a
    /// range, one step per character, keeping the formatting.
    /// </summary>
    /// <param name="line">0 or 1.</param>
    /// <param name="start">The first character of the range.</param>
    /// <param name="length">How many characters it covers.</param>
    /// <param name="fromRgb">The colour of the first character, as 0xRRGGBB.</param>
    /// <param name="toRgb">The colour of the last one.</param>
    /// <param name="hex">
    /// Each character its own RGB colour. Otherwise each takes the nearest of the 16 codes, which
    /// gives bands rather than a smooth ramp — what a server without RGB text can show.
    /// </param>
    public void SetGradient(int line, int start, int length, int fromRgb, int toRgb, bool hex)
    {
        (start, length) = Clamp(line, start, length);
        for (var i = 0; i < length; i++)
        {
            var t = length == 1 ? 0 : i / (double)(length - 1);
            var rgb = Lerp(fromRgb, toRgb, t);
            var at = start + i;
            _styles[line][at] = hex ? _styles[line][at].WithColor('x', rgb) : _styles[line][at].WithColor(NearestCode(rgb));
        }
    }

    private static int Lerp(int a, int b, double t)
    {
        int Channel(int shift) => (int)Math.Round(((a >> shift) & 255) + (((b >> shift) & 255) - ((a >> shift) & 255)) * t);
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>Turns a format on or off across a range.</summary>
    public void SetFormat(int line, int start, int length, MotdFormat flag, bool on) =>
        Transform(line, start, length, s => s with { Format = on ? s.Format | flag : s.Format & ~flag });

    /// <summary>Back to the default colour with no formatting.</summary>
    public void ClearStyle(int line, int start, int length) =>
        Transform(line, start, length, _ => MotdStyle.Plain);

    /// <summary>True when the range is not empty and every character already has the format.</summary>
    public bool AllHave(int line, int start, int length, MotdFormat flag)
    {
        (start, length) = Clamp(line, start, length);
        if (length == 0) return false;
        for (var i = start; i < start + length; i++)
            if ((_styles[line][i].Format & flag) == 0) return false;
        return true;
    }

    private void Transform(int line, int start, int length, Func<MotdStyle, MotdStyle> f)
    {
        (start, length) = Clamp(line, start, length);
        for (var i = start; i < start + length; i++)
            _styles[line][i] = f(_styles[line][i]);
    }

    private (int Start, int Length) Clamp(int line, int start, int length)
    {
        start = Math.Clamp(start, 0, _text[line].Length);
        length = Math.Clamp(length, 0, _text[line].Length - start);
        return (start, length);
    }
}
