using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McServerLauncher.Services;

/// <summary>
/// Two-level cache (memory, then disk, then the network) for the store's API responses. It exists
/// so that opening a mod — and going back and opening it again — costs one request, not one per
/// visit, and so that a project already seen still opens with no connection.
/// <para>
/// Concurrent callers asking for the same key share a single fetch: opening a details page kicks
/// off the project, its versions and its dependencies at once, and the related-mods strip may ask
/// for a project that is already in flight.
/// </para>
/// <para>
/// Entries are kept on disk beyond their freshness window on purpose: when the network fails, a
/// stale entry is a much better answer than an empty page. Anything older than
/// <see cref="MaxDiskAge"/> is pruned once per run.
/// </para>
/// </summary>
public sealed class StoreCache
{
    /// <summary>Shared instance: the cache is only useful if every view model hits the same one.</summary>
    public static readonly StoreCache Shared = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Disk entries older than this are deleted; they are worthless even as a fallback.</summary>
    private static readonly TimeSpan MaxDiskAge = TimeSpan.FromDays(30);

    private readonly string _dir;
    private readonly ConcurrentDictionary<string, Entry> _memory = new();
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();
    private int _pruned;

    private sealed record Entry(object? Value, DateTime StoredUtc);

    public StoreCache(string? cacheDir = null)
    {
        _dir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McServerLauncher", "cache", "store");
    }

    /// <summary>
    /// Returns the cached value when it is younger than <paramref name="ttl"/>, otherwise calls
    /// <paramref name="fetch"/> and stores the result. A failed or empty fetch falls back to a
    /// stale copy when there is one, so the UI degrades to "slightly old" instead of "nothing".
    /// </summary>
    public async Task<T?> GetOrFetchAsync<T>(string key, TimeSpan ttl,
        Func<CancellationToken, Task<T?>> fetch, CancellationToken ct = default) where T : class
    {
        if (TryGetFresh<T>(key, ttl, out var fresh)) return fresh;

        // Someone else is already fetching this key: wait for their result instead of doubling the
        // request. If their task fails or is cancelled we fall through and try ourselves.
        if (_inFlight.TryGetValue(key, out var pending))
        {
            try
            {
                await pending.WaitAsync(ct);
                if (TryGetFresh<T>(key, ttl, out var shared)) return shared;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* the other caller's fetch failed; try our own below */ }
        }

        var tcs = new TaskCompletionSource();
        if (!_inFlight.TryAdd(key, tcs.Task))
        {
            // Lost the race between the check above and here: just fetch without registering.
            return await FetchAndStoreAsync(key, fetch, ct) ?? ReadStale<T>(key);
        }

        try
        {
            var value = await FetchAndStoreAsync(key, fetch, ct);
            return value ?? ReadStale<T>(key);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
            tcs.TrySetResult();
        }
    }

    /// <summary>Drops a key from memory so the next read goes back to the network.</summary>
    public void Invalidate(string key) => _memory.TryRemove(key, out _);

    private async Task<T?> FetchAndStoreAsync<T>(string key, Func<CancellationToken, Task<T?>> fetch,
        CancellationToken ct) where T : class
    {
        var value = await fetch(ct);
        if (value is null) return null;

        _memory[key] = new Entry(value, DateTime.UtcNow);
        TrimMemory();
        WriteDisk(key, value);
        return value;
    }

    /// <summary>
    /// Keeps the in-memory half bounded. Project bodies are long, and a browsing session can walk
    /// through hundreds of them; the disk copy stays, so evicting only costs a file read.
    /// </summary>
    private void TrimMemory()
    {
        const int maxEntries = 200;
        if (_memory.Count <= maxEntries) return;
        foreach (var key in _memory.OrderBy(kv => kv.Value.StoredUtc)
                                   .Take(_memory.Count - maxEntries / 2)
                                   .Select(kv => kv.Key)
                                   .ToList())
            _memory.TryRemove(key, out _);
    }

    private bool TryGetFresh<T>(string key, TimeSpan ttl, out T? value) where T : class
    {
        if (_memory.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.StoredUtc < ttl)
        {
            value = entry.Value as T;
            if (value is not null) return true;
        }

        // Not in memory (or expired): a disk copy written within the TTL is just as good, and it is
        // what makes the second run of the app feel instant.
        var (disk, ageOk) = ReadDisk<T>(key, ttl);
        if (disk is not null && ageOk)
        {
            _memory[key] = new Entry(disk, DateTime.UtcNow);
            value = disk;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Last resort: any disk copy, however old, when the network is unavailable.</summary>
    private T? ReadStale<T>(string key) where T : class
    {
        var (value, _) = ReadDisk<T>(key, TimeSpan.MaxValue);
        if (value is not null) _memory[key] = new Entry(value, DateTime.UtcNow);
        return value;
    }

    private (T? Value, bool WithinTtl) ReadDisk<T>(string key, TimeSpan ttl) where T : class
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return (null, false);

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            return (value, ttl == TimeSpan.MaxValue || age < ttl);
        }
        catch
        {
            // Corrupt or unreadable cache entry: treat it as a miss (and let it be overwritten).
            return (null, false);
        }
    }

    private void WriteDisk<T>(string key, T value)
    {
        try
        {
            PruneOnce();
            Directory.CreateDirectory(_dir);
            // Write via a temporary file so a crash mid-write can't leave a half-written entry
            // that would then be read back as corrupt JSON.
            var path = PathFor(key);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The cache is an optimisation: a read-only or full disk must never break browsing.
        }
    }

    /// <summary>Hashes the key so a slug from the API can never escape the cache folder.</summary>
    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_dir, hash + ".json");
    }

    private void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) != 0) return;
        try
        {
            if (!Directory.Exists(_dir)) return;
            var cutoff = DateTime.UtcNow - MaxDiskAge;
            foreach (var file in Directory.EnumerateFiles(_dir))
            {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                catch { /* in use or gone: skip */ }
            }
        }
        catch { /* best-effort */ }
    }
}
