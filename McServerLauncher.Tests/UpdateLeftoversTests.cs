using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The folders updates download into are cleared once they are old, and nothing else is.
/// </summary>
/// <remarks>
/// The installer has to stay put while it runs, so the copy that starts it cannot delete it — and
/// nothing did later: every update left a few dozen megabytes in the temporary folder.
/// </remarks>
public class UpdateLeftoversTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mcl-leftovers-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    public UpdateLeftoversTests() => Directory.CreateDirectory(_temp);

    private string Folder(string name, TimeSpan age)
    {
        var path = Path.Combine(_temp, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "MC-ServerLauncher-Setup.exe"), "x");
        Directory.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void AnOldPackageFolderIsDeleted()
    {
        var old = Folder("mcsl-ab12cd34.x9z", TimeSpan.FromDays(3));
        SelfUpdater.DeleteOldPackages(_temp, Now);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void OneFromAnUpdateRunningNowIsKept()
    {
        var fresh = Folder("mcsl-ab12cd34.x9z", TimeSpan.FromMinutes(5));
        SelfUpdater.DeleteOldPackages(_temp, Now);
        Assert.True(Directory.Exists(fresh));
    }

    [Theory]
    [InlineData("mcsl-instance-tests")]
    [InlineData("mcsl-installer.AbC123")]
    [InlineData("other-ab12cd34.x9z")]
    public void FoldersNamedAnyOtherWayAreNotTouched(string name)
    {
        var theirs = Folder(name, TimeSpan.FromDays(30));
        SelfUpdater.DeleteOldPackages(_temp, Now);
        Assert.True(Directory.Exists(theirs));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort */ }
    }
}
