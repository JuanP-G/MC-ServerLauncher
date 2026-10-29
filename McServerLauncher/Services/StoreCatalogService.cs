using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McServerLauncher.Services;

/// <summary>What the catalogue knows about one project.</summary>
/// <param name="Topic">The app's tag id that best describes it, or null when the models were unsure.</param>
/// <param name="Also">Other tag ids that fit nearly as well.</param>
/// <param name="Confidence">The probability the model put on <paramref name="Topic"/>.</param>
/// <param name="Summaries">One sentence per language code ("es", "en", "de", "fr", "pt").</param>
public sealed record StoreCatalogEntry(
    string? Topic,
    IReadOnlyList<string> Also,
    double Confidence,
    IReadOnlyDictionary<string, string> Summaries);

/// <summary>
/// The store catalogue: what each popular project is about and what it does, decided outside the app.
/// </summary>
/// <remarks>
/// <para>
/// The rule-based tags (<see cref="StoreTagService"/>) read a title and a one-line description, and
/// that is not enough: a mod about backpacks and a mod about armour are both "equipment" to anyone
/// counting words. So a pipeline in <c>tools/store-catalog</c> asks a small decision model one closed
/// question per project, and a text model for a sentence in five languages, and publishes the answers
/// as one gzip on the <c>catalog</c> branch. This class is the app's end of it.
/// </para>
/// <para>
/// It is a read-only, anonymous download of a public file, the same kind of request as the update
/// check: nothing about the user or the server goes out, there is no key in the app, and the models
/// are never called from here. The file is fetched at most once a day, with its ETag, and kept on
/// disk, so the store works offline on the last one. Everything in it is treated as data from the
/// outside: size-capped, shape-checked, and ignored whole if anything about it is wrong, because a
/// store with the rule-based tags is a working store and one with half a catalogue is not.
/// </para>
/// <para>
/// What the catalogue says never beats a person: a hand-written summary in
/// <c>store-summaries.json</c> comes first, and tags it names that the app does not know are skipped.
/// </para>
/// </remarks>
public sealed class StoreCatalogService
{
    /// <summary>Where the pipeline publishes. A raw file, so the request carries nothing to log.</summary>
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/JuanP-G/MC-ServerLauncher/catalog/store-catalog.json.gz";

    /// <summary>The newest file format this build understands.</summary>
    internal const int SupportedVersion = 1;

    internal const long MaxDownloadBytes = 16L * 1024 * 1024;
    internal const long MaxUnpackedBytes = 96L * 1024 * 1024;
    private const int MaxTextLength = 400;
    private const int MaxIdLength = 32;

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromHours(1);
    private static readonly string[] Languages = { "es", "en", "de", "fr", "pt" };

