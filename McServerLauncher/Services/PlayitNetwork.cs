using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>How one Playit region is doing, as seen from this machine.</summary>
public enum PlayitRegionState
{
    /// <summary>Not measured yet.</summary>
    Pending,

    /// <summary>It answers from one of its own datacenters.</summary>
    Operational,

    /// <summary>It answers, but from a datacenter somewhere else: its own is out and traffic goes round.</summary>
    Rerouted,

    /// <summary>It did not answer, or Playit itself lists it as offline.</summary>
    Down,
}

/// <summary>One of the regions Playit can be reached through, and the datacenters that belong to it.</summary>
/// <param name="Code">The prefix of its test host: <c>https://{Code}.rt.playit.gg</c>.</param>
/// <param name="Slug">Its name in Playit's API (<c>/query/region</c>, <c>/info/pops</c>).</param>
/// <param name="HomeDataCenters">Datacenter ids that count as its own; empty means any.</param>
public sealed record PlayitRegion(string Code, string Slug, IReadOnlyCollection<int> HomeDataCenters)
{
    /// <summary>The key of its display name in the string resources.</summary>
    public string NameKey => "PlayNet_R_" + Slug.Replace('-', '_');

    /// <summary>Whether any datacenter will do (the global anycast address).</summary>
    public bool AcceptsAny => HomeDataCenters.Count == 0;
}

/// <summary>What one test host answered: which datacenter served it, and how fast.</summary>
/// <param name="Answered">False when it did not answer at all.</param>
public sealed record PlayitRegionProbe(bool Answered, int? DataCenterId, string? DataCenterName,
    double? PingMs, double? JitterMs);

/// <summary>A region after measuring it.</summary>
/// <param name="ServedBy">For a rerouted region, the region its datacenter belongs to.</summary>
public sealed record PlayitRegionResult(PlayitRegion Region, PlayitRegionState State, PlayitRegionProbe? Probe,
    PlayitRegion? ServedBy);

/// <summary>
/// The regions and datacenters of Playit's network, and how to tell from one answer whether a region
/// is healthy.
/// </summary>
/// <remarks>
/// <para>
/// Playit has no status page. What it does have, without an account, is a test host per region
/// (<c>https://eu.rt.playit.gg</c> and so on) that answers with the datacenter that served it —
/// <c>{"dc":"madrid-2","dc_id":37,"region":"europe"}</c> — which is what its own ping page,
/// ping.playit.gg, times. When a region's datacenter is out, its host is answered from another one:
/// on 2026-11-14 Poland's was served by Stockholm. That is what "rerouted" means here.
/// </para>
/// <para>
/// The datacenter ids and the region each belongs to are the table ping.playit.gg ships. An id that
/// is not in it is a datacenter added since; it is never called a reroute, because nothing here can
/// say where it is. The regions are listed in the order ping.playit.gg shows them.
/// </para>
/// </remarks>
public static class PlayitNetworkCatalog
{
    // Datacenter ids, from the table in ping.playit.gg's script (2026-11-14).
    private const int FremontCa = 2, Atlanta = 7, Stockholm = 11, Tokyo = 16, Seoul = 17, Singapore = 18,
        Sydney = 19, SaoPaulo = 20, Warsaw = 22, Mumbai = 28, Santiago = 34, Frankfurt = 36, Madrid = 37,
        London = 38, Bucharest = 39, LosAngeles = 40, NewYork = 41, Seattle = 42, Denver = 43, Dallas = 44,
        Miami = 45, Chicago = 46;

    private static readonly int[] NorthAmerica =
        [FremontCa, Atlanta, LosAngeles, NewYork, Seattle, Denver, Dallas, Miami, Chicago];

    private static readonly int[] Europe = [Stockholm, Warsaw, Frankfurt, Madrid, London, Bucharest];

