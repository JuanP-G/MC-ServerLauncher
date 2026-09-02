using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using McServerLauncher.Localization;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The sign editor, driven the way a person drives it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MotdDocumentTests"/> covers the document underneath, and every one of those tests
/// passed while the editor was, from the outside, completely broken: selecting a word and clicking
/// a colour did nothing at all. Clicking a button moved the focus, Avalonia cleared the text box's
/// selection on the way out, and the handler was handed an empty range. The logic was right and it
/// was being asked the wrong question — which no test calling the document directly could have
/// caught.
/// </para>
/// <para>
/// Nothing here lays the dialog out. Headless Avalonia cannot create the FluentIcons typeface and
/// the preview card contains a SymbolIcon, so any layout pass reaching it throws. An earlier version
/// did call Measure and Arrange, and six of these seven passed — not because they were sound, but
/// because whether the failing pass landed inside the setup varied between them. What is under test
/// is behaviour, not pixels.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class MotdEditorTests(AvaloniaFixture avalonia)
{
    /// <summary>Opens the editor and hands back the controls these tests drive.</summary>
    /// <remarks>
    /// The logical tree, not the visual one: a visual tree only exists once something has been laid
    /// out, and deliberately nothing is.
    /// </remarks>
    private static (MotdEditorDialog Dialog, TextBox Plain, TextBox Code, List<Button> Swatches)
        Open(string sign, bool wakeOnDemand = false)
    {
        var dialog = new MotdEditorDialog(sign, "Supervivencia", "3/20", null, null, wakeOnDemand);
        var boxes = dialog.GetLogicalDescendants().OfType<TextBox>().ToList();

        return (dialog,
            boxes.First(b => b.Name == "PlainBox"),
            boxes.First(b => b.Name == "CodeBox"),
            dialog.GetLogicalDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("swatch")).ToList());
    }

    /// <summary>Selects a stretch of the clean box, the way dragging over it would.</summary>
    /// <remarks>
    /// Pumped afterwards because Avalonia raises <c>TextChanged</c> through the dispatcher rather
    /// than synchronously. An earlier version of these tests assumed it was synchronous — and said
    /// so in a comment — which meant they read the state from before their own change.
    /// </remarks>
    private static void Select(TextBox box, int start, int end)
    {
        box.SelectionStart = start;
        box.SelectionEnd = end;
        AvaloniaFixture.Pump();
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        AvaloniaFixture.Pump();
    }

    [Fact]
    public void NothingInTheToolbarCanTakeTheFocus()
    {
        // The whole bug in one line. A toolbar that takes the focus takes the selection with it,
        // and every button in it then acts on nothing at all.
        avalonia.Run(() =>
        {
            var (dialog, _, _, swatches) = Open("Bienvenido al servidor");

            Assert.NotEmpty(swatches);
            Assert.All(swatches, s => Assert.False(s.Focusable));
            Assert.All(dialog.GetLogicalDescendants().OfType<ToggleButton>()
                    .Where(t => t.Classes.Contains("mark")),
                t => Assert.False(t.Focusable));
        });
    }

    [Fact]
    public void SelectingAWordAndClickingAColourPaintsIt()
    {
        avalonia.Run(() =>
        {
            var (_, plain, code, swatches) = Open("Bienvenido al servidor");

            Select(plain, 0, 10);
            Click(swatches[10]);          // the eleventh is 'a', Minecraft's green

            Assert.StartsWith("§aBienvenido", code.Text, StringComparison.Ordinal);
            Assert.Contains("al servidor", code.Text, StringComparison.Ordinal);
            Assert.Equal("Bienvenido al servidor", plain.Text);
        });
    }

    [Fact]
    public void TheSelectionSurvivesTheClickSoAColourCanBeChanged()
    {
        // Painting twice over is ordinary use — you try green, then gold. If the selection is lost
        // on the first click, the second one silently does nothing, which is what was happening.
        avalonia.Run(() =>
        {
            var (_, plain, code, swatches) = Open("Bienvenido al servidor");

            Select(plain, 0, 10);
            Click(swatches[10]);          // green
            Click(swatches[6]);           // gold

            Assert.StartsWith("§6Bienvenido", code.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("§a", code.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheLastSwatchTakesTheColourBackOff()
    {
        avalonia.Run(() =>
        {
            var (_, plain, code, swatches) = Open("§aBienvenido al servidor");

            Select(plain, 0, 10);
            Click(swatches[^1]);          // the × : back to the default colour

            // Only the selected part loses its colour. The rest keeps it, and must.
            Assert.StartsWith("Bienvenido", code.Text, StringComparison.Ordinal);
            Assert.Contains("§a al servidor", code.Text, StringComparison.Ordinal);
            Assert.Equal("Bienvenido al servidor", plain.Text);
        });
    }

    [Fact]
    public void WithNothingSelectedAColourChangesNothing()
    {
        // A caret is not a selection. Painting the whole line because nothing was marked would be a
        // surprise, and an expensive one to undo by hand.
        avalonia.Run(() =>
        {
            var (_, plain, code, swatches) = Open("Bienvenido al servidor");

            Select(plain, 4, 4);
            var before = code.Text;
            Click(swatches[10]);

            Assert.Equal(before, code.Text);
        });
    }

    [Fact]
    public void WhatIsSavedIsTheSignAndNotTheWordsAlone()
    {
        avalonia.Run(() =>
        {
            var (dialog, plain, _, swatches) = Open("Bienvenido al servidor");

            Select(plain, 0, 10);
            Click(swatches[10]);

            Click(dialog.GetLogicalDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("accent")));

            Assert.StartsWith("§aBienvenido", dialog.Result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PastingACodedSignIntoTheCodeBoxReachesTheCleanBox()
    {
        // The point of the lower box: somebody else's sign goes in and comes out understood, with
        // no import step, because that box is the document.
        avalonia.Run(() =>
        {
            var (_, plain, code, _) = Open("x");

            code.Text = "§6§lHola §bmundo";
            AvaloniaFixture.Pump();

            Assert.Equal("Hola mundo", plain.Text);
        });
    }

    /// <summary>The text the preview card is actually showing, whichever sign that is.</summary>
    private static string PreviewOf(MotdEditorDialog dialog) =>
        string.Concat(dialog.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Name == "PreviewText")
            .Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []);

    [Fact]
    public void WithoutWakeOnDemandThereIsOnlyOneSignToSee()
    {
        // No switch, because there is no second sign: a server that does not answer while stopped
        // shows nothing at all in the list, so "asleep" is not a state anybody can look at.
        avalonia.Run(() =>
        {
            var (dialog, _, _, _) = Open("Bienvenido");

            var sw = dialog.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Name == "StateSwitch");
            Assert.False(sw.IsVisible);
            Assert.Equal("Bienvenido", PreviewOf(dialog));
        });
    }

    [Fact]
    public void WithWakeOnDemandThePreviewOpensOnTheSleepingSign()
    {
        // The whole point of the switch. The sign a sleeping server shows is NOT the one you wrote —
        // your second line gives way to the launcher's notice, because the list draws two lines and
        // no more. That was always true; what was missing was any way to see it before a friend
        // told you.
        avalonia.Run(() =>
        {
            var (dialog, _, _, _) = Open("Linea uno" + (char)10 + "Linea dos", wakeOnDemand: true);

            var sw = dialog.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Name == "StateSwitch");
            Assert.True(sw.IsVisible);

            var shown = PreviewOf(dialog);
            Assert.Contains("Linea uno", shown, StringComparison.Ordinal);
            Assert.Contains(Localizer.Get("Wake_MotdSleeping"), shown, StringComparison.Ordinal);
            Assert.DoesNotContain("Linea dos", shown, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SwitchingBackShowsTheSignYouWrote()
    {
        avalonia.Run(() =>
        {
            var (dialog, _, _, _) = Open("Linea uno" + (char)10 + "Linea dos", wakeOnDemand: true);

            var on = dialog.GetLogicalDescendants().OfType<ToggleButton>().First(t => t.Name == "PreviewOn");
            on.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AvaloniaFixture.Pump();

            var shown = PreviewOf(dialog);
            Assert.Contains("Linea dos", shown, StringComparison.Ordinal);
            Assert.DoesNotContain(Localizer.Get("Wake_MotdSleeping"), shown, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TypingInTheCleanBoxKeepsTheColourAlreadyThere()
    {
        // Finishing a gold word must not turn the new letters grey.
        avalonia.Run(() =>
        {
            var (_, plain, code, _) = Open("§6Oro");

            plain.Text = "Oro puro";
            AvaloniaFixture.Pump();

            Assert.Equal("§6Oro puro", code.Text);
        });
    }
}
