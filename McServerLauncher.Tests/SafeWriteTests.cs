using System.Security.Cryptography;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The two rules every file the app writes has to obey: land where it was meant to, and not be
/// rewritten for nothing.
/// </summary>
/// <remarks>
/// Both were being broken by code that looked correct. Five download sites joined a folder to a
/// name the API gave them with <c>Path.Combine</c>, which is not a containment check and never was;
/// and Geyser's config was rewritten in full every thirty seconds whether or not a single byte had
/// changed, which also meant any bug in what was written reached the disk on its own.
/// </remarks>
public class SafeWriteTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-safe-" + Guid.NewGuid().ToString("N"));

    private string Folder()
    {
        Directory.CreateDirectory(_folder);
        return _folder;
    }

    [Theory]
    [InlineData("sodium.jar", "sodium.jar")]
    [InlineData("Explorify v1.6.5.mod.jar", "Explorify v1.6.5.mod.jar")]   // spaces and dots are fine
    [InlineData("../../../evil.jar", "evil.jar")]
    [InlineData("sub/dir/thing.jar", "thing.jar")]
    public void ANameFromTheApiCannotEscapeTheFolder(string remoteName, string expected)
    {
        var path = AtomicDownload.PathIn(Folder(), remoteName);

        Assert.Equal(Path.Combine(_folder, expected), path);
        Assert.Equal(_folder, Path.GetDirectoryName(path));
    }

    [Fact]
    public void ABackslashPathIsStoppedWhereABackslashIsASeparator()
    {
        // Windows only, and not for tidiness: on Linux a backslash is an ordinary character in a
        // file name, so "..\\..\\startup.bat" is one strange name inside the folder rather than a way
        // out of it. Asserting the Windows answer there would be testing the wrong system.
        var path = AtomicDownload.PathIn(Folder(), "..\\..\\startup.bat");

        Assert.Equal(_folder, Path.GetDirectoryName(path));
        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(_folder, "startup.bat"), path);
    }

    [Fact]
    public void AnAbsolutePathFromTheApiIsNotObeyed()
    {
        // The worst of the shapes Path.Combine accepts: it discards the folder entirely and hands
        // back whatever the remote name said, which is how a download ends up in System32.
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "x.dll");

        var path = AtomicDownload.PathIn(Folder(), absolute);

        Assert.Equal(Path.Combine(_folder, "x.dll"), path);
    }

    [Fact]
    public void ANameWithNothingUsableInItIsRefusedRatherThanGuessed()
    {
        // No last segment at all. Inventing one would put a file with a made-up name in the mods
        // folder; refusing says the API returned something the app does not understand.
        Assert.Throws<InvalidOperationException>(() => AtomicDownload.PathIn(Folder(), "some/dir/"));
        Assert.Throws<InvalidOperationException>(() => AtomicDownload.PathIn(Folder(), "   "));
    }

    [Fact]
    public void AConfigThatSaysTheSameThingIsNotRewritten()
    {
        var path = Path.Combine(Folder(), "config.yml");

        Assert.True(AtomicTextFile.WriteIfChanged(path, "bedrock:\n  port: 19132\n"));
        var stamp = File.GetLastWriteTimeUtc(path);

        // The address refresh calls this every thirty seconds with, almost always, this same text.
        Assert.False(AtomicTextFile.WriteIfChanged(path, "bedrock:\n  port: 19132\n"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

        Assert.True(AtomicTextFile.WriteIfChanged(path, "bedrock:\n  port: 19133\n"));
        Assert.Equal("bedrock:\n  port: 19133\n", File.ReadAllText(path));
    }

    [Fact]
    public void TheWriteLeavesNoTemporaryFileBehind()
    {
        // It goes through "<path>.tmp" so a power cut mid-write cannot truncate the real file. That
        // temporary must not survive the write, on either branch — created or replaced.
        var path = Path.Combine(Folder(), "config.yml");

        AtomicTextFile.WriteIfChanged(path, "one");
        Assert.False(File.Exists(path + ".tmp"));

        AtomicTextFile.WriteIfChanged(path, "two");
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("two", File.ReadAllText(path));
    }

    [Fact]
    public async Task AFileIsHashedOnceUntilItChanges()
    {
        // The Mods tab hashes every jar in the folder twice per install — once to look for updates
        // and once to see which projects are already there. On a hundred mods that was two hundred
        // full reads for one click, and the second hundred were answers the first had just found.
        var path = Path.Combine(Folder(), "mod.jar");
        File.WriteAllText(path, "one");

        var cache = new FileHashCache();
        var first = await cache.Sha1Async(path);

        // Same file: the cached answer, and correct.
        Assert.Equal(first, await cache.Sha1Async(path));
        Assert.Equal(await DownloadVerifier.ComputeHashAsync(path, HashAlgorithmName.SHA1, default), first);

        // A different file under the same name is not the same jar, and serving the old hash would
        // identify a mod as something it no longer is. The size alone catches this one...
        File.WriteAllText(path, "different length");
        var second = await cache.Sha1Async(path);
        Assert.NotEqual(first, second);

        // ...and a replacement of exactly the same length is caught by the write time, which is
        // what an update or a re-download actually looks like.
        File.WriteAllText(path, "one");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(first, await cache.Sha1Async(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
