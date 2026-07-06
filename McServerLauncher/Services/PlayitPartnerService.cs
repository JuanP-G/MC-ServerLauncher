using System.Net.Http;
using System.Reflection;
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
/// Third-party "Integration"-tier onboarding (see docs/architecture): exchanges a Playit
/// <c>account_setup_code</c> (that the user obtained from playit.gg) for a per-user self-managed
/// agent secret key, using the app's partner Api-Key + variant_id. This is the only call that uses
/// the partner key; everything after uses the returned per-user secret via
/// <see cref="PlayitApiService"/>.
/// </summary>
public class PlayitPartnerService
{
    private const string DefaultBaseUrl = "https://api.playit.gg";
    private const string AgentName = "MC Server Launcher";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string? _apiKey;
    private readonly string? _variantId;
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    /// <summary>Production constructor: partner credentials come from <see cref="PlayitPartnerConfig"/>.</summary>
    public PlayitPartnerService() : this(null, null, null, null) { }

    /// <summary>Test/overridable constructor. Null apiKey/variantId falls back to the config loader.</summary>
    public PlayitPartnerService(string? apiKey, string? variantId, string? baseUrl, HttpClient? http)
    {
        if (apiKey is null && variantId is null)
            (apiKey, variantId) = PlayitPartnerConfig.Load();
        _apiKey = apiKey;
        _variantId = variantId;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = http ?? SharedHttp;
    }

    /// <summary>True when the app has partner credentials, i.e. the setup-code flow is available.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_variantId);

    /// <summary>
    /// Exchanges the user's setup code for their own self-managed agent secret key.
    /// Throws <see cref="InvalidOperationException"/> (with a localized message) if the app isn't
    /// configured, the network/API fails, or the code is invalid/expired.
    /// </summary>
    public async Task<CreateAgentResult> CreateAgentAsync(string setupCode, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Localizer.Get("Msg_PartnerNotConfigured"));
        if (string.IsNullOrWhiteSpace(setupCode))
            throw new InvalidOperationException(Localizer.Get("Msg_PasteSetupCode"));

        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        var body = new JsonObject
        {
            ["agent_name"] = AgentName,
            ["self_managed"] = true,
            ["agent_details"] = new JsonObject
            {
                ["variant_id"] = _variantId,
                ["version_major"] = v.Major,
                ["version_minor"] = Math.Max(0, v.Minor),
                ["version_patch"] = Math.Max(0, v.Build)
            },
            ["platform"] = CurrentPlatform,
            ["account_setup_code"] = setupCode.Trim()
        }.ToJsonString();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/partner/create_agent");
        req.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {_apiKey}");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        string json;
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(string.Format(
                    Localizer.Get("Msg_SetupCodeFailedFmt"), ExtractError(json) ?? $"HTTP {(int)resp.StatusCode}"));
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
                throw new InvalidOperationException(string.Format(
                    Localizer.Get("Msg_SetupCodeFailedFmt"), ExtractError(data.GetRawText()) ?? status.GetString() ?? "error"));
            return data;
        }
        return root;
    }

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
