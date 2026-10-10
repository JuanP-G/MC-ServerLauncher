using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// An exception nobody caught leaves a file behind instead of nothing.
/// </summary>
/// <remarks>
/// The app had no global handler: a crash closed it without a word and left its servers running
/// with nothing to stop them.
/// </remarks>
public class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-crash-" + Guid.NewGuid().ToString("N"));
    private readonly string _real = CrashLog.Folder;

    public CrashLogTests() => CrashLog.Folder = _dir;

    [Fact]
    public void TheCrashIsWrittenDownWithWhereItCameFrom()
    {
        var path = CrashLog.Write("UI", new InvalidOperationException("something broke"));

        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("UI", text);
        Assert.Contains("something broke", text);
        Assert.Contains("MC Server Launcher", text);
    }

    [Fact]
    public void OnlyTheNewestAreKept()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < CrashLog.Keep + 5; i++)
            File.WriteAllText(Path.Combine(_dir, $"crash-20200101-0000{i:00}-000.log"), "old");

        CrashLog.Write("Task", new Exception("new"));

        Assert.Equal(CrashLog.Keep, Directory.GetFiles(_dir, "crash-*.log").Length);
    }

    public void Dispose()
    {
        CrashLog.Folder = _real;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
