using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>Error returned by the Playit API (status != success).</summary>
public class PlayitApiException : Exception
{
    public string? ErrorType { get; }
    public bool IsAuthError => ErrorType == "auth";

    public PlayitApiException(string? type, string message) : base(message) => ErrorType = type;
}

/// <summary>
/// Playit.gg API client.
/// - Preferred: a per-user self-managed agent secret key from the partner setup-code flow
///   (<see cref="PlayitPartnerService"/>), set app-wide via <see cref="SetAgentKey"/> and used as
///   <c>agent-key</c> for both reads AND writes.
/// - Fallback (legacy): the agent's secret_key from playit.toml (reads) and a user-pasted write
///   key (writes, sent as Api-Key/Agent-Key). Auth is resolved by trying the schemes in order, so a
///   caller doesn't need to know which kind of key it holds.
/// </summary>
public class PlayitApiService
{
    private const string BaseUrl = "https://api.playit.gg";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Auth schemes tried in order. The per-user agent secret key authenticates as "agent-key"; a
    // legacy account write key as "Api-Key" (with "Agent-Key" as a last resort). Only auth errors
    // fall through to the next scheme; any other API error propagates immediately.
    private static readonly string[] AuthSchemes = { "agent-key", "Api-Key", "Agent-Key" };

    // App-wide per-user agent secret key from the partner setup-code flow. When set, it is the
    // credential used for all Playit API calls (reads and writes), superseding playit.toml.
    private static string? _agentKey;

    /// <summary>
    /// Sets (or clears) the per-user agent secret key used for all Playit API auth. Called once at
    /// startup from settings and again after the setup-code flow mints a new key. Drops the shared
    /// tunnel cache so the next refresh uses the new credential.
    /// </summary>
    public static void SetAgentKey(string? agentSecretKey)
    {
        lock (SecretLock) { _agentKey = string.IsNullOrWhiteSpace(agentSecretKey) ? null : agentSecretKey; }
        InvalidateTunnelCache();
    }

    /// <summary>The credential to read with: the partner agent key if set, else the playit.toml secret.</summary>
    private string? CurrentReadKey()
    {
        lock (SecretLock) { if (_agentKey is not null) return _agentKey; }
        return ReadSecretKey();
    }

