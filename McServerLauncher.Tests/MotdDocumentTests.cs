using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The server's sign as styled runs, and back again as <c>§</c> codes.
/// </summary>
/// <remarks>
/// The editor was the expensive half of this work until the code string became the only original.
/// With one model instead of two there is nothing to synchronise, and what is left is a handful of
/// plain functions over plain data — which is why the cases that actually break editors (typing in
/// the middle of a coloured word, deleting across a join, pasting) can be pinned down here rather
/// than found by hand in a dialog.
/// </remarks>
public class MotdDocumentTests
{
    private static string Round(string code) => MotdDocument.ToCode(MotdDocument.Parse(code));

    // --- Reading ---

    [Fact]
    public void PlainTextIsOneRunWithNoColour()
    {
        var runs = MotdDocument.Parse("Un servidor");

        Assert.Single(runs);
        Assert.Equal("Un servidor", runs[0].Text);
        Assert.Null(runs[0].Colour);
    }

    [Fact]
    public void EachColourStartsItsOwnRun()
    {
        var runs = MotdDocument.Parse("§aVerde §6Dorado");

        Assert.Equal(2, runs.Count);
        Assert.Equal(('a', "Verde "), (runs[0].Colour, runs[0].Text));
        Assert.Equal(('6', "Dorado"), (runs[1].Colour, runs[1].Text));
    }

