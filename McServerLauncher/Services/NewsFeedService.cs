using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>How the last look at the notices file ended.</summary>
public enum NewsFeedStatus
{
    /// <summary>A newer file was downloaded, checked and installed.</summary>
    Ok,

    /// <summary>The copy already held is the current one (the server said so, or it was asked too recently).</summary>
    NotModified,

    /// <summary>No server has a file yet. Not an error: a project with nothing to say has no file to publish.</summary>
    NoFeed,

    /// <summary>
    /// The file could not be had or could not be trusted: no network, a server error, a rate limit, a
    /// signature that is missing or wrong, a file that does not parse, a replay of an older one. Never
    /// to be read as "there are no notices" — the copy held is all there is, and the screen says so.
    /// </summary>
    Failed,
}

/// <summary>
/// Downloads the signed notices file (<c>news.json</c>) and keeps a copy that is checked again every
/// time it is read.
/// </summary>
/// <remarks>
/// <para>
/// What is accepted is decided before anything is written: the file must arrive whole (64 KiB at
/// most), its signature (<c>news.json.sig</c>, a second request) must verify over the exact bytes,
/// it must parse, and its <c>seq</c> must not be lower than the highest ever accepted, which is what
/// stops somebody serving an old file that was signed correctly in its day. Only then are the files
/// replaced, and the ETag is stored last of all: an ETag stored for a body that was then refused
/// would make every later request answer 304 and the corrected signature would never be picked up.
/// </para>
/// <para>
/// Two hosts serve the same file (the website and GitHub Pages). They are tried in order and the first
/// that gives an answer counts; a host that fails or serves something unacceptable hands over to the
/// next. Nothing is sent but the request itself: no version, no language, no identifier. The server
/// sees an address and the app's User-Agent, like any download.
/// </para>
/// <para>
/// Nothing here throws. Whatever goes wrong is a <see cref="NewsFeedStatus"/>, and the last good copy
/// stays.
/// </para>
/// </remarks>
public sealed class NewsFeedService
{
    /// <summary>The website first, GitHub Pages second: the same file, published to both.</summary>
    public static readonly IReadOnlyList<string> DefaultUrls =
    [
        "https://mc-server-launcher.vercel.app/news.json",
        "https://juanp-g.github.io/MC-ServerLauncher/news.json",
    ];

    /// <summary>The most a notices file may weigh. A real one is a few kilobytes.</summary>
    internal const long MaxFeedBytes = AppHttp.MaxSmallTextBytes;

    /// <summary>The most a signature may weigh; a real one is under a hundred bytes.</summary>
    internal const long MaxSignatureBytes = 4096;

    /// <summary>After a failure, the least time before another try that nobody asked for.</summary>
    internal static readonly TimeSpan RetryGap = TimeSpan.FromMinutes(2);

    internal const string FileName = "news.json";
    internal const string SignatureFileName = "news.json.sig";
    internal const string StampFileName = "news.stamp";

    private static readonly HttpClient SharedHttp = AppHttp.Create(TimeSpan.FromSeconds(15));

    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _urls;
    private readonly IReadOnlyList<string>? _keys;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loading = new();

    private bool _loaded;
    private NewsFeed? _copy;
    private string _etag = string.Empty;
    private int _seq;
    private DateTimeOffset? _checkedAt;
    private DateTimeOffset? _lastFailure;

