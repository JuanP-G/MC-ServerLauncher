using System.IO;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>
/// Loads the Playit third-party PARTNER credentials: the app-wide <c>Api-Key</c> and
/// <c>variant_id</c> used ONLY to call <c>/v1/partner/create_agent</c> (which mints a per-user
/// agent secret key). These are never committed to source. They are read from environment
/// variables or a gitignored <c>PlayitPartner.local.json</c> shipped next to the executable. When
/// absent, the partner onboarding flow is disabled and the app falls back to the legacy
/// manual-key path.
/// </summary>
public static class PlayitPartnerConfig
{
    /// <summary>Returns (apiKey, variantId); either may be null when not configured.</summary>
    public static (string? ApiKey, string? VariantId) Load()
    {
        var apiKey = NullIfBlank(Environment.GetEnvironmentVariable("PLAYIT_PARTNER_API_KEY"));
        var variantId = NullIfBlank(Environment.GetEnvironmentVariable("PLAYIT_PARTNER_VARIANT_ID"));

        // The file fills in whichever env var wasn't provided.
        if (apiKey is null || variantId is null)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "PlayitPartner.local.json");
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    apiKey ??= root.TryGetProperty("apiKey", out var k) ? NullIfBlank(k.GetString()) : null;
                    variantId ??= root.TryGetProperty("variantId", out var v) ? NullIfBlank(v.GetString()) : null;
                }
            }
            catch
            {
                // Malformed/unreadable file: treated as not configured (legacy path is used).
            }
        }

        return (apiKey, variantId);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
