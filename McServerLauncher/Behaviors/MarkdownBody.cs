using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using McServerLauncher.Services;

namespace McServerLauncher.Behaviors;

/// <summary>
/// Attached property that renders a project's long description (Markdown, as Modrinth returns it)
/// into a panel, in the same spirit as <see cref="MinecraftMotd"/>: the view model hands over
/// text, the view turns it into controls.
/// <para>
/// Parsing lives in <see cref="MarkdownParser"/>; this only decides how each block looks. Links
/// become clickable text that opens the browser through <see cref="BrowserLauncher"/>, which
/// refuses anything that isn't an ordinary web address — the description is remote content.
/// </para>
/// </summary>
public static class MarkdownBody
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Panel, string?>("Source", typeof(MarkdownBody));

    public static string? GetSource(Panel o) => o.GetValue(SourceProperty);
    public static void SetSource(Panel o, string? v) => o.SetValue(SourceProperty, v);

    static MarkdownBody()
    {
        SourceProperty.Changed.AddClassHandler<Panel>((panel, e)
            => _ = RenderAsync(panel, e.NewValue as string));
    }

    private static readonly IBrush LinkBrush = new ImmutableSolidColorBrush(Color.Parse("#58A6FF"));
    private static readonly IBrush QuoteBrush = new ImmutableSolidColorBrush(Color.Parse("#2AFFFFFF"));
    private static readonly IBrush CodeBackground = new ImmutableSolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IBrush RuleBrush = new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF"));

    /// <summary>Parses off the UI thread, then builds the controls on it.</summary>
    /// <remarks>
    /// <para>
    /// The description is written by whoever published the mod, and some inputs make the parser's
    /// regular expressions backtrack: measured, a crafted 20 000-character body took about half a
    /// second. Parsing on the UI thread froze the window for all of it. The parser is pure and
    /// returns plain records, so it can run anywhere; only the controls need the UI thread.
    /// </para>
    /// <para>
    /// If the source changes again while a parse is running (the user opens another mod), the
    /// older result is dropped instead of overwriting the newer page.
    /// </para>
    /// </remarks>
    private static async Task RenderAsync(Panel panel, string? markdown)
    {
        panel.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        IReadOnlyList<MarkdownBlock> blocks;
        try
        {
            blocks = await Task.Run(() => MarkdownParser.Parse(markdown));
        }
        catch
        {
            return;   // an unparseable description leaves the panel empty rather than crashing the page
        }

        if (!ReferenceEquals(GetSource(panel), markdown)) return;

        panel.Children.Clear();
        foreach (var block in blocks)
        {
            var control = Build(block);
            if (control is not null) panel.Children.Add(control);
        }
    }

    /// <summary>The control for one block: rules and code have their own shape, the rest are text.</summary>
    private static Control? Build(MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Rule: return BuildRule();
            case MarkdownBlockKind.Code: return BuildCode(block);
        }

        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        FillInlines(text, block.Spans);

        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading1: StyleHeading(text, 18, new Thickness(0, 12, 0, 4)); break;
            case MarkdownBlockKind.Heading2: StyleHeading(text, 15, new Thickness(0, 10, 0, 3)); break;
            case MarkdownBlockKind.Heading3: StyleHeading(text, 13, new Thickness(0, 8, 0, 2)); break;
            case MarkdownBlockKind.Quote: return BuildQuote(text);
            case MarkdownBlockKind.Bullet:
            case MarkdownBlockKind.Ordered: return BuildListItem(block, text);
            default:
                text.FontSize = 13;
                text.LineHeight = 19;
                text.Margin = new Thickness(0, 2, 0, 6);
                break;
        }

        return text;
    }

    /// <summary>A thin horizontal line.</summary>
    private static Border BuildRule() => new()
    {
        Height = 1,
        Background = RuleBrush,
        Margin = new Thickness(0, 10, 0, 10),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>A fenced code block: monospaced, selectable and wrapped on a tinted card.</summary>
    private static Border BuildCode(MarkdownBlock block) => new()
    {
        Background = CodeBackground,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8),
        Margin = new Thickness(0, 4, 0, 6),
        Child = new SelectableTextBlock
        {
            Text = block.Spans.Count > 0 ? block.Spans[0].Text : string.Empty,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        }
    };

    /// <summary>The look shared by the three heading levels, which differ only in size and spacing.</summary>
    private static void StyleHeading(SelectableTextBlock text, double size, Thickness margin)
    {
        text.FontSize = size;
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = margin;
    }

    /// <summary>A quotation: smaller, italic and set off by a bar on the left.</summary>
    private static Border BuildQuote(SelectableTextBlock text)
    {
        text.FontSize = 13;
        text.Opacity = 0.85;
        text.FontStyle = FontStyle.Italic;
        return new Border
        {
            BorderBrush = QuoteBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 4),
            Child = text
        };
    }

    /// <summary>A list item: its marker (a bullet or its number) beside the text.</summary>
    private static Grid BuildListItem(MarkdownBlock block, SelectableTextBlock text)
    {
        text.FontSize = 13;
        var marker = block.Kind == MarkdownBlockKind.Bullet
            ? "•"
            : (block.Number > 0 ? block.Number + "." : "•");
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(6, 1, 0, 1)
        };
        var bullet = new TextBlock
        {
            Text = marker,
            FontSize = 13,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 14
        };
        Grid.SetColumn(bullet, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(bullet);
        row.Children.Add(text);
        return row;
    }

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static void FillInlines(SelectableTextBlock target, IReadOnlyList<MarkdownSpan> spans)
    {
        var inlines = target.Inlines ??= new InlineCollection();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;

            // A Run is text, not an input element, so it cannot be clicked. Links are therefore
            // hosted in a small control placed inside the text flow.
            if (span.Link is { } url)
            {
                inlines.Add(new InlineUIContainer(BuildLink(span.Text, url))
                {
                    BaselineAlignment = BaselineAlignment.TextBottom
                });
                continue;
            }

            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeight.Bold;
            if (span.Italic) run.FontStyle = FontStyle.Italic;
            if (span.Code)
            {
                run.FontFamily = new FontFamily("Consolas, Menlo, monospace");
                run.Background = CodeBackground;
            }
            inlines.Add(run);
        }
    }

    private static Control BuildLink(string label, string url)
    {
        var link = new TextBlock
        {
            Text = label,
            Foreground = LinkBrush,
            Cursor = HandCursor,
            FontSize = 13,
            TextDecorations = new TextDecorationCollection
            {
                new TextDecoration { Location = TextDecorationLocation.Underline }
            }
        };
        ToolTip.SetTip(link, url);
        link.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) return;
            BrowserLauncher.Open(url);
            e.Handled = true;
        };
        return link;
    }
}