    [Fact]
    public void AColourClearsTheMarksJustLikeTheGame()
    {
        // Not a quirk of ours — it is what Minecraft does, and a sign written elsewhere relies on it.
        var runs = MotdDocument.Parse("§lNegrita§aNormal");

        Assert.True(runs[0].Bold);
        Assert.False(runs[1].Bold);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var runs = MotdDocument.Parse("§6§lFuerte§rSuave");

        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Bold);
        Assert.Null(runs[1].Colour);
        Assert.False(runs[1].Bold);
    }

    [Fact]
    public void AmpersandIsReadAsWellAsTheSectionSign()
    {
        // Half the internet writes signs with &. Refusing them would break pasting from anywhere.
        Assert.Equal('a', MotdDocument.Parse("&aVerde")[0].Colour);
    }

    [Fact]
    public void ObfuscatedAndUnknownCodesAreSwallowedNotPrinted()
    {
        // §k has nothing sensible to draw, and an unknown code is not text. Printing either would
        // put stray letters in the middle of somebody's sign.
        Assert.Equal("Hola", MotdDocument.PlainText(MotdDocument.Parse("§kHo§zla")));
    }

    [Fact]
    public void ATrailingCodeWithNothingAfterItIsJustText()
    {
        // "50% off §" — the sign ends on the introducer. It must not eat the following character
        // because there is not one.
        Assert.Equal("Fin §", MotdDocument.PlainText(MotdDocument.Parse("Fin §")));
    }

    [Fact]
    public void TwoLinesSurviveTheEscapingInServerProperties()
    {
        // server.properties is line-oriented, so a two-line sign is stored as a backslash and an n.
        var runs = MotdDocument.Parse(@"Arriba\nAbajo");

        Assert.Equal("Arriba\nAbajo", MotdDocument.PlainText(runs));
        Assert.Equal(@"Arriba\nAbajo", MotdDocument.Escape(MotdDocument.ToCode(runs)));
    }

    // --- Writing ---

    [Fact]
    public void WhatGoesInComesOut()
    {
        Assert.Equal("§aBienvenido §7al servidor", Round("§aBienvenido §7al servidor"));
    }

    [Fact]
    public void ASignDoesNotGrowEveryTimeItIsOpenedAndSaved()
    {
        // Redundant codes would accumulate silently until the sign hit the client's length limit.
        var once = Round("§6§lLos findes§r normal");
        Assert.Equal(once, Round(once));
        Assert.Equal(once, Round(Round(once)));
    }

    [Fact]
    public void DroppingAMarkEmitsAReset()
    {
        // There is no code for "stop being bold". Without the reset the rest of the sign would stay
        // bold in the client while the preview showed it plain.
        var runs = new[] { new MotdRun("Fuerte", '6', Bold: true), new MotdRun("suave", '6') };

        Assert.Contains("§r", MotdDocument.ToCode(runs));
        Assert.False(MotdDocument.Parse(MotdDocument.ToCode(runs))[1].Bold);
    }

    [Fact]
    public void EmptyRunsWriteNothing()
    {
        Assert.Equal("Hola", MotdDocument.ToCode(new[]
        {
            new MotdRun(string.Empty, 'a'), new MotdRun("Hola"), new MotdRun(string.Empty, '6'),
        }));
    }

    // --- Styling a selection ---

    [Fact]
    public void ColouringTheMiddleOfARunSplitsItInThree()
    {
        var runs = MotdDocument.Colour(MotdDocument.Parse("abcdef"), 2, 2, '6');

        Assert.Equal(3, runs.Count);
        Assert.Equal(new[] { "ab", "cd", "ef" }, runs.Select(r => r.Text).ToArray());
        Assert.Equal('6', runs[1].Colour);
    }

    [Fact]
    public void StylingAcrossTwoRunsKeepsTheirColours()
    {
        // Bold spans the join; the two colours either side of it must survive.
        var runs = MotdDocument.Style(MotdDocument.Parse("§aVerde§6Oro"), 3, 5, MotdStyle.Bold, on: true);

        Assert.All(runs.Where(r => r.Bold), r => Assert.NotNull(r.Colour));
        Assert.Equal("VerdeOro", MotdDocument.PlainText(runs));
        Assert.Contains(runs, r => r is { Colour: 'a', Bold: true });
        Assert.Contains(runs, r => r is { Colour: '6', Bold: true });
    }

    [Fact]
    public void StylingAndUnstylingLeavesTheSignExactlyAsItWas()
    {
        // The merge is what makes this true. Without it the sign would fragment a little on every
        // edit and the code string would fill with codes that change nothing.
        var original = MotdDocument.Parse("§aBienvenido");
        var there = MotdDocument.Style(original, 3, 4, MotdStyle.Bold, on: true);
        var back = MotdDocument.Style(there, 3, 4, MotdStyle.Bold, on: false);

        Assert.Single(back);
        Assert.Equal(MotdDocument.ToCode(original), MotdDocument.ToCode(back));
    }

    [Fact]
    public void AnEmptySelectionChangesNothing()
    {
        var original = MotdDocument.Parse("§aVerde");
        Assert.Equal(MotdDocument.ToCode(original),
            MotdDocument.ToCode(MotdDocument.Colour(original, 2, 0, '6')));
    }

    [Fact]
    public void ClearingStripsColourAndMarksTogether()
    {
        var runs = MotdDocument.Clear(MotdDocument.Parse("§6§lFuerte"), 0, 6);

        Assert.Single(runs);
        Assert.Null(runs[0].Colour);
        Assert.False(runs[0].Bold);
    }

    // --- Typing, deleting, pasting ---

    [Fact]
    public void TypingAtTheEndOfAColouredWordContinuesInThatColour()
    {
        // What every editor does, and what people expect. Inserting in the default colour instead
        // would make finishing a word change its colour halfway through.
        var runs = MotdDocument.Replace(MotdDocument.Parse("§6Oro"), 3, 0, " puro");

        Assert.Single(runs);
        Assert.Equal("Oro puro", runs[0].Text);
        Assert.Equal('6', runs[0].Colour);
    }

    [Fact]
    public void TypingAtTheEndTakesTheLastRunsLookAndNotTheFirsts()
    {
        // The case that tells "the style before the cursor" apart from "the first style in the
        // sign". Every earlier test here happened to have the same colour in both places, so
        // getting this wrong passed all of them — the sabotage found it, not the suite.
        var runs = MotdDocument.Replace(MotdDocument.Parse("§aVerde§6Oro"), 8, 0, "!");

        Assert.Equal("VerdeOro!", MotdDocument.PlainText(runs));
        Assert.Equal('6', runs[^1].Colour);
    }

    [Fact]
    public void TypingAtTheVeryStartTakesTheFirstRunsLook()
    {
        var runs = MotdDocument.Replace(MotdDocument.Parse("§aVerde§6Oro"), 0, 0, ">");

        Assert.Equal(">VerdeOro", MotdDocument.PlainText(runs));
        Assert.Equal('a', runs[0].Colour);
    }

    [Fact]
    public void TypingOnAJoinContinuesTheRunThatEndsThere()
    {
        // Right between "Verde" and "Oro". Both readings are defensible; continuing the run that
        // ends there is what an editor does, and it is what the caret looks like it is doing.
        var runs = MotdDocument.Replace(MotdDocument.Parse("§aVerde§6Oro"), 5, 0, "-");

        Assert.Equal("Verde-Oro", MotdDocument.PlainText(runs));
        Assert.Equal('a', runs[0].Colour);
        Assert.Equal("Verde-", runs[0].Text);
    }

    [Fact]
    public void TypingInTheMiddleOfARunStaysInsideIt()
    {
        var runs = MotdDocument.Replace(MotdDocument.Parse("§6Oro"), 1, 0, "X");

        Assert.Single(runs);
        Assert.Equal("OXro", runs[0].Text);
        Assert.Equal('6', runs[0].Colour);
    }

    [Fact]
    public void DeletingAcrossTheJoinBetweenTwoColoursKeepsBothEnds()
    {
        // The case that breaks these editors. "Verde" + "Oro"; take the last two of one and the
        // first two of the other.
        var runs = MotdDocument.Replace(MotdDocument.Parse("§aVerde§6Oro"), 3, 4, string.Empty);

        Assert.Equal("Vero", MotdDocument.PlainText(runs));
        Assert.Equal(new char?[] { 'a', '6' }, runs.Select(r => r.Colour).ToArray());
    }

    [Fact]
    public void DeletingEverythingLeavesNothingRatherThanAnEmptyRun()
    {
        Assert.Empty(MotdDocument.Replace(MotdDocument.Parse("§aVerde"), 0, 5, string.Empty));
    }

    [Fact]
    public void TypingIntoAnEmptySignWorks()
    {
        var runs = MotdDocument.Replace(MotdDocument.Parse(string.Empty), 0, 0, "Hola");

        Assert.Single(runs);
        Assert.Equal("Hola", runs[0].Text);
        Assert.Null(runs[0].Colour);
    }

    [Fact]
    public void ReplacingASelectionTakesTheLookOfWhatWasBeforeIt()
    {
        var runs = MotdDocument.Replace(MotdDocument.Parse("§aVerde §6Oro"), 6, 3, "Plata");

        Assert.Equal("Verde Plata", MotdDocument.PlainText(runs));
        Assert.Equal('a', runs[^1].Colour);
    }

    [Fact]
    public void EditingNeverLosesOrInventsCharacters()
    {
        // The invariant underneath all of the above: plain text after an edit is exactly the plain
        // text before, with the stretch swapped. Anything else is a lost or duplicated letter.
        const string code = "§aBienvenido §7al §6§lservidor";
        var plain = MotdDocument.PlainText(MotdDocument.Parse(code));

        for (var start = 0; start <= plain.Length; start++)
            for (var length = 0; length <= plain.Length - start; length++)
            {
                var edited = MotdDocument.Replace(MotdDocument.Parse(code), start, length, "XY");
                var expected = plain[..start] + "XY" + plain[(start + length)..];

                Assert.Equal(expected, MotdDocument.PlainText(edited));
            }
    }

    [Fact]
    public void StylingNeverLosesOrInventsCharacters()
    {
        const string code = "§aBienvenido §7al §6servidor";
        var plain = MotdDocument.PlainText(MotdDocument.Parse(code));

        for (var start = 0; start < plain.Length; start++)
            for (var length = 1; length <= plain.Length - start; length++)
                Assert.Equal(plain, MotdDocument.PlainText(
                    MotdDocument.Colour(MotdDocument.Parse(code), start, length, '9')));
    }

    // --- Importing a pasted sign ---

    [Fact]
    public void OnlyTheSectionSignCountsAsFormatting()
    {
        Assert.True(MotdDocument.LooksCoded("§aVerde"));
        Assert.False(MotdDocument.LooksCoded("Sin codigos"));
        Assert.False(MotdDocument.LooksCoded(null));
    }

    [Fact]
    public void AnAmpersandInOrdinaryTextIsNotFormatting()
    {
        // "&m" is strikethrough. Importing on sight would eat half of this name — which is why the
        // dialog asks about & instead of assuming.
        Assert.False(MotdDocument.LooksCoded("Juan &Mar"));
    }
}
