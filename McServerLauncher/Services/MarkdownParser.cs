using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>What a parsed block of a project's long description is.</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Ordered,
    Quote,
    Code,
    Rule
}

/// <summary>A run of text inside a block, with the formatting that applies to it.</summary>
public sealed record MarkdownSpan(string Text, bool Bold = false, bool Italic = false,
    bool Code = false, string? Link = null);

/// <summary>One block of the description: a paragraph, a heading, a list item…</summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, IReadOnlyList<MarkdownSpan> Spans,
    int Number = 0);

/// <summary>
/// A small Markdown reader for the long descriptions Modrinth returns.
/// <para>
/// It is deliberately partial. Those descriptions are Markdown with raw HTML mixed in, written to
/// be rendered by a web page, and pulling in a full Markdown engine (and an HTML sanitiser behind
/// it) to show them in a desktop panel would be a large dependency for a read-only view. What is
/// supported is what those pages actually use: headings, lists, quotes, code, emphasis, links and
/// rules. Everything else — HTML tags, images, scripts — is dropped rather than shown raw, so a
/// project page can never inject markup into the app.
/// </para>
/// <para>Images are dropped on purpose: the gallery already shows them, and a description with
/// thirty remote images would mean thirty downloads for a page the user may only skim.</para>
/// </summary>
public static partial class MarkdownParser
{
    /// <summary>Descriptions longer than this are cut; the page offers Modrinth for the rest.</summary>
    public const int MaxCharacters = 20_000;

    /// <summary>Upper bound on blocks, so a pathological page can't build a giant visual tree.</summary>
    private const int MaxBlocks = 400;

    /// <summary>
    /// Upper bound on clickable links in one description. A link has to be a real control to be
    /// clickable, and some pages list every sponsor by name — Create's runs to several hundred.
    /// Past this point links are kept as plain text: still readable, but not hundreds of controls
    /// inside a single paragraph.
    /// </summary>
    private const int MaxLinks = 120;

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTagRegex();

    [GeneratedRegex(@"<(script|style)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex(@"^\s{0,3}(?<hashes>#{1,6})\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    /// <summary>A setext underline: a run of = or - directly under a line of text makes it a heading.</summary>
    [GeneratedRegex(@"^\s{0,3}(?<c>=|-)\k<c>*\s*$")]
    private static partial Regex SetextRegex();

    [GeneratedRegex(@"^\s{0,6}[-*+]\s+(?<text>.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s{0,6}(?<n>\d{1,3})[.)]\s+(?<text>.*)$")]
    private static partial Regex OrderedRegex();

    [GeneratedRegex(@"^\s{0,3}>\s?(?<text>.*)$")]
    private static partial Regex QuoteRegex();

    /// <summary>A table separator row (|---|---|), which carries no information here.</summary>
    [GeneratedRegex(@"^\s*\|?[\s:|-]+\|[\s:|-]*$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(
        @"(?<code>`+[^`]+`+)" +
        @"|(?<image>!\[[^\]]*\]\([^)]*\))" +
        @"|\[(?<ltext>[^\]]*)\]\((?<lurl>[^)\s]*)(?:\s+""[^""]*"")?\)" +
        @"|\*\*(?<bold>[^*]+)\*\*" +
        @"|__(?<bold2>[^_]+)__" +
        @"|\*(?<italic>[^*\n]+)\*" +
        @"|(?<![A-Za-z0-9])_(?<italic2>[^_\n]+)_(?![A-Za-z0-9])" +
        // The length cap keeps the regex bounded. It has to be generous: a single <iframe> in a
        // real description already runs to ~260 characters, and a tag that overflows the cap would
        // not be recognised as markup and would be printed to the screen verbatim.
        @"|(?<html><[^<>\n]{1,2000}>)")]
    private static partial Regex InlineRegex();

    /// <summary>Parses a description into blocks. Null or empty input gives an empty list.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var text = markdown.Length > MaxCharacters ? markdown[..MaxCharacters] : markdown;

        // Strip what must never reach the renderer, and turn explicit HTML breaks into real ones
        // so a description written mostly in HTML still splits into readable lines.
        text = ScriptOrStyleRegex().Replace(text, string.Empty);
        text = HtmlCommentRegex().Replace(text, string.Empty);
        text = LineBreakTagRegex().Replace(text, "\n");

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();
        var inFence = false;
        var fence = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            var spans = ParseInline(paragraph.ToString());
            if (spans.Count > 0) blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, spans));
            paragraph.Clear();
        }

