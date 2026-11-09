using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A disk that refuses a write must not take the launcher down. The history is recorded on the
/// output thread of the server process, where an exception nobody catches ends the whole app and
/// leaves the Java process running with the port still taken.
/// </summary>
public class PlayerHistoryDiskFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcsl-histfail-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public PlayerHistoryDiskFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* temporary folder */ }
    }

    private PlayerHistoryStore Store(PlayerHistorySettings? settings = null) =>
        new(Path.Combine(_root, "server"), () => (settings ?? new PlayerHistorySettings()).Clamped(), () => _now);

    private string EventsFile(string key) => Path.Combine(_root, "server", "events", key + ".jsonl");

    [Fact]
    public void ARefusedAppendDoesNotThrowOutOfRecord()
    {
        using var store = Store();
        // A directory where the events file should be: appending to it fails everywhere.
        Directory.CreateDirectory(EventsFile("alice"));

        var ex = Record.Exception(() =>
            store.Record(new PlayerEvent(PlayerEventKind.Join, "Alice", null), _now));

        Assert.Null(ex);
    }

    [Fact]
    public void TheSummaryStillCountsTheSessionWhenTheEventCouldNotBeWritten()
    {
        using var store = Store();
        Directory.CreateDirectory(EventsFile("alice"));

        store.Record(new PlayerEvent(PlayerEventKind.Join, "Alice", null), _now);

        Assert.Equal(1, store.Get("Alice")?.Sessions);
    }

    [Fact]
    public void ARecordThatFailedDoesNotStopTheNextOnesFromBeingWritten()
    {
        using var store = Store();
        Directory.CreateDirectory(EventsFile("alice"));
        store.Record(new PlayerEvent(PlayerEventKind.Join, "Alice", null), _now);

        store.Record(new PlayerEvent(PlayerEventKind.Join, "Bob", null), _now);

        Assert.Single(store.Events("Bob"));
    }

    [Fact]
    public void ACompactionThatCannotRewriteTheFileDoesNotThrow()
    {
        var clock = _now;
        using var store = new PlayerHistoryStore(Path.Combine(_root, "server"),
            () => new PlayerHistorySettings().Clamped(), () => clock);   // 90 days of retention
        store.Record(new PlayerEvent(PlayerEventKind.Chat, "Alice", "old"), clock);
        clock = _now.AddDays(89);
        store.Record(new PlayerEvent(PlayerEventKind.Chat, "Alice", "recent"), clock);
        File.SetAttributes(EventsFile("alice"), FileAttributes.ReadOnly);

        // Two days on, the first event is past the retention, so Prune has to rewrite the file.
        clock = _now.AddDays(91);
        var ex = Record.Exception(store.Prune);

        Assert.Null(ex);
    }

    [Fact]
    public void AStoreWhoseFolderCannotBeCreatedStillDoesNotThrow()
    {
        // A file where the store's own folder should be.
        File.WriteAllText(Path.Combine(_root, "server"), "not a folder");
        using var store = Store();

        var ex = Record.Exception(() =>
            store.Record(new PlayerEvent(PlayerEventKind.Join, "Alice", null), _now));

        Assert.Null(ex);
    }
}
