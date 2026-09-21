using McServerLauncher.Behaviors;

namespace McServerLauncher.Tests;

/// <summary>
/// The console's auto-scroll asks for one scroll per burst, not one per line.
/// </summary>
/// <remarks>
/// The scroll itself is a <c>ScrollIntoView</c> on a virtualized list of up to two thousand wrapped
/// lines, which is the most expensive thing on that path; the folding is what keeps a chatty server
/// from paying it dozens of times a second, and what keeps selecting another server from paying it
/// at all until the new console is on screen.
/// </remarks>
public class ConsoleAutoScrollTests
{
    /// <summary>A queue standing in for the dispatcher, so nothing here needs a UI thread.</summary>
    private sealed class Pump
    {
        private readonly List<Action> _queued = new();

        public int Queued => _queued.Count;

        public void Post(Action action) => _queued.Add(action);

        public void Run()
        {
            var due = _queued.ToList();
            _queued.Clear();
            foreach (var action in due) action();
        }
    }

    [Fact]
    public void ABurstOfRequestsBecomesOneScroll()
    {
        var pump = new Pump();
        var scrolls = 0;
        var coalescer = new ScrollCoalescer(pump.Post, () => scrolls++);

        for (var i = 0; i < 500; i++) coalescer.Request();

        Assert.Equal(1, pump.Queued);
        pump.Run();
        Assert.Equal(1, scrolls);
    }

    [Fact]
    public void TheNextBurstIsScrolledToAsWell()
    {
        var pump = new Pump();
        var scrolls = 0;
        var coalescer = new ScrollCoalescer(pump.Post, () => scrolls++);

        coalescer.Request();
        pump.Run();
        coalescer.Request();
        coalescer.Request();
        pump.Run();

        Assert.Equal(2, scrolls);
    }

    [Fact]
    public void NothingIsScrolledUntilThePumpRuns()
    {
        var pump = new Pump();
        var scrolls = 0;
        var coalescer = new ScrollCoalescer(pump.Post, () => scrolls++);

        coalescer.Request();

        // The point of the whole exercise: the request never runs inline, so switching server does
        // not stop to measure two thousand lines before the new console can be drawn.
        Assert.Equal(0, scrolls);
    }

    [Fact]
    public void AScrollThatAsksForAnotherOneGetsIt()
    {
        var pump = new Pump();
        var scrolls = 0;
        ScrollCoalescer? coalescer = null;
        coalescer = new ScrollCoalescer(pump.Post, () =>
        {
            scrolls++;
            if (scrolls == 1) coalescer!.Request(); // e.g. the scroll itself realizes more items
        });

        coalescer.Request();
        pump.Run();
        Assert.Equal(1, pump.Queued);
        pump.Run();

        Assert.Equal(2, scrolls);
    }
}
