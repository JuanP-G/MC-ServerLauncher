using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The MOTD as two lines of styled characters, and the trip to and from <c>server.properties</c>.
/// </summary>
public class MotdDocumentTests
{
    private static MotdStyle Gold => new('6', MotdFormat.None);
    private static MotdStyle GoldBold => new('6', MotdFormat.Bold);

    // --- reading ---

    [Fact]
    public void ReadsColoursAndFormatsPerCharacter()
    {
        var doc = MotdDocument.FromProperties("§6Hi §lyou§r!");

        Assert.Equal("Hi you!", doc.GetText(0));
        Assert.Equal(Gold, doc.StyleAt(0, 0));
        Assert.Equal(Gold, doc.StyleAt(0, 2));
        Assert.Equal(GoldBold, doc.StyleAt(0, 3));
        Assert.Equal(GoldBold, doc.StyleAt(0, 5));
        Assert.Equal(MotdStyle.Plain, doc.StyleAt(0, 6));
    }

    [Fact]
    public void ReadsTheEscapedFormTheFileActuallyHolds()
    {
        var doc = MotdDocument.FromProperties(@"\u00a7bSurvival\n\u00a7aVanilla");

        Assert.Equal("Survival", doc.GetText(0));
        Assert.Equal("Vanilla", doc.GetText(1));
        Assert.Equal('b', doc.StyleAt(0, 0).Color);
        Assert.Equal('a', doc.StyleAt(1, 0).Color);
    }

    [Fact]
    public void AColourWipesTheFormatting()
    {
        var doc = MotdDocument.FromProperties("§l§aX");
        Assert.Equal(new MotdStyle('a', MotdFormat.None), doc.StyleAt(0, 0));
    }

    [Fact]
    public void StyleCarriesOverTheLineBreakAsItDoesInTheGame()
    {
        var doc = MotdDocument.FromProperties(@"§6one\ntwo");
        Assert.Equal(Gold, doc.StyleAt(1, 0));
    }

    [Fact]
    public void ExtraLinesAreFlaggedAndNotKept()
    {
        var doc = MotdDocument.FromProperties(@"a\nb\nc");

        Assert.True(doc.HadExtraLines);
        Assert.Equal("b", doc.GetText(1));
    }

    [Fact]
    public void ObfuscatedAndUnknownCodesAreDroppedButTheTextStays()
    {
        var doc = MotdDocument.FromProperties("§kab§zc");
        Assert.Equal("abc", doc.GetText(0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingIsTwoEmptyLines(string? raw)
    {
        var doc = MotdDocument.FromProperties(raw);
        Assert.Equal("", doc.GetText(0));
        Assert.Equal("", doc.GetText(1));
        Assert.Equal("", doc.ToProperties());
    }

    // --- writing ---

    [Theory]
    [InlineData("A Minecraft Server")]
    [InlineData(@"\u00a76Survival \u00a78| \u00a7aVanilla")]
    [InlineData(@"\u00a7l\u00a7nBold underline")]
    [InlineData(@"\u00a7cRed\n\u00a7rplain")]
    public void WhatIsReadIsWrittenBackUnchanged(string raw) =>
        Assert.Equal(raw, MotdDocument.FromProperties(raw).ToProperties());

    [Fact]
    public void TheBreakIsWrittenAsTwoCharactersNeverARealNewline()
    {
        // A real newline would end the line in the properties file and turn the rest of the MOTD
        // into a stray key.
        var doc = MotdDocument.FromProperties(@"a\nb");
        var written = doc.ToProperties();

        Assert.DoesNotContain('\n', written);
        Assert.Equal(@"a\nb", written);
    }

    [Fact]
    public void NonAsciiIsEscapedSoOlderServersReadItBack()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "Ñandú · 1");

        Assert.Equal(@"\u00d1and\u00fa \u00b7 1", doc.ToProperties());
        Assert.Equal("Ñandú · 1", MotdDocument.FromProperties(doc.ToProperties()).GetText(0));
    }

    [Fact]
    public void ABackslashSurvivesTheRoundTrip()
    {
        var doc = new MotdDocument();
        doc.SetText(0, @"a\b");

        Assert.Equal(@"a\\b", doc.ToProperties());
        Assert.Equal(@"a\b", MotdDocument.FromProperties(doc.ToProperties()).GetText(0));
    }

    [Fact]
    public void EdgeSpacesAreEscapedBecauseJavaDropsThem()
    {
        var doc = new MotdDocument();
        doc.SetText(0, " hi ");

        Assert.Equal(@"\u0020hi\u0020", doc.ToProperties());
        Assert.Equal(" hi ", MotdDocument.FromProperties(doc.ToProperties()).GetText(0));
    }

    [Fact]
    public void RemovingAFormatNeedsAResetBeforeTheNextRun()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "ab");
        doc.SetColor(0, 0, 2, '6');
        doc.SetFormat(0, 0, 1, MotdFormat.Bold, true);

