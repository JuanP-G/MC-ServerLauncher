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

    // Official Minecraft color palette (§0-§9, §a-§f).
    private static readonly Dictionary<char, Color> Palette = new()
    {
        ['0'] = Color.Parse("#000000"), ['1'] = Color.Parse("#0000AA"), ['2'] = Color.Parse("#00AA00"),
        ['3'] = Color.Parse("#00AAAA"), ['4'] = Color.Parse("#AA0000"), ['5'] = Color.Parse("#AA00AA"),
        ['6'] = Color.Parse("#FFAA00"), ['7'] = Color.Parse("#AAAAAA"), ['8'] = Color.Parse("#555555"),
        ['9'] = Color.Parse("#5555FF"), ['a'] = Color.Parse("#55FF55"), ['b'] = Color.Parse("#55FFFF"),
        ['c'] = Color.Parse("#FF5555"), ['d'] = Color.Parse("#FF55FF"), ['e'] = Color.Parse("#FFFF55"),
        ['f'] = Color.Parse("#FFFFFF"),
    };

    private static readonly Color Default = Color.Parse("#AAAAAA");

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();

    private static void OnTextChanged(TextBlock tb, AvaloniaPropertyChangedEventArgs e)
    {
        tb.Inlines?.Clear();
        var inlines = tb.Inlines ??= new InlineCollection();

        var text = Unescape(e.NewValue as string ?? string.Empty);
        if (text.Length == 0) return;

        var color = Default;
        bool bold = false, italic = false, underline = false, strike = false;
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            var run = new Run(sb.ToString()) { Foreground = new SolidColorBrush(color) };
            if (bold) run.FontWeight = FontWeight.Bold;
            if (italic) run.FontStyle = FontStyle.Italic;
            if (underline || strike)
            {
                var dec = new TextDecorationCollection();
                if (underline) dec.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
                if (strike) dec.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
                run.TextDecorations = dec;
            }
            inlines.Add(run);
            sb.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '§' || c == '&') && i + 1 < text.Length)
            {
                Flush();
                var code = char.ToLowerInvariant(text[++i]);
                if (Palette.TryGetValue(code, out var col))
                {
                    color = col; // a color resets the formatting (as in Minecraft)
                    bold = italic = underline = strike = false;
                }
                else switch (code)
                {
                    case 'l': bold = true; break;
                    case 'o': italic = true; break;
                    case 'n': underline = true; break;
                    case 'm': strike = true; break;
                    case 'k': break; // obfuscated: shown as normal text
                    case 'r': color = Default; bold = italic = underline = strike = false; break;
                }
            }
            else if (c == '\n')
            {
                Flush();
                inlines.Add(new LineBreak());
            }
            else
            {
                sb.Append(c);
            }
        }
        Flush();
    }

    /// <summary>Converts Java properties escape sequences (\n and \uXXXX) to their real characters.</summary>
    private static string Unescape(string s)
    {
        s = s.Replace("\\n", "\n");
        s = UnicodeEscapeRegex().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        return s;
    }
}
