using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace McServerLauncher.Services;

/// <summary>
/// Remote images (project icons and gallery screenshots) with a memory + disk cache in front.
/// <para>
/// The URLs come from Modrinth's API, but they are still remote input, so every download is
/// guarded: the response has to declare an image content-type and stay under a byte cap that is
/// enforced <em>while streaming</em>, so a missing or lying Content-Length can't balloon memory
/// either. Anything odd yields null and the caller keeps its placeholder.
/// </para>
/// <para>
/// Decoding happens on the calling (background) thread, never on the UI thread. Bitmaps are cached
/// and handed to several views at once, so they are deliberately never disposed here — a disposed
/// bitmap still bound to an <c>Image</c> would throw when it is drawn.
/// </para>
/// </summary>
public static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Generous cap for a project icon (they're typically a few KB).</summary>
    public const int MaxIconBytes = 2 * 1024 * 1024;

    /// <summary>Cap for gallery screenshots, which are full-size images rather than icons.</summary>
    public const int MaxGalleryBytes = 8 * 1024 * 1024;

    /// <summary>Bitmaps kept in memory. Bounded, because a browsing session sees a lot of icons.</summary>
    private const int MaxMemoryEntries = 256;

    private static readonly ConcurrentDictionary<string, Entry> Memory = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> InFlight = new();

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "McServerLauncher", "cache", "images");

    /// <summary>Disk entries older than this are pruned; icons change, and space is not free.</summary>
    private static readonly TimeSpan MaxDiskAge = TimeSpan.FromDays(30);

    private static int _pruned;

    private sealed record Entry(Bitmap Bitmap, DateTime StoredUtc);

    static ImageCache()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "JuanP-G/MC-ServerLauncher");
    }

    /// <summary>
    /// Returns the decoded image for <paramref name="url"/>, from memory, then disk, then the
    /// network. Concurrent callers for the same URL share one download.
    /// <para>
    /// Returns null for everything that isn't an image in hand — an empty URL, a rejected
    /// download, an undecodable format, or a caller that cancelled. Callers all do the same thing
    /// with that answer (keep the placeholder), and since most of them are fire-and-forget, a
    /// thrown cancellation would only become an unobserved task exception.
    /// </para>
    /// </summary>
    public static async Task<Bitmap?> GetAsync(string? url, int maxBytes = MaxIconBytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (Memory.TryGetValue(url, out var cached)) return cached.Bitmap;

        // One download per URL, however many callers ask at once. The shared task deliberately
        // carries no cancellation token: a second caller waiting on it must not be cancelled
        // because the first one walked away.
        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = InFlight.GetOrAdd(url, tcs.Task);

        if (ReferenceEquals(task, tcs.Task))
        {
            try { tcs.TrySetResult(await LoadAsync(url, maxBytes)); }
            catch { tcs.TrySetResult(null); }
            finally { InFlight.TryRemove(url, out _); }
        }

        try { return await task.WaitAsync(ct); }
        catch { return null; }
    }

    private static async Task<Bitmap?> LoadAsync(string url, int maxBytes)
    {
        var path = PathFor(url);

        // Disk first: a cached icon decodes in well under a millisecond and costs no request.
        var bytes = ReadDisk(path);
        if (bytes is null)
        {
            bytes = await DownloadAsync(url, maxBytes);
            if (bytes is null) return null;
            WriteDisk(path, bytes);
        }

        var bitmap = Decode(bytes);
        if (bitmap is null)
        {
            // Undecodable content (an SVG, or a truncated cache entry): drop the cached copy so a
            // later attempt re-downloads instead of failing forever on the same bad bytes.
            try { File.Delete(path); } catch { /* best-effort */ }
            return null;
        }

        Memory[url] = new Entry(bitmap, DateTime.UtcNow);
        TrimMemory();
        return bitmap;
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> DownloadAsync(string url, int maxBytes)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentType?.MediaType is not { } mime ||
                !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;
            if (resp.Content.Headers.ContentLength is { } declared && declared > maxBytes) return null;

            using var buffered = new MemoryStream();
            await using (var stream = await resp.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    if (buffered.Length + read > maxBytes) return null;
                    buffered.Write(buffer, 0, read);
                }
            }
            return buffered.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > MaxDiskAge) return null;
            return File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDisk(string path, byte[] bytes)
    {
        try
        {
            PruneOnce();
            Directory.CreateDirectory(CacheDir);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The cache is an optimisation: a read-only or full disk must never break the gallery.
        }
    }

    /// <summary>Hashes the URL, so a remote path can never decide where a file is written.</summary>
    private static string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(CacheDir, hash + ".img");
    }

    private static void TrimMemory()
    {
        if (Memory.Count <= MaxMemoryEntries) return;
        // Evicted bitmaps are not disposed on purpose: a view may still be drawing one.
        foreach (var key in Memory.OrderBy(kv => kv.Value.StoredUtc)
                                  .Take(Memory.Count - MaxMemoryEntries / 2)
                                  .Select(kv => kv.Key)
                                  .ToList())
            Memory.TryRemove(key, out _);
    }

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) != 0) return;
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            var cutoff = DateTime.UtcNow - MaxDiskAge;
            foreach (var file in Directory.EnumerateFiles(CacheDir))
            {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                catch { /* in use or gone: skip */ }
            }
        }
        catch { /* best-effort */ }
    }
}
