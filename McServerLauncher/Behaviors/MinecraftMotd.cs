using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Propiedad adjunta que renderiza un MOTD de Minecraft (con códigos de color/formato §) como
/// texto con colores dentro de un TextBlock, igual que se ve en la lista de servidores del juego.
/// </summary>
public static partial class MinecraftMotd
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MinecraftMotd), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);

    // Paleta oficial de colores de Minecraft (§0-§9, §a-§f).
    private static readonly Dictionary<char, Color> Palette = new()
    {
        ['0'] = FromHex("#000000"), ['1'] = FromHex("#0000AA"), ['2'] = FromHex("#00AA00"),
        ['3'] = FromHex("#00AAAA"), ['4'] = FromHex("#AA0000"), ['5'] = FromHex("#AA00AA"),
        ['6'] = FromHex("#FFAA00"), ['7'] = FromHex("#AAAAAA"), ['8'] = FromHex("#555555"),
        ['9'] = FromHex("#5555FF"), ['a'] = FromHex("#55FF55"), ['b'] = FromHex("#55FFFF"),
        ['c'] = FromHex("#FF5555"), ['d'] = FromHex("#FF55FF"), ['e'] = FromHex("#FFFF55"),
        ['f'] = FromHex("#FFFFFF"),
    };

    private static readonly Color Default = FromHex("#AAAAAA");

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();

        var text = Unescape((string?)e.NewValue ?? string.Empty);
        if (text.Length == 0) return;

        var color = Default;
        bool bold = false, italic = false, underline = false, strike = false;
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            var run = new Run(sb.ToString()) { Foreground = new SolidColorBrush(color) };
            if (bold) run.FontWeight = FontWeights.Bold;
            if (italic) run.FontStyle = FontStyles.Italic;
            if (underline || strike)
            {
                var dec = new TextDecorationCollection();
                if (underline) dec.Add(TextDecorations.Underline);
                if (strike) dec.Add(TextDecorations.Strikethrough);
                run.TextDecorations = dec;
            }
            tb.Inlines.Add(run);
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
                    color = col; // un color reinicia el formato (como en Minecraft)
                    bold = italic = underline = strike = false;
                }
                else switch (code)
                {
                    case 'l': bold = true; break;
                    case 'o': italic = true; break;
                    case 'n': underline = true; break;
                    case 'm': strike = true; break;
                    case 'k': break; // ofuscado: lo mostramos normal
                    case 'r': color = Default; bold = italic = underline = strike = false; break;
                }
            }
            else if (c == '\n')
            {
                Flush();
                tb.Inlines.Add(new LineBreak());
            }
            else
            {
                sb.Append(c);
            }
        }
        Flush();
    }

    /// <summary>Convierte secuencias de properties de Java (\n y \uXXXX) a sus caracteres reales.</summary>
    private static string Unescape(string s)
    {
        s = s.Replace("\\n", "\n");
        s = UnicodeEscapeRegex().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        return s;
    }

    private static Color FromHex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
