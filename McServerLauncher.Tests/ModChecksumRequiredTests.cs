using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A mod without a published checksum is not installed.
/// </summary>
/// <remarks>
/// The rule written down for mods was "no checksum, no install", and the download went ahead
/// unverified when Modrinth gave neither hash. Refused before anything is fetched, so no request
/// leaves this test.
/// </remarks>
public class ModChecksumRequiredTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-nohash-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NoHashMeansNoDownload()
    {
        var dest = Path.Combine(_dir, "mod.jar");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ModrinthService().DownloadModAsync("https://cdn.modrinth.invalid/mod.jar", dest, null, null));

        Assert.Contains("mod.jar", refused.Message);
        Assert.False(File.Exists(dest));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
