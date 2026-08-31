using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McServerLauncher.Services;

/// <summary>
/// GeyserMC's downloads API, which the app reads for Floodgate and for Hydraulic.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the same for every project they publish: <c>/{project}</c> lists versions,
/// <c>/versions/{version}</c> lists builds, and <c>/versions/{version}/builds/{build}</c> lists one
/// download per platform, each with its name and its SHA-256. It was walked in two places with two
/// implementations, and they had drifted: one took the newest build and gave up if it carried no
/// download for the platform, the other walked backwards until it found one.
/// </para>
/// <para>
/// Walking backwards is the correct behaviour and is now what both do. Builds are published per
/// commit and a given one may carry no artifact for a platform at all — taking the newest blindly
/// is how the NeoForge situation was first misread.
/// </para>
/// </remarks>
public static class GeyserDownloadsApi
{
    private const string Base = "https://download.geysermc.org/v2/projects";

    /// <summary>How far back to look before concluding a platform has no build.</summary>
    /// <remarks>
    /// Ten is generous for a gap between builds and small enough that a project which genuinely
    /// stopped publishing for a platform is reported as such in ten requests rather than hundreds.
    /// </remarks>
    private const int BuildsToTry = 10;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static GeyserDownloadsApi()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "JuanP-G/MC-ServerLauncher");
    }

    /// <summary>One published artifact: which build it came from, and how to verify it.</summary>
    public record Artifact(string Version, int Build, string FileName, string Url, string Sha256);

    /// <summary>
    /// The newest build of <paramref name="project"/> that publishes a download for
    /// <paramref name="platform"/>, or null when none of the recent ones does.
    /// </summary>
    /// <remarks>
    /// A download with no SHA-256 is skipped rather than returned: the app's rule everywhere is that
    /// nothing is installed without its checksum, and returning one here would push that decision
    /// out to callers who would each have to remember it.
    /// </remarks>
    public static async Task<Artifact?> LatestAsync(string project, string platform, CancellationToken ct = default)
    {
        var versionsJson = await Http.GetStringAsync($"{Base}/{project}", ct);
        using var versions = JsonDocument.Parse(versionsJson);
        var version = versions.RootElement.GetProperty("versions").EnumerateArray().Last().GetString();
        if (version is null) return null;

        var buildsJson = await Http.GetStringAsync($"{Base}/{project}/versions/{version}", ct);
        using var buildsDoc = JsonDocument.Parse(buildsJson);
        var builds = buildsDoc.RootElement.GetProperty("builds").EnumerateArray()
            .Select(b => b.GetInt32())
            .OrderByDescending(b => b)
            .Take(BuildsToTry);

        foreach (var build in builds)
        {
            ct.ThrowIfCancellationRequested();

            var buildJson = await Http.GetStringAsync($"{Base}/{project}/versions/{version}/builds/{build}", ct);
            using var doc = JsonDocument.Parse(buildJson);

            if (!doc.RootElement.GetProperty("downloads").TryGetProperty(platform, out var download)) continue;

            var name = download.TryGetProperty("name", out var n) ? n.GetString() : null;
            var sha256 = download.TryGetProperty("sha256", out var h) ? h.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(sha256)) continue;

            return new Artifact(version, build, name!,
                $"{Base}/{project}/versions/{version}/builds/{build}/downloads/{platform}", sha256!);
        }

        return null;
    }

    /// <summary>Opens the artifact's download stream. The caller verifies and stores it.</summary>
    public static Task<HttpResponseMessage> OpenAsync(Artifact artifact, CancellationToken ct = default) =>
        Http.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, ct);
}
