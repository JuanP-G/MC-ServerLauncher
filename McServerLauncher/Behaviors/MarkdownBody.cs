using System.Collections.Generic;
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
        SourceProperty.Changed.AddClassHandler<Panel>((panel, e) => Render(panel, e.NewValue as string));
    }

    private static readonly IBrush LinkBrush = new ImmutableSolidColorBrush(Color.Parse("#58A6FF"));
    private static readonly IBrush QuoteBrush = new ImmutableSolidColorBrush(Color.Parse("#2AFFFFFF"));
    private static readonly IBrush CodeBackground = new ImmutableSolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IBrush RuleBrush = new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF"));

    private static void Render(Panel panel, string? markdown)
    {
        panel.Children.Clear();
        foreach (var block in MarkdownParser.Parse(markdown))
        {
            var control = Build(block);
            if (control is not null) panel.Children.Add(control);
        }
    }

    private static Control? Build(MarkdownBlock block)
    {
        if (block.Kind == MarkdownBlockKind.Rule)
            return new Border
            {
                Height = 1,
                Background = RuleBrush,
                Margin = new Thickness(0, 10, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

        if (block.Kind == MarkdownBlockKind.Code)
            return new Border
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

        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        FillInlines(text, block.Spans);

        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading1:
                text.FontSize = 18;
                text.FontWeight = FontWeight.SemiBold;
                text.Margin = new Thickness(0, 12, 0, 4);
                break;
            case MarkdownBlockKind.Heading2:
                text.FontSize = 15;
                text.FontWeight = FontWeight.SemiBold;
                text.Margin = new Thickness(0, 10, 0, 3);
                break;
            case MarkdownBlockKind.Heading3:
                text.FontSize = 13;
                text.FontWeight = FontWeight.SemiBold;
                text.Margin = new Thickness(0, 8, 0, 2);
                break;
            case MarkdownBlockKind.Quote:
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
            case MarkdownBlockKind.Bullet:
            case MarkdownBlockKind.Ordered:
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
            default:
                text.FontSize = 13;
                text.LineHeight = 19;
                text.Margin = new Thickness(0, 2, 0, 6);
                break;
        }

        return text;
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
