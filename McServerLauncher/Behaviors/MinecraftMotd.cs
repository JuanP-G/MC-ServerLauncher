using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Attached property that renders a Minecraft MOTD (with § color/format codes) as
/// colored text inside a TextBlock, just like it looks in the game's server list.
/// </summary>
public static partial class MinecraftMotd
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(MinecraftMotd));

    public static string? GetText(TextBlock o) => o.GetValue(TextProperty);
    public static void SetText(TextBlock o, string? v) => o.SetValue(TextProperty, v);

    static MinecraftMotd()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, e) => OnTextChanged(tb, e));
    }

    // Official Minecraft color palette (§0-§9, §a-§f), from the one table MotdDocument keeps.
    internal static readonly Dictionary<char, Color> Palette = Services.MotdDocument.ColorCodes.ToDictionary(
        code => code, code => Color.FromUInt32(0xFF000000 | (uint)Services.MotdDocument.RgbOf(code)));

    private static readonly Color Default = Color.Parse("#AAAAAA");

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();

    private static void OnTextChanged(TextBlock tb, AvaloniaPropertyChangedEventArgs e)
    {
        tb.Inlines?.Clear();
        var inlines = tb.Inlines ??= new InlineCollection();

        var text = Unescape(e.NewValue as string ?? string.Empty);
        if (text.Length == 0) return;

        var runs = new RunBuilder(inlines);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            // The long RGB form Paper reads: §x§R§R§G§G§B§B.
            if (c == '§' && Services.MotdDocument.TryReadHex(text, i, out var rgb))
            {
                runs.SetColor(Color.FromUInt32(0xFF000000 | (uint)rgb));
                i += 13;
            }
            else if ((c == '§' || c == '&') && i + 1 < text.Length)
            {
                runs.Apply(char.ToLowerInvariant(text[++i]));
            }
            else if (c == '\n')
            {
                runs.LineBreak();
            }
            else
            {
                runs.Add(c);
            }
        }
        runs.Flush();
    }

    /// <summary>
    /// Turns the MOTD's characters into runs: the text collected so far is closed into a run
    /// whenever the colour or the style changes, in the colour and style it was written in.
    /// </summary>
    private sealed class RunBuilder
    {
        private readonly InlineCollection _inlines;
        private readonly StringBuilder _text = new();
        private Color _color = Default;
        private bool _bold, _italic, _underline, _strike;

        public RunBuilder(InlineCollection inlines) => _inlines = inlines;

        public void Add(char c) => _text.Append(c);

        public void LineBreak()
        {
            Flush();
            _inlines.Add(new LineBreak());
        }

        /// <summary>A colour, which resets the formatting (as in Minecraft).</summary>
        public void SetColor(Color color)
        {
            Flush();
            _color = color;
            _bold = _italic = _underline = _strike = false;
        }

        /// <summary>The code after a § or &amp;: a colour, a style, or reset.</summary>
        public void Apply(char code)
        {
            Flush();
            if (Palette.TryGetValue(code, out var col))
            {
                SetColor(col);
                return;
            }

            switch (code)
            {
                case 'l': _bold = true; break;
                case 'o': _italic = true; break;
                case 'n': _underline = true; break;
                case 'm': _strike = true; break;
                case 'k': break; // obfuscated: shown as normal text
                case 'r': SetColor(Default); break;
            }
        }

        public void Flush()
        {
            if (_text.Length == 0) return;
            var run = new Run(_text.ToString()) { Foreground = new SolidColorBrush(_color) };
            if (_bold) run.FontWeight = FontWeight.Bold;
            if (_italic) run.FontStyle = FontStyle.Italic;
            if (_underline || _strike)
            {
                var dec = new TextDecorationCollection();
                if (_underline) dec.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
                if (_strike) dec.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
                run.TextDecorations = dec;
            }
            _inlines.Add(run);
            _text.Clear();
        }
    }

    /// <summary>Converts Java properties escape sequences (\n and \uXXXX) to their real characters.</summary>
    private static string Unescape(string s)
    {
        s = s.Replace("\\n", "\n");
        s = UnicodeEscapeRegex().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        return s;
    }
}