    /// <summary>Every region, in the order Playit's ping page shows them.</summary>
    public static readonly IReadOnlyList<PlayitRegion> Regions =
    [
        new("gl", "global", []),
        new("na", "north-america", NorthAmerica),
        new("eu", "europe", Europe),
        new("as", "asia", [Tokyo, Seoul, Singapore]),
        new("in", "india", [Mumbai]),
        new("sa", "south-america", [SaoPaulo]),
        new("chl", "chile", [Santiago]),
        new("sea", "seattle-washington", [Seattle]),
        new("los", "california", [FremontCa, LosAngeles]),
        new("den", "denver-colorado", [Denver]),
        new("dal", "dallas-texas", [Dallas]),
        new("chi", "chicago-illinois", [Chicago]),
        new("nyc", "new-york", [NewYork]),
        new("mia", "miami", [Miami]),
        new("atl", "atlanta", [Atlanta]),
        new("gbr", "united-kingdom", [London]),
        new("deu", "germany", [Frankfurt]),
        new("swe", "sweden", [Stockholm]),
        new("pol", "poland", [Warsaw]),
        new("rou", "romania", [Bucharest]),
        new("jpn", "japan", [Tokyo, Seoul]),
        new("aus", "australia", [Sydney]),
    ];

    /// <summary>Every datacenter id this table knows.</summary>
    internal static IEnumerable<int> KnownDataCenters => Regions.SelectMany(r => r.HomeDataCenters).Distinct();

