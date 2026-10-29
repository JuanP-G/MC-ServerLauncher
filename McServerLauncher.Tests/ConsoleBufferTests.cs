using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The console's buffer on its own: how much it keeps, how it hides what the filters hide, and what
/// it tells the view model, with no server behind it.
/// </summary>
[Collection("avalonia")]
public class ConsoleBufferTests(AvaloniaFixture ui)
{
    private static ConsoleLine Line(string text, ConsoleLineKind kind = ConsoleLineKind.Info) => new(text, kind);

    [Fact]
    public void ALineEnqueuedOnTheUiThreadIsShownAtOnce()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();

            buffer.Enqueue(Line("hello"));

            Assert.Equal(["hello"], buffer.Lines.Select(l => l.Text));
            Assert.Equal(["hello"], buffer.Visible.Select(l => l.Text));
        });
    }

    [Fact]
    public void ABatchIsReportedOnceAndInOrder()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();
            var batches = new List<IReadOnlyList<ConsoleLine>>();
            buffer.Flushed += batches.Add;

            buffer.Enqueue(Line("a"));
            buffer.Enqueue(Line("b"));

            // Each enqueue on the UI thread flushes at once: two batches of one, in order.
            Assert.Equal(["a", "b"], batches.SelectMany(b => b).Select(l => l.Text));
        });
    }

    [Fact]
    public void LinesFromAnotherThreadWaitForTheUiThreadAndArriveTogether()
    {
        ConsoleBuffer buffer = null!;
        var batches = new List<int>();
        ui.Run(() =>
        {
            buffer = new ConsoleBuffer();
            buffer.Flushed += b => batches.Add(b.Count);
        });

        Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) buffer.Enqueue(Line("line " + i));
        }).Wait();

        // Nothing has reached the screen yet: the UI thread has not been given a turn.
        ui.Run(() =>
        {
            AvaloniaFixture.Pump();
            Assert.Equal(50, buffer.Lines.Count);
            Assert.Equal(50, batches.Sum());
            Assert.True(batches.Count < 50, "one trip per line is what the buffer exists to avoid");
        });
    }

    [Fact]
    public void ASwitchedOffKindIsHiddenButCountedAndMarkedUnseen()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();
            var chat = buffer.Kinds.Single(k => k.Kind == ConsoleLineKind.Chat);
            chat.IsOn = false;

            buffer.Enqueue(Line("<Steve> hi", ConsoleLineKind.Chat));
            buffer.Enqueue(Line("Done", ConsoleLineKind.Info));

            Assert.Equal(2, buffer.Lines.Count);
            Assert.Equal(["Done"], buffer.Visible.Select(l => l.Text));
            Assert.Equal(1, chat.Count);
            Assert.True(chat.HasUnseen);
            Assert.True(buffer.IsFiltered);
        });
    }

    [Fact]
    public void TheSearchTextFiltersWhatIsShownAndKeepsEverythingElseHeld()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();
            buffer.Enqueue(Line("Starting server"));
            buffer.Enqueue(Line("Done (3.1s)!"));

            buffer.Filter = "done";

            Assert.Equal(["Done (3.1s)!"], buffer.Visible.Select(l => l.Text));
            Assert.Equal(2, buffer.Lines.Count);
        });
    }

    [Fact]
    public void ShowAllUndoesTheSearchAndTheSwitches()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();
            buffer.Kinds[0].IsOn = false;
            buffer.Filter = "x";

            buffer.ShowAllKinds();

            Assert.False(buffer.IsFiltered);
            Assert.Equal(string.Empty, buffer.Filter);
        });
    }

    [Fact]
    public void RebuildingTellsTheOwnerSoItCanReRaiseIsFiltered()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();
            var rebuilt = 0;
            buffer.Rebuilt += () => rebuilt++;

            buffer.Filter = "a";
            buffer.Kinds[0].IsOn = false;

            Assert.Equal(2, rebuilt);
        });
    }

    [Fact]
    public void PastTheCapTheOldestLinesFallOffInOneBlock()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();
            var total = ConsoleBuffer.MaxLines + ConsoleBuffer.TrimBlock + 1;

            for (var i = 0; i < total; i++) buffer.Enqueue(Line("line " + i));

            Assert.Equal(ConsoleBuffer.MaxLines, buffer.Lines.Count);
            Assert.Equal("line " + (total - ConsoleBuffer.MaxLines), buffer.Lines[0].Text);
            Assert.Equal("line " + (total - 1), buffer.Lines[^1].Text);
            Assert.Equal(buffer.Lines.Count, buffer.Visible.Count);
        });
    }

    [Fact]
    public void ClearEmptiesTheListsAndTheCounters()
    {
        ui.Run(() =>
        {
            var buffer = new ConsoleBuffer();
            buffer.Enqueue(Line("a"));

            buffer.Clear();

            Assert.Empty(buffer.Lines);
            Assert.Empty(buffer.Visible);
            Assert.All(buffer.Kinds, k => Assert.Equal(0, k.Count));
        });
    }
}
