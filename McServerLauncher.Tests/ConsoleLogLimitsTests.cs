using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The console log stays within bounds on a machine where the app never closes.
/// </summary>
/// <remarks>
/// Old files were pruned only when the app started, and the app is made to live in the tray for
/// weeks. And a day had no ceiling: a server stuck printing an error in a loop wrote until the disk
/// was full.
/// </remarks>
public class ConsoleLogLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-logs-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 1, 10, 12, 0, 0);

    private ConsoleLogService Service(long max = ConsoleLogService.DefaultMaxBytesPerDay) =>
        new(_dir, () => _now, max);

    private string FileFor(DateTime day) => Path.Combine(_dir, $"launcher-{day:yyyy-MM-dd}.log");

    private string Read(DateTime day)
    {
        using var stream = new FileStream(FileFor(day), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void ANewDayPrunesWithoutARestart()
    {
        Directory.CreateDirectory(_dir);
        var old = FileFor(new DateTime(2026, 1, 1));
        File.WriteAllText(old, "old");
        File.SetLastWriteTime(old, new DateTime(2026, 1, 1));

        using var log = Service();
        log.Log("s", "first day");
        Assert.True(File.Exists(old));             // nine days old: kept

        _now = new DateTime(2026, 1, 20, 0, 1, 0);  // the app never closed; the date moved on
        log.Log("s", "a later day");

        Assert.False(File.Exists(old));            // nineteen days old: gone
    }

    [Fact]
    public void ADayStopsAtItsCeilingAndSaysSo()
    {
        using (var log = Service(max: 400))
        {
            for (var i = 0; i < 200; i++) log.Log("s", "the same error, again and again");
            log.Flush();
        }

        var text = Read(_now);
        Assert.True(text.Length < 600, $"wrote {text.Length} characters past a 400 ceiling");
        Assert.Single(text.Split('\n'), l => l.Contains("size limit"));
    }

    [Fact]
    public void TheNextDayWritesAgain()
    {
        using var log = Service(max: 200);
        for (var i = 0; i < 50; i++) log.Log("s", "flood");

        _now = _now.AddDays(1);
        log.Log("s", "a new day");
        log.Flush();

        Assert.Contains("a new day", Read(_now));
    }

    [Fact]
    public void ARestartTheSameDayRemembersTheDayIsFull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FileFor(_now), new string('x', 500));

        using (var log = Service(max: 400))
        {
            log.Log("s", "after a restart");
            log.Flush();
        }

        Assert.DoesNotContain("after a restart", Read(_now));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