        // a: gold+bold, b: gold. Bold cannot be switched off, so b starts over.
        Assert.Equal("§6§la§r§6b", doc.ToCodes());
    }

    [Fact]
    public void ASecondLineEndsTheFirstLinesStyleWithAReset()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "one");
        doc.SetText(1, "two");
        doc.SetColor(0, 0, 3, '6');

        Assert.Equal("§6one\n§rtwo", doc.ToCodes());
    }

    [Fact]
    public void AnEmptySecondLineIsNotWrittenAtAll()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "solo");

        Assert.Equal("solo", doc.ToProperties());
    }

    [Fact]
    public void AnEmptyFirstLineStillKeepsTheSecond()
    {
        var doc = new MotdDocument();
        doc.SetText(1, "abajo");

        Assert.Equal(@"\nabajo", doc.ToProperties());
    }

    // --- editing ---

    [Fact]
    public void TypingInTheMiddleKeepsTheStylesAround()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "ab");
        doc.SetColor(0, 0, 1, 'a');
        doc.SetColor(0, 1, 1, 'c');

        doc.SetText(0, "axb", caret: 2);

        Assert.Equal('a', doc.StyleAt(0, 0).Color);
        Assert.Equal('a', doc.StyleAt(0, 1).Color);  // typed text takes the style before it
        Assert.Equal('c', doc.StyleAt(0, 2).Color);
    }

    [Fact]
    public void TypingNextToTheSameLetterAttributesTheEditToTheCaret()
    {
        // "ab" → "aab" is the same string whether the new "a" went in front or behind the old one.
        // The caret says which, and with it the styles of the two a's do not swap.
        var doc = new MotdDocument();
        doc.SetText(0, "ab");
        doc.SetColor(0, 0, 1, 'c');

        doc.SetText(0, "aab", caret: 1);   // typed at the very start

        Assert.Equal('c', doc.StyleAt(0, 0).Color);  // new one, inherits the following character
        Assert.Equal('c', doc.StyleAt(0, 1).Color);  // the original, unchanged
        Assert.Equal('\0', doc.StyleAt(0, 2).Color);
    }

    [Fact]
    public void DeletingKeepsTheRest()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "abc");
        doc.SetColor(0, 0, 1, 'a');
        doc.SetColor(0, 1, 1, 'b');
        doc.SetColor(0, 2, 1, 'c');

        doc.SetText(0, "ac", caret: 1);

        Assert.Equal("ac", doc.GetText(0));
        Assert.Equal('a', doc.StyleAt(0, 0).Color);
        Assert.Equal('c', doc.StyleAt(0, 1).Color);
    }

    [Fact]
    public void TypingOverASelectionTakesItsStyle()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "abc");
        doc.SetColor(0, 0, 3, 'e');

        doc.SetText(0, "x", caret: 1);

        Assert.Equal("x", doc.GetText(0));
        Assert.Equal('e', doc.StyleAt(0, 0).Color);
    }

    [Fact]
    public void ThePendingStyleWinsOverTheNeighbour()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "ab");

        doc.SetText(0, "abc", caret: 3, typing: GoldBold);

        Assert.Equal(GoldBold, doc.StyleAt(0, 2));
        Assert.Equal(MotdStyle.Plain, doc.StyleAt(0, 1));
    }

    [Fact]
    public void TextCannotCarryACodeOrABreak()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "a§cb\r\nc");

        Assert.Equal("acbc", doc.GetText(0));
    }

    [Fact]
    public void FormatsToggleOverARange()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "abc");

        Assert.False(doc.AllHave(0, 0, 3, MotdFormat.Bold));
        doc.SetFormat(0, 0, 3, MotdFormat.Bold, true);
        Assert.True(doc.AllHave(0, 0, 3, MotdFormat.Bold));
        doc.SetFormat(0, 1, 1, MotdFormat.Bold, false);
        Assert.False(doc.AllHave(0, 0, 3, MotdFormat.Bold));
        Assert.False(doc.AllHave(0, 0, 0, MotdFormat.Bold));   // nothing selected is not "all bold"
    }

    [Fact]
    public void ClearingStyleGoesBackToDefaultEverywhereInTheRange()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "ab");
        doc.SetColor(0, 0, 2, 'c');
        doc.SetFormat(0, 0, 2, MotdFormat.Italic, true);

        doc.ClearStyle(0, 0, 1);

        Assert.Equal(MotdStyle.Plain, doc.StyleAt(0, 0));
        Assert.Equal('c', doc.StyleAt(0, 1).Color);
    }

    [Fact]
    public void ARangePastTheEndIsClampedNotAnError()
    {
        var doc = new MotdDocument();
        doc.SetText(0, "ab");

        doc.SetColor(0, 1, 50, 'a');
        doc.SetColor(0, 9, 1, 'a');

        Assert.Equal('a', doc.StyleAt(0, 1).Color);
        Assert.Equal('\0', doc.StyleAt(0, 0).Color);
    }
}
