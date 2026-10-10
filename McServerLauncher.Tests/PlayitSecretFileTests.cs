using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The file playitd reads its key from is never readable by anyone else, not even for a moment.
/// </summary>
/// <remarks>
/// It was written under the process umask — world-readable on many Linux distributions — and only
/// then narrowed to 0600. And an existing file was truncated in place, keeping whatever mode it had.
/// </remarks>
public class PlayitSecretFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-secret-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "agent-secret");

    public PlayitSecretFileTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void TheKeyIsWrittenTrimmed()
    {
        PlayitAgentRunner.WriteSecret(File_, "  abc123\n");
        Assert.Equal("abc123", File.ReadAllText(File_));
    }

    [Fact]
    public void AShorterKeyLeavesNothingOfTheOldOne()
    {
        PlayitAgentRunner.WriteSecret(File_, "a-much-longer-old-key");
        PlayitAgentRunner.WriteSecret(File_, "new");
        Assert.Equal("new", File.ReadAllText(File_));
    }

    [Fact]
    public void OnUnixOnlyTheOwnerCanReadIt()
    {
        if (OperatingSystem.IsWindows()) return;   // the app-data folder's ACL already does this there

        // An old file left world-readable must not pass its mode on.
        File.WriteAllText(File_, "old");
        File.SetUnixFileMode(File_, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        PlayitAgentRunner.WriteSecret(File_, "new");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(File_));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