        foreach (var rawLine in lines)
        {
            if (blocks.Count >= MaxBlocks) break;
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code,
                        new[] { new MarkdownSpan(fence.ToString().TrimEnd(), Code: true) }));
                    fence.Clear();
                    inFence = false;
                }
                else
                {
                    FlushParagraph();
                    inFence = true;
                }
                continue;
            }

            if (inFence)
            {
                if (fence.Length < 4000) fence.Append(line).Append('\n');
                continue;
            }

            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // A line of = or - directly under text is a setext heading, not a rule. Modrinth
            // descriptions use them a lot, and treating them as rules turns every section title
            // into a stray horizontal line.
            if (paragraph.Length > 0 && SetextRegex().IsMatch(line))
            {
                var spans = ParseInline(paragraph.ToString());
                paragraph.Clear();
                if (spans.Count > 0)
                    blocks.Add(new MarkdownBlock(
                        line.TrimStart().StartsWith("=", StringComparison.Ordinal)
                            ? MarkdownBlockKind.Heading1
                            : MarkdownBlockKind.Heading2,
                        spans));
                continue;
            }

            if (RuleRegex().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, Array.Empty<MarkdownSpan>()));
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                var level = Math.Min(3, heading.Groups["hashes"].Value.Length);
                var kind = level switch
                {
                    1 => MarkdownBlockKind.Heading1,
                    2 => MarkdownBlockKind.Heading2,
                    _ => MarkdownBlockKind.Heading3
                };
                var spans = ParseInline(heading.Groups["text"].Value);
                if (spans.Count > 0) blocks.Add(new MarkdownBlock(kind, spans));
                continue;
            }

            var quote = QuoteRegex().Match(line);
            if (quote.Success)
            {
                FlushParagraph();
                var spans = ParseInline(quote.Groups["text"].Value);
                if (spans.Count > 0) blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, spans));
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                var spans = ParseInline(bullet.Groups["text"].Value);
                if (spans.Count > 0) blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, spans));
                continue;
            }

            var ordered = OrderedRegex().Match(line);
            if (ordered.Success)
            {
                FlushParagraph();
                var spans = ParseInline(ordered.Groups["text"].Value);
                if (spans.Count > 0)
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Ordered, spans,
                        int.TryParse(ordered.Groups["n"].Value, out var n) ? n : 0));
                continue;
            }

            // Tables are rendered as their plain rows: the separator carries nothing, and the cell
            // pipes read better as spaces than as a broken grid.
            if (TableSeparatorRegex().IsMatch(line) && line.Contains('|')) continue;
            if (line.Contains('|') && line.TrimStart().StartsWith("|", StringComparison.Ordinal))
            {
                FlushParagraph();
                var row = line.Trim().Trim('|').Replace("|", "   ");
                var spans = ParseInline(row);
                if (spans.Count > 0) blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, spans));
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line.Trim());
        }

        if (inFence && fence.Length > 0)
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code,
                new[] { new MarkdownSpan(fence.ToString().TrimEnd(), Code: true) }));
        FlushParagraph();

        return CapLinks(blocks);
    }

    /// <summary>
    /// Demotes everything past <see cref="MaxLinks"/> to plain text. Counting afterwards keeps the
    /// parser itself simple, and the pages that hit the cap are sponsor lists where the first
    /// hundred links are already more than anyone will click.
    /// </summary>
    private static List<MarkdownBlock> CapLinks(List<MarkdownBlock> blocks)
    {
        var remaining = MaxLinks;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var links = 0;
            foreach (var span in block.Spans)
                if (span.Link is not null) links++;

            if (links == 0) continue;
            if (links <= remaining)
            {
                remaining -= links;
                continue;
            }

            var spans = new List<MarkdownSpan>(block.Spans.Count);
            foreach (var span in block.Spans)
            {
                if (span.Link is null) { spans.Add(span); continue; }
                if (remaining > 0) { remaining--; spans.Add(span); continue; }
                spans.Add(span with { Link = null });
            }
            blocks[i] = block with { Spans = spans };
        }
        return blocks;
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    /// <summary>Splits one line of text into formatted spans.</summary>
    private static List<MarkdownSpan> ParseInline(string text)
    {
        var spans = new List<MarkdownSpan>();
        if (string.IsNullOrWhiteSpace(text)) return spans;

        // Images go first, before anything else looks at the line. Sponsor banners are written as
        // an image wrapped in a link — [![alt](image)](target) — and left in place the link pattern
        // would swallow the image's own brackets and print the rest as raw text.
        text = ImageRegex().Replace(text, string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return spans;

        var position = 0;
        foreach (Match match in InlineRegex().Matches(text))
        {
            if (match.Index > position) AddPlain(spans, text[position..match.Index]);
            position = match.Index + match.Length;

            if (match.Groups["image"].Success || match.Groups["html"].Success)
                continue; // dropped on purpose

            if (match.Groups["code"].Success)
            {
                spans.Add(new MarkdownSpan(match.Groups["code"].Value.Trim('`'), Code: true));
            }
            else if (match.Groups["lurl"].Success)
            {
                var label = Decode(StripTags(match.Groups["ltext"].Value)).Trim();
                var url = match.Groups["lurl"].Value;
                // An empty label means the link only wrapped an image that was just removed — a
                // banner or a badge. There is nothing left to show, so the link goes too.
                if (label.Length == 0) continue;
                // A link the app refuses to open would be a lie as a link, so it becomes plain text.
                spans.Add(BrowserLauncher.IsWebUrl(url)
                    ? new MarkdownSpan(label, Link: url)
                    : new MarkdownSpan(label));
            }
            else if (match.Groups["bold"].Success || match.Groups["bold2"].Success)
            {
                var value = match.Groups["bold"].Success ? match.Groups["bold"].Value : match.Groups["bold2"].Value;
                spans.Add(new MarkdownSpan(Decode(value), Bold: true));
            }
            else if (match.Groups["italic"].Success || match.Groups["italic2"].Success)
            {
                var value = match.Groups["italic"].Success ? match.Groups["italic"].Value : match.Groups["italic2"].Value;
                spans.Add(new MarkdownSpan(Decode(value), Italic: true));
            }
        }

        if (position < text.Length) AddPlain(spans, text[position..]);

        // A line that was only an image or only HTML leaves nothing worth a block.
        return spans.Count == 1 && spans[0].Text.Trim().Length == 0 ? new List<MarkdownSpan>() : spans;
    }

    private static void AddPlain(List<MarkdownSpan> spans, string raw)
    {
        var value = Decode(raw);
        if (value.Length > 0) spans.Add(new MarkdownSpan(value));
    }

    [GeneratedRegex(@"<[^<>]{1,2000}>")]
    private static partial Regex AnyTagRegex();

    /// <summary>Removes any HTML left inside a link label, so it never reaches the screen raw.</summary>
    private static string StripTags(string value) => AnyTagRegex().Replace(value, string.Empty);

    /// <summary>Turns HTML entities into real characters and collapses non-breaking spaces.</summary>
    private static string Decode(string value) =>
        WebUtility.HtmlDecode(value).Replace(' ', ' ');
}
