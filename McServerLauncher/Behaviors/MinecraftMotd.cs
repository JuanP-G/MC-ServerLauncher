using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using McServerLauncher.Services;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Attached property that renders a Minecraft MOTD (with § color/format codes) as
/// colored text inside a TextBlock, just like it looks in the game's server list.
/// </summary>
/// <remarks>
/// The reading is <see cref="MotdDocument"/>'s, not its own. It used to have its own loop over the
/// codes, which was fine while it was the only thing in the app that understood a sign — but the
/// editor has to understand one too, and two readings of the same format drift. Since they share
/// one, the preview in the editor cannot disagree with the sign drawn in the header, because they
/// are the same code twice.
/// </remarks>
public static class MinecraftMotd
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(MinecraftMotd));

    public static string? GetText(TextBlock o) => o.GetValue(TextProperty);
    public static void SetText(TextBlock o, string? v) => o.SetValue(TextProperty, v);

    static MinecraftMotd()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, e) => OnTextChanged(tb, e));
    }

    /// <summary>Official Minecraft color palette (§0-§9, §a-§f).</summary>
    /// <remarks>
    /// Written out rather than made into theme tokens on purpose: these are Minecraft's colours,
    /// not ours, and they must not move when the app's palette does.
    /// </remarks>
    private static readonly Dictionary<char, Color> Palette = new()
    {
        ['0'] = Color.Parse("#000000"), ['1'] = Color.Parse("#0000AA"), ['2'] = Color.Parse("#00AA00"),
        ['3'] = Color.Parse("#00AAAA"), ['4'] = Color.Parse("#AA0000"), ['5'] = Color.Parse("#AA00AA"),
        ['6'] = Color.Parse("#FFAA00"), ['7'] = Color.Parse("#AAAAAA"), ['8'] = Color.Parse("#555555"),
        ['9'] = Color.Parse("#5555FF"), ['a'] = Color.Parse("#55FF55"), ['b'] = Color.Parse("#55FFFF"),
        ['c'] = Color.Parse("#FF5555"), ['d'] = Color.Parse("#FF55FF"), ['e'] = Color.Parse("#FFFF55"),
        ['f'] = Color.Parse("#FFFFFF"),
    };

    private static readonly Color Default = Color.Parse(MotdDocument.DefaultColour);

    /// <summary>The colour a run is drawn in.</summary>
    public static Color ColourOf(char? code) =>
        code is { } c && Palette.TryGetValue(c, out var colour) ? colour : Default;

    /// <summary>Builds the inlines for a sign. Public so the editor's preview uses the same ones.</summary>
    public static IEnumerable<Inline> Render(IEnumerable<MotdRun> runs)
    {
        foreach (var run in runs)
        {
            // A newline inside a run has to become a real break: Inlines lay text out, and a "\n"
            // in a Run is drawn as a space.
            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) yield return new LineBreak();
                if (parts[i].Length == 0) continue;

                var inline = new Run(parts[i]) { Foreground = new SolidColorBrush(ColourOf(run.Colour)) };
                if (run.Bold) inline.FontWeight = FontWeight.Bold;
                if (run.Italic) inline.FontStyle = FontStyle.Italic;

                if (run.Underline || run.Strike)
                {
                    var decorations = new TextDecorationCollection();
                    if (run.Underline)
                        decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
                    if (run.Strike)
                        decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
                    inline.TextDecorations = decorations;
                }

                yield return inline;
            }
        }
    }

    private static void OnTextChanged(TextBlock tb, AvaloniaPropertyChangedEventArgs e)
    {
        tb.Inlines?.Clear();
        var inlines = tb.Inlines ??= new InlineCollection();

        foreach (var inline in Render(MotdDocument.Parse(e.NewValue as string)))
            inlines.Add(inline);
    }
}
