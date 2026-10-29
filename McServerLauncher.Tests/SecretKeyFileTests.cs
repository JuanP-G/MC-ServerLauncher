using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The key file that encrypts secrets on Linux and macOS: created once, and never replaced under
/// somebody who has already used it.
/// </summary>
public class SecretKeyFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-key-" + Guid.NewGuid().ToString("N"));
    private string KeyFile => Path.Combine(_folder, ".secret.key");

    [Fact]
    public void ACreatedKeyIsTheOneReadBackLater()
    {
        var first = SecretProtector.GetOrCreateKey(KeyFile);
        var second = SecretProtector.GetOrCreateKey(KeyFile);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AnExistingKeyIsNeverOverwritten()
    {
        Directory.CreateDirectory(_folder);
        var mine = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(KeyFile, mine);

        Assert.Equal(mine, SecretProtector.GetOrCreateKey(KeyFile));
        Assert.Equal(mine, File.ReadAllBytes(KeyFile));
    }

    [Fact]
    public void TwoProcessesCreatingItTogetherEndUpWithTheSameKey()
    {
        // Create used to truncate, so the slower of two creators replaced the key the faster one had
        // already encrypted with. Many rounds, because the window is a few microseconds wide.
        for (var round = 0; round < 40; round++)
        {
            var file = Path.Combine(_folder, $"round{round}", ".secret.key");
            var barrier = new Barrier(8);
            var keys = new byte[8][];

            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                barrier.SignalAndWait();
                keys[i] = SecretProtector.GetOrCreateKey(file);
            });

            var onDisk = File.ReadAllBytes(file);
            Assert.All(keys, k => Assert.Equal(onDisk, k));
        }
    }

    [Fact]
    public void ADamagedKeyFileIsReplacedRatherThanLeftToFailForever()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(KeyFile, new byte[5]);   // wrong length: unreadable

        var key = SecretProtector.GetOrCreateKey(KeyFile);

        Assert.Equal(32, key.Length);
        Assert.Equal(key, File.ReadAllBytes(KeyFile));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch { /* temp */ }
    }
}
