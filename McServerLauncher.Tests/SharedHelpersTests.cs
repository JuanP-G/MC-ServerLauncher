using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The small helpers that replaced copies spread over a dozen services: where the app's data lives,
/// how a stray file is removed, how a downloaded program is made runnable, and what every request
/// says it is.
/// </summary>
/// <remarks>
/// They are trivial on purpose, and that is why they are pinned: each one stands in for code that
/// used to be written in place, so a mistake here is a mistake in all of those places at once.
/// </remarks>
public class SharedHelpersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcsl-helpers-" + Path.GetRandomFileName());

    public SharedHelpersTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* the OS sweeps temp */ }
    }

    // --- AppPaths ---

    [Fact]
    public void DataDirIsTheAppsFolderUnderApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher");

        Assert.Equal(expected, AppPaths.DataDir);
    }

    [Fact]
    public void InJoinsEverySegmentUnderTheDataDir() =>
        Assert.Equal(Path.Combine(AppPaths.DataDir, "cache", "images"), AppPaths.In("cache", "images"));

    [Fact]
    public void InWithoutSegmentsIsTheDataDirItself() =>
        Assert.Equal(AppPaths.DataDir, AppPaths.In());

    // --- FileCleanup ---

    [Fact]
    public void TryDeleteRemovesAFileThatIsThere()
    {
        var file = Path.Combine(_folder, "stray.part");
        File.WriteAllText(file, "x");

        FileCleanup.TryDelete(file);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void TryDeleteOfAFileThatIsNotThereDoesNothing() =>
        FileCleanup.TryDelete(Path.Combine(_folder, "never-existed"));

    [Fact]
    public void TryDeleteOfAFileHeldOpenDoesNotThrow()
    {
        var file = Path.Combine(_folder, "held.part");
        // Held with no sharing: on Windows the delete fails, and that must stay private.
        using var held = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);

        FileCleanup.TryDelete(file);
    }

    // --- FileModes ---

    [Fact]
    public void MakeExecutableSetsTheExecuteBitsOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_folder, "program");
        File.WriteAllText(file, "#!/bin/sh");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        FileModes.MakeExecutable(file);

        var mode = File.GetUnixFileMode(file);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
    }

    [Fact]
    public void MakeExecutableDoesNothingOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Not even for a file that does not exist: there is no bit to set, so nothing can fail.
        FileModes.MakeExecutable(Path.Combine(_folder, "missing.exe"));
    }

    [Fact]
    public void TryMakeExecutableOfAMissingFileDoesNotThrow() =>
        FileModes.TryMakeExecutable(Path.Combine(_folder, "missing"));

    // --- AppHttp ---

    [Fact]
    public void EveryClientIntroducesItself()
    {
        using var http = AppHttp.Create(TimeSpan.FromSeconds(7));

        Assert.Equal(AppHttp.UserAgent, http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void TheTimeoutIsTheOneAskedFor()
    {
        using var http = AppHttp.Create(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), http.Timeout);
    }

    [Fact]
    public void TheUserAgentNamesTheProjectSoServicesCanTellWhoIsCalling() =>
        Assert.Contains("MC-ServerLauncher", AppHttp.UserAgent);
}
