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
/// - Reads (list tunnels / address): use the agent's secret_key (playit.toml), which is usually
///   read-only.
/// - Writes (create/delete tunnel): require a key with write permission provided by the
///   user (sent as Api-Key, with an Agent-Key fallback).
/// </summary>
public class PlayitApiService
{
    private const string BaseUrl = "https://api.playit.gg";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string[] TomlPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "playit_gg", "playit.toml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "playit_gg", "playit.toml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "playit_gg", "playit.toml"),
    };

    public record PlayitTunnel(string Id, string Name, int LocalPort, string? AssignedDomain, string? CustomDomain)
    {
        public string? Address => string.IsNullOrEmpty(CustomDomain) ? AssignedDomain : CustomDomain;
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

    /// <summary>Write POST: tries Api-Key and, on an auth error, retries with Agent-Key.</summary>
    private async Task<JsonElement> PostWriteAsync(string path, string writeKey, string body, CancellationToken ct)
    {
        try
        {
            return await PostAsync(path, $"Api-Key {writeKey}", body, ct);
        }
        catch (PlayitApiException ex) when (ex.IsAuthError)
        {
            return await PostAsync(path, $"Agent-Key {writeKey}", body, ct);
        }
    }

    /// <summary>Reads agent_id and tunnels using a key (by default the agent's read-only one).</summary>
    public async Task<(string AgentId, List<PlayitTunnel> Tunnels)> GetRunDataAsync(string readAuthValue, CancellationToken ct = default)
    {
        var data = await PostAsync("/agents/rundata", readAuthValue, "", ct);
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
                list.Add(new PlayitTunnel(id, name, localPort, assigned, custom));
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

    private Task<List<PlayitTunnel>> StartTunnelFetch() => Task.Run(async () =>
    {
        var secret = ReadSecretKey();
        if (string.IsNullOrEmpty(secret)) return new List<PlayitTunnel>();
        var (_, tunnels) = await GetRunDataAsync($"agent-key {secret}", CancellationToken.None);
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

    /// <summary>Returns the authValue for reading (the agent key if present, otherwise the write key).</summary>
    private string? ReadAuth(string writeKey)
    {
        var secret = ReadSecretKey();
        if (!string.IsNullOrEmpty(secret)) return $"agent-key {secret}";
        return string.IsNullOrEmpty(writeKey) ? null : $"Api-Key {writeKey}";
    }

    /// <summary>
    /// Creates the server's Minecraft Java tunnel if one doesn't already exist for that port.
    /// Returns true if it created one, false if it already existed. Requires a write key.
    /// </summary>
    public async Task<bool> EnsureMinecraftTunnelAsync(string writeKey, string name, int localPort, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(writeKey))
            throw new InvalidOperationException(Localizer.Get("Msg_MissingWriteKey"));

        var readAuth = ReadAuth(writeKey)
            ?? throw new InvalidOperationException(Localizer.Get("Msg_PlayitAuthFail"));

        var (agentId, tunnels) = await GetRunDataAsync(readAuth, ct);
        if (tunnels.Any(t => t.LocalPort == localPort))
            return false;

        var body = new JsonObject
        {
            ["name"] = name,
            ["tunnel_type"] = "minecraft-java",
            ["port_type"] = "tcp",
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

        await PostWriteAsync("/tunnels/create", writeKey, body, ct);
        InvalidateTunnelCache(); // so the next address refresh sees the new tunnel right away
        return true;
    }

    /// <summary>Deletes the tunnel whose local port matches. Returns true if one was deleted. Requires a write key.</summary>
    public async Task<bool> DeleteTunnelForPortAsync(string writeKey, int localPort, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(writeKey))
            throw new InvalidOperationException(Localizer.Get("Msg_MissingWriteKey"));

        var readAuth = ReadAuth(writeKey)
            ?? throw new InvalidOperationException(Localizer.Get("Msg_PlayitAuthFail"));

        var (_, tunnels) = await GetRunDataAsync(readAuth, ct);
        var match = tunnels.FirstOrDefault(t => t.LocalPort == localPort);
        if (match is null || string.IsNullOrEmpty(match.Id)) return false;

        var body = new JsonObject { ["tunnel_id"] = match.Id }.ToJsonString();
        await PostWriteAsync("/tunnels/delete", writeKey, body, ct);
        InvalidateTunnelCache(); // so the next address refresh stops showing the deleted tunnel
        return true;
    }
}
