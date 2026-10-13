using System.Collections.Concurrent;
using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// What a jar says about itself, remembered for as long as the jar has not changed.
/// </summary>
/// <remarks>
/// <para>
/// Reading a manifest means opening the jar and every jar bundled inside it — fabric-api alone is
/// forty-odd — and finding the other copies of a mod reads the whole folder. "Update all" does that
/// once per mod, so a folder of thirty mods was nine hundred jar reads for one click, all of them
/// producing the same answers.
/// </para>
/// <para>
/// Keyed like <see cref="FileHashCache"/>, by path plus size plus write time, for the same reason:
/// replace or re-download a jar and the stamp moves, so the cached answer is never served for a file
/// that is no longer the one it was read from.
/// </para>
/// </remarks>
public sealed class ContentManifestCache
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, ContentManifest.Manifest Manifest)> _known = new();

    /// <summary>The jar's manifest, reading it only if this exact file has not been read already.</summary>
    public ContentManifest.Manifest Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return ContentManifest.Read(path);

        var stamp = (info.Length, info.LastWriteTimeUtc);
        if (_known.TryGetValue(path, out var cached) &&
            cached.Length == stamp.Length && cached.Written == stamp.LastWriteTimeUtc)
            return cached.Manifest;

        var manifest = ContentManifest.Read(path);
        _known[path] = (stamp.Length, stamp.LastWriteTimeUtc, manifest);
        return manifest;
    }
}
