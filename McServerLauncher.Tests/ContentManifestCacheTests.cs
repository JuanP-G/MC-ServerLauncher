using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>The manifest cache forgets jars that are no longer there.</summary>
/// <remarks>
/// Every toggle renames a jar and every update writes a new one; the cache kept them all for as long
/// as the app ran.
/// </remarks>
public class ContentManifestCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "mcl-mcache-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void OnlyTheJarsStillPresentAreKept()
    {
        var cache = new ContentManifestCache();
        var kept = Path.Combine(_dir, "kept.jar");
        var renamed = Path.Combine(_dir, "renamed.jar");
        File.WriteAllText(kept, "not a zip");
        File.WriteAllText(renamed, "not a zip");
        cache.Read(kept);
        cache.Read(renamed);

        cache.ForgetAllBut(new[] { kept });

        Assert.Equal(1, cache.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
