using System.Security.Cryptography;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Hashing a jar must not get in the way of whoever else is using it. The store buttons hash every
/// installed jar in the background, right after installs and switches — the moments the user is most
/// likely to be renaming or replacing one.
/// </summary>
public class FileHashSharingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-hash-" + Guid.NewGuid().ToString("N"));

    public FileHashSharingTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task AJarAnotherProcessHasOpenForWritingCanStillBeHashed()
    {
        // Windows only enforces this: File.OpenRead asks not to share with a writer and fails.
        var path = Path.Combine(_folder, "mod.jar");
        File.WriteAllText(path, "contents");

        await using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var hash = await DownloadVerifier.ComputeHashAsync(path, HashAlgorithmName.SHA1);

        Assert.Equal(Convert.ToHexString(SHA1.HashData("contents"u8)), hash);
    }
}
