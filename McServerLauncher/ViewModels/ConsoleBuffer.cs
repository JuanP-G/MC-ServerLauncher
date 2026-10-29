using Avalonia.Threading;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The lines of one server's console: what is held, what is shown after the filters, and how lines
/// from any thread get onto the screen without making the window stutter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Arrival.</b> The server's lines come in on its output threads, sometimes thousands in a few
/// seconds while a modpack loads. <see cref="Enqueue"/> only queues; one trip to the UI thread
/// drains however many are waiting, instead of one per line — that is what made the window stutter
/// while a big server started. The app's own lines are written on the UI thread and appear at once,
/// in order with whatever the server had already queued.
/// </para>
/// <para>
/// <b>Holding.</b> The list is capped at <see cref="MaxLines"/>. It is trimmed in blocks: one
/// <c>RemoveAt(0)</c> per line was an O(n) shift plus a UI notification for every line once the cap
/// was reached. Letting the list overshoot by <see cref="TrimBlock"/> and cutting back in a single
/// bulk operation makes the per-line cost amortized O(1), at the price of momentarily holding up to
/// 2200 lines.
/// </para>
/// <para>
/// <b>Showing.</b> <see cref="Visible"/> is all of <see cref="Lines"/> when nothing is filtered, or
/// the matching subset — kept in order and updated incrementally as lines arrive — while a search is
/// typed or a category is switched off.
/// </para>
/// </remarks>
internal sealed class ConsoleBuffer
{
    /// <summary>The most lines kept; older ones fall off the top.</summary>
    public const int MaxLines = 2000;

    /// <summary>How far past <see cref="MaxLines"/> the list may grow before it is cut back.</summary>
    public const int TrimBlock = 200;

    private readonly Dictionary<ConsoleLineKind, ConsoleKindFilter> _kindFilters;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ConsoleLine> _pending = new();

    /// <summary>1 while a flush is posted and has not run yet.</summary>
    private int _flushScheduled;

    private string _filter = string.Empty;

    public ConsoleBuffer()
    {
        // Ordered by how much they matter rather than by the enum, so the two that make somebody
        // open the console at all come first and are never the ones that wrap out of sight.
        Kinds = new[]
        {
            ConsoleLineKind.Error, ConsoleLineKind.Warn, ConsoleLineKind.Chat, ConsoleLineKind.Players,
            ConsoleLineKind.Command, ConsoleLineKind.Launcher, ConsoleLineKind.Info
        }.Select(k => new ConsoleKindFilter(k)).ToList();

        _kindFilters = Kinds.ToDictionary(k => k.Kind);
        foreach (var filter in Kinds)
            filter.PropertyChanged += OnKindChanged;
        RefreshColours();
    }

    /// <summary>Lines of the embedded console (server stdout/stderr + the launcher's own).</summary>
    public BulkObservableCollection<ConsoleLine> Lines { get; } = new();

    /// <summary>The lines currently shown: <see cref="Lines"/> after the search and the switches.</summary>
    public BulkObservableCollection<ConsoleLine> Visible { get; } = new();

    /// <summary>One switch per kind of line, in the order they appear along the top of the console.</summary>
    public IReadOnlyList<ConsoleKindFilter> Kinds { get; }

    /// <summary>The text being searched for; empty shows everything.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? string.Empty;
            Rebuild();
        }
    }

    /// <summary>True while anything at all is being hidden, so the UI can offer to undo it.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Filter) || Kinds.Any(k => !k.IsOn);

    /// <summary>Raised when <see cref="Visible"/> was rebuilt, i.e. <see cref="IsFiltered"/> may have changed.</summary>
    public event Action? Rebuilt;

    /// <summary>
    /// Raised after a batch of lines went on screen, with that batch, on the UI thread. What
    /// reacts to the content of the lines (who joined, what the server refused) listens here.
    /// </summary>
    public event Action<IReadOnlyList<ConsoleLine>>? Flushed;

    /// <summary>Queues a line from any thread and makes sure it will be shown.</summary>
    public void Enqueue(ConsoleLine line)
    {
        _pending.Enqueue(line);

        if (Dispatcher.UIThread.CheckAccess())
        {
            Flush();
            return;
        }

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    /// <summary>Empties the console and its counters.</summary>
    public void Clear()
    {
        Lines.Clear();
        Visible.Clear();
        foreach (var filter in Kinds) { filter.Count = 0; filter.HasUnseen = false; }
    }

    /// <summary>Switches every category back on and clears the search.</summary>
    public void ShowAllKinds()
    {
        foreach (var filter in Kinds) filter.IsOn = true;
        Filter = string.Empty;
    }

    /// <summary>
    /// Repaints the console and its switches after the colours change in the settings.
    /// </summary>
    /// <remarks>
    /// The lines themselves get their colour through a converter, so nothing here has to hold a
    /// brush per line. Rebuilding the visible list is what makes them ask again: it is a single
    /// Reset, the items are re-created, and the converter runs with the new settings.
    /// </remarks>
    public void RefreshColours()
    {
        var brushes = ConsolePalette.All(
            NotificationPreferences.Global, ConsolePreferences.ChatColor, ConsolePreferences.PlayersColor);

        foreach (var filter in Kinds)
            filter.Brush = brushes[filter.Kind];

        Rebuild();
    }

    private bool Matches(ConsoleLine line) => ConsoleKindFilter.Matches(line, Filter, _kindFilters);

    private void Rebuild()
    {
        Visible.ReplaceAll(Lines.Where(Matches));
        Rebuilt?.Invoke();
    }

    private void OnKindChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only the switch itself: Count and HasUnseen change constantly while a server runs, and
        // rebuilding the visible list on every arriving line would undo the whole point of adding
        // lines incrementally.
        if (e.PropertyName == nameof(ConsoleKindFilter.IsOn))
            Rebuild();
    }

    /// <summary>Puts every waiting line on screen, with one notification per list.</summary>
    private void Flush()
    {
        // Cleared before taking the lines, so one arriving meanwhile schedules a flush of its own.
        Interlocked.Exchange(ref _flushScheduled, 0);

        var batch = new List<ConsoleLine>();
        while (_pending.TryDequeue(out var queued)) batch.Add(queued);
        if (batch.Count == 0) return;

        var visible = new List<ConsoleLine>(batch.Count);
        foreach (var line in batch)
        {
            if (_kindFilters.TryGetValue(line.Kind, out var filter))
            {
                filter.Count++;
                // Arriving while its own switch is off would otherwise be completely invisible:
                // filtered out of the list, with nothing on screen changing.
                if (!filter.IsOn) filter.HasUnseen = true;
            }

            if (Matches(line)) visible.Add(line);
        }

        Lines.AddRange(batch);
        Visible.AddRange(visible);
        TrimIfOverCap();

        Flushed?.Invoke(batch);
    }

    private void TrimIfOverCap()
    {
        if (Lines.Count <= MaxLines + TrimBlock) return;

        var excess = Lines.Count - MaxLines;

        // Counted before the lines are gone; see VisibleLinesLeaving for why the visible
        // list is trimmed rather than rebuilt.
        var leaving = ConsoleKindFilter.VisibleLinesLeaving(Lines, excess, Matches);

        Lines.RemoveFromStart(excess);
        ConsoleKindFilter.Recount(Lines, Kinds); // lines fell off the top
        Visible.RemoveFromStart(leaving);
    }
}
