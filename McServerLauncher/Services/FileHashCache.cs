using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace McServerLauncher.Services;

/// <summary>
/// The SHA-1 of a file, remembered for as long as the file has not changed.
/// </summary>
/// <remarks>
/// <para>
/// Identifying a mod on Modrinth means hashing its jar, and the Mods tab does that for every jar in
/// the folder in two separate places: the update check, and working out which projects are already
/// installed before pulling in a dependency. On a folder with a hundred mods that was two hundred
/// full reads for one click, all of them producing the answers the other pass had just computed.
/// </para>
/// <para>
/// The key is the path plus the size plus the write time, which is what makes reuse safe: replace,
/// update or re-download a jar and any of the three moves, so the cached answer is discarded rather
/// than served for a file that is no longer the same one.
/// </para>
/// </remarks>
public sealed class FileHashCache
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Sha1)> _known = new();

    /// <summary>The file's SHA-1, hashing it only if this exact file has not been hashed already.</summary>
    public async Task<string> Sha1Async(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        var stamp = (info.Length, info.LastWriteTimeUtc);

        if (_known.TryGetValue(path, out var cached) &&
            cached.Length == stamp.Length && cached.Written == stamp.LastWriteTimeUtc)
            return cached.Sha1;

        var sha1 = await DownloadVerifier.ComputeHashAsync(path, HashAlgorithmName.SHA1, ct);
        _known[path] = (stamp.Length, stamp.LastWriteTimeUtc, sha1);
        return sha1;
    }

    /// <summary>Forgets everything. For a folder that changed in ways the stamps cannot see.</summary>
    public void Clear() => _known.Clear();
}
