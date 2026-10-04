using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The single-instance tests leave nothing behind in the app's real data folder.
/// </summary>
/// <remarks>
/// A scope used to change only the lock file's name, not its folder: every run of these tests left
/// its own <c>instance-tests-*.lock</c> in the data folder of whoever ran them. One developer's
/// machine had more than eight hundred.
/// </remarks>
public class SingleInstanceHygieneTests : IDisposable
{
    private readonly string _scope = "tests-" + Guid.NewGuid().ToString("N")[..8];

    private static string RealDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher");

    public SingleInstanceHygieneTests() => SingleInstance.Scope = _scope;

    public void Dispose() => SingleInstance.Scope = null;

    [Fact]
    public void ATestsLockIsNeverInTheRealDataFolderAndGoesAwayAfterwards()
    {
        var name = $"instance-{_scope}.lock";
        var tests = Path.Combine(Path.GetTempPath(), "mcsl-instance-tests");

        using (var instance = SingleInstance.TryAcquire(out _))
        {
            Assert.NotNull(instance);
            Assert.False(File.Exists(Path.Combine(RealDataFolder, name)));
            Assert.True(File.Exists(Path.Combine(tests, name)));
        }

        Assert.False(File.Exists(Path.Combine(tests, name)));
    }
}
