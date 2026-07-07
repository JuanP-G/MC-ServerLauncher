using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Result of <c>/v1/partner/create_agent</c>: the per-user self-managed agent minted for the user
/// from their setup code. <see cref="AgentSecretKey"/> is what the app stores (encrypted) and uses
/// as the <c>agent-key</c> for all subsequent tunnel management.
/// </summary>
public record CreateAgentResult(long AccountId, string AgentId, string AgentSecretKey, bool AgentOverLimit);

/// <summary>
/// Third-party "Integration"-tier onboarding: exchanges a Playit <c>account_setup_code</c> (that the
/// user obtained from playit.gg) for a per-user self-managed agent secret key. The request goes
/// through a small proxy (a Cloudflare Worker, see <c>playit-proxy/</c>) that injects the partner
/// Api-Key server-side — so NO secret ships inside this (public, open-source) app. Everything after
/// uses the returned per-user secret via <see cref="PlayitApiService"/>.
/// </summary>
public class PlayitPartnerService
{
    // The proxy that adds the partner Api-Key and forwards to Playit. Public (not a secret): it can
    // only reach create_agent, which still needs a user-authorized setup code. Override in dev with
    // the PLAYIT_PROXY_URL environment variable.
    private const string DefaultProxyUrl = "https://dawn-hall-c5a8.gustofparaps4.workers.dev";
    private const string AgentName = "MC Server Launcher";

    // Public values from Playit's open-source agent (Playit told us to use their current release's
    // variant). variant_id = DEFAULT_VARIANT_ID in playitd's daemon.rs; version = the agent's
    // workspace version. The API requires the (variant_id, version) pair to be a registered one, so
    // we send the AGENT's version, not this app's.
    private const string VariantId = "308943e8-faef-4835-a2ba-270351f72aa3";
    private const int AgentVersionMajor = 1;
    private const int AgentVersionMinor = 0;
    private const int AgentVersionPatch = 10;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public PlayitPartnerService() : this(null, null) { }

    /// <summary>Test/overridable constructor. Null baseUrl uses the proxy (or PLAYIT_PROXY_URL).</summary>
    public PlayitPartnerService(string? baseUrl, HttpClient? http)
    {
        _baseUrl = (baseUrl
            ?? Environment.GetEnvironmentVariable("PLAYIT_PROXY_URL")
            ?? DefaultProxyUrl).TrimEnd('/');
        _http = http ?? SharedHttp;
    }

    /// <summary>Always available: the proxy URL is baked in and there is no per-app secret to set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl);

    /// <summary>
    /// Exchanges the user's setup code for their own self-managed agent secret key (via the proxy).
    /// Throws <see cref="InvalidOperationException"/> (with a localized message) on network/API
    /// failure or an invalid/expired code.
    /// </summary>
    public async Task<CreateAgentResult> CreateAgentAsync(string setupCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(setupCode))
            throw new InvalidOperationException(Localizer.Get("Msg_PasteSetupCode"));

        var body = new JsonObject
        {
            ["agent_name"] = AgentName,
            ["self_managed"] = true,
            ["agent_details"] = new JsonObject
            {
                ["variant_id"] = VariantId,
                ["version_major"] = AgentVersionMajor,
                ["version_minor"] = AgentVersionMinor,
                ["version_patch"] = AgentVersionPatch
            },
            ["platform"] = CurrentPlatform,
            ["account_setup_code"] = setupCode.Trim()
        }.ToJsonString();

        // No Authorization header — the proxy injects the partner Api-Key server-side.
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/partner/create_agent");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        string json;
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = ExtractError(json);
                throw new InvalidOperationException(string.Format(Localizer.Get("Msg_SetupCodeFailedFmt"),
                    err is null ? $"HTTP {(int)resp.StatusCode}" : DescribeFail(err)));
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(string.Format(Localizer.Get("Msg_SetupCodeFailedFmt"), ex.Message));
        }

        return Parse(json);
    }

    /// <summary>Platform token expected by the API (matches the doc's example: "windows").</summary>
    private static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static CreateAgentResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = Unwrap(doc.RootElement);

        // Be lenient about numeric vs string ids across API versions.
        var accountId = data.TryGetProperty("account_id", out var a) && a.TryGetInt64(out var id) ? id : 0L;
        var agentId = data.TryGetProperty("agent_id", out var ag) ? ag.GetString() ?? "" : "";
        var secret = data.TryGetProperty("agent_secret_key", out var s) ? s.GetString() ?? "" : "";
        var overLimit = data.TryGetProperty("agent_over_limit", out var o) && o.ValueKind == JsonValueKind.True;

        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(agentId))
            throw new InvalidOperationException(string.Format(
                Localizer.Get("Msg_SetupCodeFailedFmt"), Localizer.Get("Msg_SetupCodeNoAgent")));

        return new CreateAgentResult(accountId, agentId, secret, overLimit);
    }

    /// <summary>Handles both a bare result object and the ApiResult envelope { status, data }.</summary>
    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.TryGetProperty("status", out var status) && root.TryGetProperty("data", out var data))
        {
            if (status.GetString() != "success")
                throw new InvalidOperationException(string.Format(Localizer.Get("Msg_SetupCodeFailedFmt"),
                    DescribeFail(ExtractError(data.GetRawText()) ?? status.GetString() ?? "error")));
            return data;
        }
        return root;
    }

    /// <summary>
    /// Maps known Playit "fail" codes to a clearer message; unknown codes pass through so they're
    /// still visible. <c>AgentVariantVersionNotFound</c> means the app's variant_id/version isn't
    /// registered on Playit's side yet (the app is still waiting on its variant_id).
    /// </summary>
    private static string DescribeFail(string raw) => raw switch
    {
        "AgentVariantVersionNotFound" => Localizer.Get("Msg_PartnerVariantNotFound"),
        "SetupCodeNotFound" or "AccountSetupCodeNotFound" => Localizer.Get("Msg_SetupCodeInvalid"),
        _ => raw
    };

    /// <summary>Best-effort human-readable error from an API error body.</summary>
    private static string? ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg) return msg;
            if (root.TryGetProperty("data", out var d))
            {
                if (d.ValueKind == JsonValueKind.String) return d.GetString();
                if (d.TryGetProperty("message", out var dm) && dm.GetString() is { Length: > 0 } dmsg) return dmsg;
                if (d.TryGetProperty("type", out var t) && t.GetString() is { Length: > 0 } type) return type;
            }
        }
        catch { /* not JSON: fall through */ }
        return null;
    }
}