    private static readonly string[] TomlPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "playit_gg", "playit.toml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "playit_gg", "playit.toml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "playit_gg", "playit.toml"),
    };

    /// <summary>One tunnel as the agent reports it.</summary>
    /// <remarks>
    /// <c>Proto</c> is "tcp" or "udp": a crossplay server has one of each, because Java is TCP and
    /// Bedrock is UDP. <c>PublicPort</c> is the port players actually connect through, which is
    /// never the local one — Bedrock clients have to be given it by hand, and Geyser needs it for
    /// its <c>broadcast-port</c> setting.
    /// </remarks>
    public record PlayitTunnel(
        string Id, string Name, int LocalPort, string? AssignedDomain, string? CustomDomain,
        string Proto = "tcp", int PublicPort = 0)
    {
        public string? Address => string.IsNullOrEmpty(CustomDomain) ? AssignedDomain : CustomDomain;

        /// <summary>True for the Bedrock half of a crossplay server.</summary>
        public bool IsUdp => string.Equals(Proto, "udp", StringComparison.OrdinalIgnoreCase);
    }

    // --- secret_key cache (EFI-1) ---
    // Every ServerViewModel used to re-read playit.toml from disk on each tunnel refresh; the key
    // almost never changes (only when the user re-installs the agent), so a short TTL removes the
    // repeated file I/O while still picking up a new install quickly.
    private static readonly object SecretLock = new();
    private static string? _cachedSecret;
    private static DateTime _secretReadAtUtc = DateTime.MinValue;
    private static readonly TimeSpan SecretTtl = TimeSpan.FromSeconds(30);

    /// <summary>Reads the (read-only) secret_key from playit.toml (cached ~30 s). Null if not found.</summary>
    public string? ReadSecretKey()
    {
        lock (SecretLock)
        {
            if (DateTime.UtcNow - _secretReadAtUtc < SecretTtl)
                return _cachedSecret;
        }

        var value = ReadSecretKeyUncached();
        lock (SecretLock)
        {
            _cachedSecret = value;
            _secretReadAtUtc = DateTime.UtcNow;
        }
        return value;
    }

    private static string? ReadSecretKeyUncached()
    {
        foreach (var path in TomlPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("secret_key", StringComparison.OrdinalIgnoreCase)) continue;
                    var idx = t.IndexOf('=');
                    if (idx > 0) return t[(idx + 1)..].Trim().Trim('"');
                }
            }
            catch { /* try the next path */ }
        }
        return null;
    }

    private async Task<JsonElement> PostAsync(string path, string authValue, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("Authorization", authValue);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if ((root.TryGetProperty("status", out var s) ? s.GetString() : null) != "success")
        {
            string? type = null, msg = json;
            if (root.TryGetProperty("data", out var d))
            {
                type = d.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                msg = d.TryGetProperty("message", out var m) ? m.GetString() ?? d.ToString() : d.ToString();
            }
            throw new PlayitApiException(type, $"Playit API: {msg ?? json}");
        }
        return root.GetProperty("data").Clone();
    }

    /// <summary>
    /// POSTs trying each auth scheme in <see cref="AuthSchemes"/> until one is not rejected for auth
    /// reasons. Works for both the agent secret key (agent-key) and a legacy write key (Api-Key).
    /// </summary>
    private async Task<JsonElement> PostWithAuthFallbackAsync(string path, string key, string body, CancellationToken ct)
    {
        PlayitApiException? lastAuthError = null;
        foreach (var scheme in AuthSchemes)
        {
            try { return await PostAsync(path, $"{scheme} {key}", body, ct); }
            catch (PlayitApiException ex) when (ex.IsAuthError) { lastAuthError = ex; }
        }
        throw lastAuthError ?? new PlayitApiException("auth", Localizer.Get("Msg_PlayitAuthFail"));
    }

    /// <summary>Reads agent_id and tunnels using <paramref name="key"/> (agent secret or write key).</summary>
    public async Task<(string AgentId, List<PlayitTunnel> Tunnels)> GetRunDataAsync(string key, CancellationToken ct = default)
    {
        var data = await PostWithAuthFallbackAsync("/agents/rundata", key, "", ct);
        var agentId = data.TryGetProperty("agent_id", out var a) ? a.GetString() ?? "" : "";

        var list = new List<PlayitTunnel>();
        if (data.TryGetProperty("tunnels", out var tunnels) && tunnels.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tunnels.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var localPort = t.TryGetProperty("local_port", out var lp) && lp.TryGetInt32(out var p) ? p : 0;
                var assigned = t.TryGetProperty("assigned_domain", out var ad) ? ad.GetString() : null;
                var custom = t.TryGetProperty("custom_domain", out var cd) ? cd.GetString() : null;
                var proto = t.TryGetProperty("proto", out var pr) ? pr.GetString() ?? "tcp" : "tcp";

                // "port" is a range; a Minecraft tunnel is one port, so its start is the one.
                var publicPort = 0;
                if (t.TryGetProperty("port", out var range) && range.ValueKind == JsonValueKind.Object &&
                    range.TryGetProperty("from", out var from) && from.TryGetInt32(out var fp))
                    publicPort = fp;

                list.Add(new PlayitTunnel(id, name, localPort, assigned, custom, proto, publicPort));
            }
        }
        return (agentId, list);
    }

    // --- Shared tunnel-list cache (EFI-1) ---
    // Every ServerViewModel refreshes its tunnel address every ~30 s; with N servers that used to
    // mean N identical HTTP calls to api.playit.gg returning the same data. One shared, throttled
    // fetch serves them all: the first caller inside the TTL window pays the request, the rest
    // await the same task and just filter by their port.
    private static readonly object TunnelCacheLock = new();
    private static Task<List<PlayitTunnel>>? _tunnelFetch;
    private static DateTime _tunnelFetchAtUtc = DateTime.MinValue;
    private static readonly TimeSpan TunnelCacheTtl = TimeSpan.FromSeconds(25);

    /// <summary>Public address of the tunnel whose local port matches <paramref name="port"/>, or null.</summary>
    public async Task<string?> GetAddressForPortAsync(int port, CancellationToken ct = default)
    {
        var tunnels = await GetTunnelsSharedAsync(ct);
        return tunnels?.FirstOrDefault(t => t.LocalPort == port)?.Address;
    }

    /// <summary>
    /// The tunnel on a local port for one protocol, or null. Callers that need the public port —
    /// Bedrock does, since players type it in — need the whole tunnel, not just its address.
    /// </summary>
    /// <remarks>
    /// Matched on the protocol as well as the port, because a crossplay server has two tunnels and
    /// the port alone would return whichever came first.
    /// </remarks>
    public async Task<PlayitTunnel?> GetTunnelAsync(int localPort, bool udp, CancellationToken ct = default)
    {
        var tunnels = await GetTunnelsSharedAsync(ct);
        return tunnels is null ? null : Match(tunnels, localPort, udp);
    }

    /// <summary>
    /// Whether deleting a server should also delete its Bedrock tunnel.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of the Java port. Both deletions used to sit behind one condition
    /// that included reading the Java port out of <c>server.properties</c>, so a server whose file
    /// was unreadable or already gone lost its Bedrock tunnel deletion too — for a tunnel this app
    /// can identify perfectly well from <c>Config.BedrockPort</c> in servers.json. What was left
    /// behind was an orphan UDP tunnel on a port the next crossplay server would be offered as free.
    /// </remarks>
    internal static bool ShouldDeleteBedrockTunnel(bool userAskedToDelete, int? bedrockPort) =>
        userAskedToDelete && bedrockPort is > 0;

    /// <summary>
    /// Local ports of every UDP tunnel on the account, or null when the account could not be asked.
    /// </summary>
    /// <remarks>
    /// For picking a Bedrock port. The system's UDP table only knows what is bound right now, and
    /// the app only knows its own servers, so neither sees a tunnel left behind by a deleted
    /// server, one made by hand on playit's site, or one belonging to another machine on the same
    /// account. Creating a tunnel on a port one of those already holds does not fail — it silently
    /// adopts the other tunnel, and two servers end up advertising one address.
    ///
    /// Null rather than an empty set on failure, so "the account has no UDP tunnels" cannot be
    /// confused with "I could not reach playit".
    /// </remarks>
    public async Task<IReadOnlyCollection<int>?> GetUdpTunnelPortsAsync(CancellationToken ct = default)
    {
        var tunnels = await GetTunnelsSharedAsync(ct);
        return tunnels?.Where(t => t.IsUdp).Select(t => t.LocalPort).ToHashSet();
    }

    /// <summary>
    /// Which tunnel a local port and a protocol identify. The one definition of "the same tunnel".
    /// </summary>
    /// <remarks>
    /// Written once because it had already drifted: the lookup grew the protocol check and the
    /// delete did not, so deleting could pick a tunnel that merely shared the port number. Both go
    /// through this now, and it is small enough to test on its own.
    /// </remarks>
    internal static PlayitTunnel? Match(IEnumerable<PlayitTunnel> tunnels, int localPort, bool udp) =>
        tunnels.FirstOrDefault(t => t.LocalPort == localPort && t.IsUdp == udp);

    private Task<List<PlayitTunnel>> StartTunnelFetch() => Task.Run(async () =>
    {
        var key = CurrentReadKey();
        if (string.IsNullOrEmpty(key)) return new List<PlayitTunnel>();
        var (_, tunnels) = await GetRunDataAsync(key, CancellationToken.None);
        return tunnels;
    });

    private async Task<List<PlayitTunnel>?> GetTunnelsSharedAsync(CancellationToken ct)
    {
        Task<List<PlayitTunnel>> fetch;
        lock (TunnelCacheLock)
        {
            // A failed fetch also stays cached until the TTL expires: no point hammering the API
            // when it's down; the next window retries naturally.
            if (_tunnelFetch is null || DateTime.UtcNow - _tunnelFetchAtUtc >= TunnelCacheTtl)
            {
                _tunnelFetchAtUtc = DateTime.UtcNow;
                _tunnelFetch = StartTunnelFetch(); // detached from any single caller's ct
            }
            fetch = _tunnelFetch;
        }

        try { return await fetch.WaitAsync(ct); }
        catch { return null; }
    }

    /// <summary>Drops the shared tunnel cache (called after creating/deleting a tunnel).</summary>
    private static void InvalidateTunnelCache()
    {
        lock (TunnelCacheLock)
        {
            _tunnelFetch = null;
            _tunnelFetchAtUtc = DateTime.MinValue;
        }
    }

    /// <summary>Which edition a tunnel carries: they are different protocols, not a preference.</summary>
    /// <remarks>
    /// Java is TCP and Bedrock is UDP, so a crossplay server genuinely needs two tunnels — a TCP
    /// tunnel carries no Bedrock traffic at all. The identifiers below are playit's own, taken from
    /// their agent's source rather than guessed; a wrong one creates a tunnel that looks fine in
    /// their dashboard and silently carries nothing.
    /// </remarks>
    public enum TunnelEdition
    {
        Java,
        Bedrock
    }

    /// <summary>The pair playit's API expects for an edition. Internal so it can be pinned by a test.</summary>
    internal static (string Type, string Port) Wire(TunnelEdition edition) => edition switch
    {
        TunnelEdition.Bedrock => ("minecraft-bedrock", "udp"),
        _ => ("minecraft-java", "tcp")
    };

    /// <summary>
    /// Creates a tunnel for the server if one doesn't already exist for that port and edition.
    /// Returns true if it created one, false if it already existed. <paramref name="key"/> is the
    /// per-user agent secret key (preferred) or a legacy write key.
    /// </summary>
    public async Task<bool> EnsureMinecraftTunnelAsync(string key, string name, int localPort,
        TunnelEdition edition = TunnelEdition.Java, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(Localizer.Get("Msg_MissingWriteKey"));

        var (tunnelType, portType) = Wire(edition);

        var (agentId, tunnels) = await GetRunDataAsync(key, ct);

        // Matched on protocol too: a crossplay server has two tunnels, and on a machine where the
        // Java and Bedrock local ports happened to coincide, matching on the port alone would see
        // the Java one and decide the Bedrock one already existed.
        if (tunnels.Any(t => t.LocalPort == localPort &&
                             string.Equals(t.Proto, portType, StringComparison.OrdinalIgnoreCase)))
            return false;

        var body = new JsonObject
        {
            ["name"] = name,
            ["tunnel_type"] = tunnelType,
            ["port_type"] = portType,
            ["port_count"] = 1,
            ["enabled"] = true,
            ["origin"] = new JsonObject
            {
                ["type"] = "agent",
                ["data"] = new JsonObject
                {
                    ["agent_id"] = agentId,
                    ["local_ip"] = "127.0.0.1",
                    ["local_port"] = localPort
                }
            }
        }.ToJsonString();

        await PostWithAuthFallbackAsync("/tunnels/create", key, body, ct);
        InvalidateTunnelCache(); // so the next address refresh sees the new tunnel right away
        return true;
    }

    /// <summary>
    /// Deletes the tunnel with this local port <em>and</em> this protocol. True if one was deleted.
    /// <paramref name="key"/> is the per-user agent secret key (preferred) or a legacy write key.
    /// </summary>
    /// <remarks>
    /// The protocol is half the identity, the same as in <see cref="GetTunnelAsync"/> and
    /// <see cref="EnsureMinecraftTunnelAsync"/>: a crossplay server owns two tunnels, Java over TCP
    /// and Bedrock over UDP, and matching on the port alone deletes whichever the account happens to
    /// list first. Deleting a tunnel is not undoable, so this is the one place where guessing is
    /// least acceptable.
    /// </remarks>
    public async Task<bool> DeleteTunnelForPortAsync(string key, int localPort, bool udp,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(Localizer.Get("Msg_MissingWriteKey"));

        var (_, tunnels) = await GetRunDataAsync(key, ct);
        var match = Match(tunnels, localPort, udp);
        if (match is null || string.IsNullOrEmpty(match.Id)) return false;

        var body = new JsonObject { ["tunnel_id"] = match.Id }.ToJsonString();
        await PostWithAuthFallbackAsync("/tunnels/delete", key, body, ct);
        InvalidateTunnelCache(); // so the next address refresh stops showing the deleted tunnel
        return true;
    }
}