    /// <summary>The region with this API name, or null.</summary>
    public static PlayitRegion? BySlug(string? slug) =>
        Regions.FirstOrDefault(r => string.Equals(r.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The narrowest region a datacenter belongs to — Stockholm is Sweden before it is Europe — or
    /// null for a datacenter this table does not know.
    /// </summary>
    public static PlayitRegion? HomeOf(int dataCenterId) => Regions
        .Where(r => r.HomeDataCenters.Contains(dataCenterId))
        .OrderBy(r => r.HomeDataCenters.Count)
        .FirstOrDefault();

    /// <summary>What a region's state is, from what its test host answered and what Playit lists.</summary>
    /// <param name="region">The region measured.</param>
    /// <param name="probe">What its test host answered.</param>
    /// <param name="listedOffline">Playit's own list (<c>/info/pops</c>) has every datacenter of this
    /// region offline.</param>
    public static PlayitRegionResult Classify(PlayitRegion region, PlayitRegionProbe probe, bool listedOffline = false)
    {
        if (!probe.Answered || listedOffline)
            return new(region, PlayitRegionState.Down, probe, null);
        if (region.AcceptsAny || probe.DataCenterId is not { } dc || region.HomeDataCenters.Contains(dc))
            return new(region, PlayitRegionState.Operational, probe, null);
        // Served from a datacenter of its own region is fine; one we cannot place is not called a reroute.
        return HomeOf(dc) is { } home
            ? new(region, PlayitRegionState.Rerouted, probe, home)
            : new(region, PlayitRegionState.Operational, probe, null);
    }

    /// <summary>
    /// The regions that <c>/info/pops</c> shows fully offline: those with at least one datacenter
    /// listed and none of them online.
    /// </summary>
    public static HashSet<string> OfflineSlugs(IEnumerable<(string Slug, bool Online)> pops) => pops
        .GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.All(p => !p.Online))
        .Select(g => g.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A datacenter's name as a person reads it: <c>madrid-2</c> is Madrid, <c>sao-paulo-1</c> Sao Paulo.</summary>
    public static string CityOf(string? dataCenterName)
    {
        if (string.IsNullOrWhiteSpace(dataCenterName)) return "";
        var parts = dataCenterName.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && parts[^1].All(char.IsDigit)) parts.RemoveAt(parts.Count - 1);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(p[0])).Append(p.AsSpan(1));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The latency and jitter of a set of round trips, the way a ping page reports them: the median,
    /// so one slow trip does not stand for the rest, and the mean change from one trip to the next.
    /// </summary>
    public static (double Ping, double Jitter) Stats(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return (0, 0);
        var sorted = samples.OrderBy(s => s).ToArray();
        var mid = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        double jitter = 0;
        for (var i = 1; i < samples.Count; i++) jitter += Math.Abs(samples[i] - samples[i - 1]);
        return (median, samples.Count > 1 ? jitter / (samples.Count - 1) : 0);
    }

    /// <summary>Reads a test host's answer: <c>{"dc":"madrid-2","dc_id":37,"region":"europe"}</c>.</summary>
    public static (int? Id, string? Name) ParseDataCenter(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int? id = root.TryGetProperty("dc_id", out var i) && i.TryGetInt32(out var n) ? n : null;
            string? name = root.TryGetProperty("dc", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            return (id, name);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Reads <c>/query/region</c>: the region this machine is routed to, or null.</summary>
    public static string? ParseYourRegion(string json) =>
        Data(json) is { } data && data.TryGetProperty("region", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

    /// <summary>Reads <c>/info/pops</c>: every datacenter Playit lists, by region, and whether it is up.</summary>
    public static List<(string Slug, bool Online)> ParsePops(string json)
    {
        var list = new List<(string, bool)>();
        if (Data(json) is not { } data || !data.TryGetProperty("pops", out var pops)
            || pops.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var pop in pops.EnumerateArray())
            if (pop.TryGetProperty("region", out var r) && r.ValueKind == JsonValueKind.String)
                list.Add((r.GetString()!, !pop.TryGetProperty("online", out var o) || o.ValueKind != JsonValueKind.False));
        return list;
    }

    // Playit's API wraps answers as {"status":"success","data":{...}}.
    private static JsonElement? Data(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var s) && s.GetString() != "success") return null;
            return root.TryGetProperty("data", out var d) ? d.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Measures Playit's network from this machine: every region's test host, timed, plus what Playit's
/// API says about its datacenters and about where this machine is routed. Needs no account.
/// </summary>
/// <remarks>
/// Each region gets one request to learn which datacenter serves it (and to open the connection),
/// then a few timed <c>HEAD</c> requests, as ping.playit.gg does; only the timed ones count, so the
/// TLS handshake is not taken for latency. A few regions are measured at a time — enough to fill the
/// screen in seconds, few enough not to slow each other down.
/// </remarks>
public sealed class PlayitNetworkService
{
    private const string ApiBase = "https://api.playit.gg";
    private const int Samples = 4;
    private const int Parallel = 4;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private static readonly HttpClient Http = AppHttp.Create(Timeout.InfiniteTimeSpan);

    /// <summary>The region Playit routes this machine to (its <c>/query/region</c>), or null.</summary>
    public async Task<string?> YourRegionAsync(CancellationToken ct)
    {
        try { return PlayitNetworkCatalog.ParseYourRegion(await PostAsync("/query/region", ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>The regions Playit lists as fully offline, or an empty set when it would not say.</summary>
    public async Task<HashSet<string>> OfflineRegionsAsync(CancellationToken ct)
    {
        try { return PlayitNetworkCatalog.OfflineSlugs(PlayitNetworkCatalog.ParsePops(await PostAsync("/info/pops", ct))); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Measures every region, reporting each as soon as it is done.</summary>
    public async Task MeasureAsync(Action<PlayitRegion, PlayitRegionProbe> onRegion, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(PlayitNetworkCatalog.Regions.Select(async region =>
        {
            await gate.WaitAsync(ct);
            try { onRegion(region, await ProbeAsync(region, ct)); }
            finally { gate.Release(); }
        }));
    }

    /// <summary>One region: which datacenter answers it, and the round trips that follow.</summary>
    public static async Task<PlayitRegionProbe> ProbeAsync(PlayitRegion region, CancellationToken ct)
    {
        var url = $"https://{region.Code}.rt.playit.gg/";
        try
        {
            int? id;
            string? name;
            using (var first = new CancellationTokenSource(RequestTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, first.Token))
            using (var resp = await Http.GetAsync(url + "?s=0", HttpCompletionOption.ResponseHeadersRead, linked.Token))
            {
                resp.EnsureSuccessStatusCode();
                (id, name) = PlayitNetworkCatalog.ParseDataCenter(
                    await AppHttp.ReadCappedStringAsync(resp.Content, AppHttp.MaxSmallTextBytes, linked.Token));
            }

            var samples = new List<double>(Samples);
            for (var i = 1; i <= Samples; i++)
            {
                using var each = new CancellationTokenSource(RequestTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, each.Token);
                using var req = new HttpRequestMessage(HttpMethod.Head, $"{url}?s={i}");
                var watch = Stopwatch.StartNew();
                try
                {
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                    watch.Stop();
                    if (resp.IsSuccessStatusCode) samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // One slow round trip is a lost sample, not a dead region.
                }
            }

            if (samples.Count == 0) return new(true, id, name, null, null);
            var (ping, jitter) = PlayitNetworkCatalog.Stats(samples);
            return new(true, id, name, ping, jitter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(false, null, null, null, null);
        }
    }

    private static async Task<string> PostAsync(string path, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(ApiBase + path, content, linked.Token);
        resp.EnsureSuccessStatusCode();
        return await AppHttp.ReadCappedStringAsync(resp.Content, AppHttp.MaxSmallTextBytes, linked.Token);
    }
}