    /// <param name="cacheFolder">Where the copy lives; null for the app's own <c>cache</c> folder.</param>
    /// <param name="http">The client; null for the shared one. Tests pass one over a fake handler.</param>
    /// <param name="urls">Where to look, in order; null for <see cref="DefaultUrls"/>.</param>
    /// <param name="keys">The keys whose signatures count; null for the ones built into the app.</param>
    /// <param name="clock">The time; null for the real one.</param>
    public NewsFeedService(string? cacheFolder = null, HttpClient? http = null, IReadOnlyList<string>? urls = null,
        IReadOnlyList<string>? keys = null, Func<DateTimeOffset>? clock = null)
    {
        _folder = cacheFolder ?? AppPaths.In("cache");
        _http = http ?? SharedHttp;
        _urls = urls ?? DefaultUrls;
        _keys = keys;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The notices of the copy held, checked again against its signature as it is read; null when there
    /// is none (nothing downloaded yet, or what is on disk does not verify).
    /// </summary>
    public NewsFeed? Feed
    {
        get
        {
            EnsureLoaded();
            return _copy;
        }
    }

    /// <summary>The highest <c>seq</c> ever accepted; a file below it is refused.</summary>
    public int AcceptedSeq
    {
        get
        {
            EnsureLoaded();
            return _seq;
        }
    }

    /// <summary>How the last look ended; null before the first one of this run.</summary>
    public NewsFeedStatus? LastStatus { get; private set; }

    /// <summary>When a server last said the copy held is current (or gave a newer one); null when it never has.</summary>
    public DateTimeOffset? CheckedAt
    {
        get
        {
            EnsureLoaded();
            return _checkedAt;
        }
    }

    /// <summary>
    /// Looks for a newer file. Never throws.
    /// </summary>
    /// <param name="onlyIfOlderThan">When given, nothing is asked if the copy was confirmed more recently
    /// than that, or if the last try failed less than <see cref="RetryGap"/> ago: for the callers that
    /// are not on a timer, such as opening the section. Null always asks.</param>
    /// <param name="ct">Stops the wait.</param>
    public async Task<NewsFeedStatus> RefreshAsync(TimeSpan? onlyIfOlderThan = null, CancellationToken ct = default)
    {
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return NewsFeedStatus.Failed; }

        try
        {
            EnsureLoaded();
            var now = _clock();

            if (onlyIfOlderThan is { } age)
            {
                if (_lastFailure is { } failed && now - failed < RetryGap) return NewsFeedStatus.Failed;
                if (_checkedAt is { } at && now - at < age) return NewsFeedStatus.NotModified;
            }

            var status = await LookAsync(now, ct);
            LastStatus = status;
            _lastFailure = status == NewsFeedStatus.Failed ? now : null;
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NewsFeedStatus> LookAsync(DateTimeOffset now, CancellationToken ct)
    {
        var anyFailed = false;
        foreach (var url in _urls)
        {
            var status = await TryHostAsync(url, now, ct);
            if (status is NewsFeedStatus.Ok or NewsFeedStatus.NotModified) return status;
            if (status == NewsFeedStatus.Failed) anyFailed = true;
        }

        // Every host said "no file" is a project with nothing published; one that failed is a question
        // left open, and an open question is never reported as an empty answer.
        return anyFailed ? NewsFeedStatus.Failed : NewsFeedStatus.NoFeed;
    }

    private async Task<NewsFeedStatus> TryHostAsync(string url, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Only a copy that verified earns an If-None-Match: a 304 would be "keep what you have".
            if (_copy is not null && _etag.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (_copy is null) return NewsFeedStatus.Failed;   // "not modified" with nothing to keep makes no sense
                Confirm(now);
                return NewsFeedStatus.NotModified;
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return NewsFeedStatus.NoFeed;
            if (!response.IsSuccessStatusCode) return NewsFeedStatus.Failed;

            var raw = await AppHttp.ReadCappedAsync(response.Content, MaxFeedBytes, ct);
            var etag = response.Headers.ETag?.ToString() ?? string.Empty;

            using var signatureRequest = new HttpRequestMessage(HttpMethod.Get, url + ".sig");
            using var signatureResponse = await _http.SendAsync(signatureRequest,
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!signatureResponse.IsSuccessStatusCode) return NewsFeedStatus.Failed;
            var signature = Encoding.UTF8.GetString(
                await AppHttp.ReadCappedAsync(signatureResponse.Content, MaxSignatureBytes, ct));

            if (!UpdateSignature.VerifyNews(raw, signature, _keys))
            {
                Safe.Note("News", "a notices file whose signature does not verify was refused: " + url,
                    new InvalidDataException("bad signature"));
                return NewsFeedStatus.Failed;
            }

            var parsed = NewsFeedParser.Parse(raw);     // before anything is written: a bad file replaces nothing
            if (parsed.Seq < _seq)
            {
                Safe.Note("News", "a notices file older than the one already accepted was refused: " + url,
                    new InvalidDataException($"seq {parsed.Seq} < {_seq}"));
                return NewsFeedStatus.Failed;
            }

            Save(raw, signature, etag, parsed.Seq, now);
            lock (_loading)
            {
                _copy = parsed;
                _etag = etag;
                _seq = parsed.Seq;
                _checkedAt = now;
            }
            return NewsFeedStatus.Ok;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Safe.Note("News", "the notices file could not be fetched from " + url, ex);
            return NewsFeedStatus.Failed;
        }
    }

    /// <summary>The server said the copy is current: remember when, and nothing else.</summary>
    private void Confirm(DateTimeOffset now)
    {
        lock (_loading) _checkedAt = now;
        try { WriteStamp(_etag, now, _seq); }
        catch (Exception ex) { Safe.Note("News", "could not record when the notices were last confirmed", ex); }
    }

    // ---- the copy on disk ----

    private void EnsureLoaded()
    {
        lock (_loading)
        {
            if (_loaded) return;
            _loaded = true;
            LoadFromDisk();
        }
    }

    private void LoadFromDisk()
    {
        // The stamp first: the highest seq ever accepted outlives a copy that no longer verifies.
        var (etag, checkedAt, seq) = ReadStamp();
        _seq = seq;

        try
        {
            var path = Path.Combine(_folder, FileName);
            var signaturePath = Path.Combine(_folder, SignatureFileName);
            if (!File.Exists(path) || !File.Exists(signaturePath)) return;

            var raw = File.ReadAllBytes(path);
            // Checked again here: the folder is writable by anything on the machine, and a copy that
            // does not verify is no more trustworthy than one off the wire.
            if (!UpdateSignature.VerifyNews(raw, File.ReadAllText(signaturePath), _keys)) return;

            _copy = NewsFeedParser.Parse(raw);
            _etag = etag;
            _checkedAt = checkedAt;
            _seq = Math.Max(_seq, _copy.Seq);
        }
        catch (Exception ex)
        {
            Safe.Note("News", "the saved notices could not be read, going without them", ex);
            _copy = null;
            _etag = string.Empty;
        }
    }

    private void Save(byte[] raw, string signature, string etag, int seq, DateTimeOffset now)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, FileName);
        var signaturePath = Path.Combine(_folder, SignatureFileName);

        File.WriteAllBytes(path + ".part", raw);
        File.WriteAllText(signaturePath + ".part", signature);
        File.Move(path + ".part", path, overwrite: true);
        File.Move(signaturePath + ".part", signaturePath, overwrite: true);

        // Last of all: the ETag belongs to a body that is now installed and verified.
        WriteStamp(etag, now, seq);
    }

    private (string Etag, DateTimeOffset? CheckedAt, int Seq) ReadStamp()
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(_folder, StampFileName));
            var etag = lines.Length > 0 ? lines[0].Trim() : string.Empty;
            DateTimeOffset? at = lines.Length > 1 && DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            var seq = lines.Length > 2 && int.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture,
                out var number) ? number : 0;
            return (etag, at, seq);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (string.Empty, null, 0);     // no stamp yet: a first run
        }
    }

    private void WriteStamp(string etag, DateTimeOffset at, int seq)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, StampFileName),
            string.Join('\n', etag, at.ToString("O", CultureInfo.InvariantCulture),
                seq.ToString(CultureInfo.InvariantCulture)));
    }
}
