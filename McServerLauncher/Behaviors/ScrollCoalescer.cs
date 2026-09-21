namespace McServerLauncher.Behaviors;

/// <summary>
/// Turns a burst of "scroll to the end" requests into a single scroll.
/// </summary>
/// <remarks>
/// <para>
/// A running server appends a line at a time, and each line used to schedule its own
/// <c>ScrollIntoView</c>. On a list of two thousand wrapped lines that measurement is not free, so a
/// chatty server (Forge booting, a datapack reloading) paid it dozens of times for a screen that can
/// only ever end up in one place: the bottom.
/// </para>
/// <para>
/// So the requests are folded together. While one is already queued, the rest are dropped — the
/// scroll that eventually runs sees the final state anyway. The way to queue work is handed in
/// rather than taken from the dispatcher, so that the folding can be tested without a UI thread.
/// </para>
/// </remarks>
internal sealed class ScrollCoalescer
{
    private readonly Action<Action> _post;
    private readonly Action _scroll;
    private bool _pending;

    internal ScrollCoalescer(Action<Action> post, Action scroll)
    {
        _post = post;
        _scroll = scroll;
    }

    /// <summary>Asks for a scroll, unless one is already on its way.</summary>
    internal void Request()
    {
        if (_pending) return;
        _pending = true;
        _post(Run);
    }

    private void Run()
    {
        // Cleared first: a request raised by the scroll itself must be able to queue the next one
        // rather than be swallowed.
        _pending = false;
        _scroll();
    }
}