    private static readonly HttpClient SharedHttp = AppHttp.Create(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Shared instance. Declared after the static fields it uses: static initialisers run in the
    /// order they are written, and the constructor needs them.
    /// </summary>
    public static StoreCatalogService Shared { get; } = new();

    private const string FileName = "store-catalog.json.gz";
    private const string EtagFileName = "store-catalog.etag";

    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _loading = new();

    private Dictionary<string, StoreCatalogEntry>? _entries;
    private DateTimeOffset? _lastAttempt;

    public StoreCatalogService(string? cacheFolder = null, HttpClient? http = null, string? url = null,
        Func<DateTimeOffset>? clock = null)
    {
        _folder = cacheFolder ?? AppPaths.In("cache");
        _http = http ?? SharedHttp;
        _url = url ?? DefaultUrl;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }


    /// <summary>How many projects the catalogue covers. Zero until one has been downloaded.</summary>
    public int Count => Entries.Count;

    /// <summary>The entry for a Modrinth project id, or null when the catalogue has none.</summary>
    public StoreCatalogEntry? Find(string? projectId) =>
        !string.IsNullOrEmpty(projectId) && Entries.TryGetValue(projectId, out var entry) ? entry : null;

    /// <summary>
    /// The sentence for a project in <paramref name="language"/> (a two-letter code), English when
    /// that language is missing, null when the catalogue has nothing for it.
    /// </summary>
    public string? SummaryFor(string? projectId, string language)
    {
        var summaries = Find(projectId)?.Summaries;
        if (summaries is null) return null;
        if (summaries.TryGetValue(language, out var text)) return text;
        return summaries.TryGetValue("en", out var english) ? english : null;
    }

    private Dictionary<string, StoreCatalogEntry> Entries
    {
        get
        {
            if (_entries is { } ready) return ready;
            lock (_loading) return _entries ??= LoadFromDisk();
        }
    }

    private Dictionary<string, StoreCatalogEntry> LoadFromDisk()
    {
        try
        {
            var path = Path.Combine(_folder, FileName);
            if (File.Exists(path)) return Parse(File.ReadAllBytes(path));
        }
        catch
        {
            // Unreadable or from a format this build does not know: no catalogue, never a crash.
        }

        return new Dictionary<string, StoreCatalogEntry>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Fetches the catalogue if the copy on disk is more than a day old, and installs it when it
    /// parses. Never throws: offline, or GitHub down, or a file that does not parse, all leave the
    /// last good copy in place.
    /// </summary>
    /// <returns>True when a newer catalogue was installed.</returns>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var now = _clock();
            // One try an hour at most, so an offline machine does not wait out a timeout each time
            // the Mods tab is opened.
            if (_lastAttempt is { } last && now - last < RetryEvery) return false;
            _lastAttempt = now;

            var path = Path.Combine(_folder, FileName);
            var etagPath = Path.Combine(_folder, EtagFileName);
            var haveCopy = File.Exists(path);
            var (etag, checkedAt) = haveCopy ? ReadStamp(etagPath) : (string.Empty, null);

            if (checkedAt is { } stamped && now - stamped < RefreshEvery) return false;

            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            if (etag.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                WriteStamp(etagPath, etag, now);
                return false;
            }

            if (!response.IsSuccessStatusCode) return false;
            if (response.Content.Headers.ContentLength > MaxDownloadBytes) return false;

            var bytes = await ReadCappedAsync(response.Content, MaxDownloadBytes, ct);
            var parsed = Parse(bytes);      // before anything is written: a bad file replaces nothing

            Directory.CreateDirectory(_folder);
            var part = path + ".part";
            await File.WriteAllBytesAsync(part, bytes, ct);
            File.Move(part, path, overwrite: true);
            WriteStamp(etagPath, response.Headers.ETag?.ToString() ?? string.Empty, now);

            lock (_loading) _entries = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The ETag of the copy on disk and when it was last confirmed current. Both in one small file,
    /// and the time is the service's own clock rather than the file's modified date, so a copy
    /// restored from a backup is not mistaken for a fresh one.
    /// </summary>
    private static (string Etag, DateTimeOffset? CheckedAt) ReadStamp(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var etag = lines.Length > 0 ? lines[0].Trim() : string.Empty;
            DateTimeOffset? at = lines.Length > 1 && DateTimeOffset.TryParse(lines[1], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            return (etag, at);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private static void WriteStamp(string path, string etag, DateTimeOffset at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, etag + "\n" + at.ToString("O"));
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, long limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("catalogue too large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a catalogue file. Throws on anything that is not one: a file in a newer format, or
    /// something that does not unzip or parse. Entries that are individually wrong are skipped.
    /// </summary>
    internal static Dictionary<string, StoreCatalogEntry> Parse(byte[] gzip)
    {
        using var source = new MemoryStream(gzip);
        using var unzip = new GZipStream(source, CompressionMode.Decompress);
        using var text = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = unzip.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (text.Length + read > MaxUnpackedBytes) throw new InvalidDataException("catalogue too large");
            text.Write(chunk, 0, read);
        }
        text.Position = 0;

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not a catalogue");
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number)
            || number is < 1 or > SupportedVersion)
            throw new InvalidDataException("a catalogue format this build does not know");
        if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("no projects");

        var entries = new Dictionary<string, StoreCatalogEntry>(StringComparer.Ordinal);
        foreach (var project in projects.EnumerateObject())
        {
            if (project.Name.Length is 0 or > MaxIdLength || project.Value.ValueKind != JsonValueKind.Object)
                continue;
            entries[project.Name] = ReadEntry(project.Value);
        }
        return entries;
    }

    private static StoreCatalogEntry ReadEntry(JsonElement element)
    {
        var topic = element.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String
            ? Clean(t.GetString(), 40) : null;

        var also = new List<string>();
        if (element.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Array)
            foreach (var item in x.EnumerateArray().Take(4))
                if (item.ValueKind == JsonValueKind.String && Clean(item.GetString(), 40) is { } id)
                    also.Add(id);

        var confidence = element.TryGetProperty("c", out var c) && c.TryGetDouble(out var value)
            ? Math.Clamp(value, 0, 1) : 0;

        var summaries = new Dictionary<string, string>();
        if (element.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Object)
            foreach (var language in Languages)
                if (s.TryGetProperty(language, out var sentence) && sentence.ValueKind == JsonValueKind.String
                    && Clean(sentence.GetString(), MaxTextLength) is { } clean)
                    summaries[language] = clean;

        return new StoreCatalogEntry(topic, also, confidence, summaries);
    }

    /// <summary>
    /// A string from the file, on one line and no longer than <paramref name="max"/>; null when
    /// nothing is left. Text that came from a model and a download has no business carrying line
    /// breaks or control characters into a card.
    /// </summary>
    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        flat = string.Join(' ', flat.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return flat.Length == 0 ? null : flat.Length > max ? flat[..max] : flat;
    }
}
