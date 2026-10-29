using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The reader for Modrinth's long descriptions: which block each kind of line becomes, and what it
/// never lets through. Written when the parser was split into one method per block type, against a
/// differential run over thousands of random documents with the version before the split.
/// </summary>
public class MarkdownParserTests
{
    private static string Kinds(string text) =>
        string.Join(",", MarkdownParser.Parse(text).Select(b => b.Kind));

    private static string Text(string text) =>
        string.Join("|", MarkdownParser.Parse(text).Select(b => string.Concat(b.Spans.Select(s => s.Text))));

    [Fact]
    public void NothingGivesNothing()
    {
        Assert.Empty(MarkdownParser.Parse(null));
        Assert.Empty(MarkdownParser.Parse("  \n \n"));
    }

    [Fact]
    public void EachLineStartsTheBlockItLooksLike()
    {
        const string doc = "# One\n## Two\n### Three\n#### Four\n- a\n* b\n+ c\n1. x\n2) y\n> q\n---\ntext";

        Assert.Equal("Heading1,Heading2,Heading3,Heading3,Bullet,Bullet,Bullet,Ordered,Ordered,Quote,Rule,Paragraph",
            Kinds(doc));
    }

    [Fact]
    public void OrderedItemsKeepTheirNumber()
    {
        var blocks = MarkdownParser.Parse("3. third\n10) tenth");

        Assert.Equal([3, 10], blocks.Select(b => b.Number));
    }

    [Fact]
    public void AnUnderlineUnderTextMakesItAHeadingNotARule()
    {
        Assert.Equal("Heading1,Heading2", Kinds("Title\n=====\n\nSub\n-----"));
        Assert.Equal("Rule", Kinds("---"));
    }

    [Fact]
    public void LinesOfOneParagraphAreJoinedAndABlankLineEndsIt()
    {
        Assert.Equal("one two|three", Text("one\ntwo\n\nthree"));
    }

    [Fact]
    public void CodeFencesKeepTheirLinesAndAnUnclosedOneIsStillCode()
    {
        var closed = MarkdownParser.Parse("```\nint a;\nint b;\n```\nafter");
        var open = MarkdownParser.Parse("```\nnever closed");

        Assert.Equal([MarkdownBlockKind.Code, MarkdownBlockKind.Paragraph], closed.Select(b => b.Kind));
        Assert.Equal("int a;\nint b;", closed[0].Spans[0].Text);
        Assert.Equal("never closed", open[0].Spans[0].Text);
        Assert.True(open[0].Spans[0].Code);
    }

    [Fact]
    public void TableRowsBecomePlainRowsAndTheSeparatorDisappears()
    {
        Assert.Equal(" a     b | 1     2 ", Text("| a | b |\n|---|---|\n| 1 | 2 |"));
    }

    [Fact]
    public void ScriptsCommentsAndImagesNeverReachTheScreen()
    {
        var text = Text("before<script>steal()</script> <!-- note --> ![pic](http://x/y.png) after");

        Assert.DoesNotContain("steal", text);
        Assert.DoesNotContain("note", text);
        Assert.DoesNotContain("pic", text);
        Assert.Contains("before", text);
        Assert.Contains("after", text);
    }

    [Fact]
    public void ALinkTheAppWillNotOpenIsPlainText()
    {
        var web = MarkdownParser.Parse("[site](https://example.com)")[0].Spans.Single();
        var script = MarkdownParser.Parse("[x](javascript:alert(1))")[0].Spans.First();

        Assert.Equal("https://example.com", web.Link);
        Assert.Null(script.Link);
    }

    [Fact]
    public void AHugeDescriptionIsCutAndTheBlockCountIsCapped()
    {
        var long400 = string.Concat(Enumerable.Repeat("- item\n", 600));

        Assert.True(MarkdownParser.Parse(long400).Count <= 400);
        Assert.True(MarkdownParser.Parse(new string('x', 25_000))[0].Spans.Sum(s => s.Text.Length)
                    <= MarkdownParser.MaxCharacters);
    }
}
