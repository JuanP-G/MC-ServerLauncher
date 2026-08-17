using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;

namespace McServerLauncher.Services;

public class ModrinthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const string ApiBaseUrl = "https://api.modrinth.com/v2";

    static ModrinthService()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "JuanP-G/MC-ServerLauncher");
    }

    // --- How long each kind of answer stays usable ---
    // A project's description and links change rarely; its version list changes when the author
    // publishes; a team never really changes. Everything also survives on disk past these windows
    // as an offline fallback (see StoreCache).

    private static readonly TimeSpan ProjectTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan VersionsTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan TeamTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maps a server type to the Modrinth search target: the categories facet group, the project
    /// type (mod vs plugin) and the loaders array for version resolution. Paper searches plugins
    /// across the bukkit-family loaders; the mod loaders search their own category.
    /// </summary>
    private static (string CategoriesGroup, string ProjectType, string LoadersJson) TargetFor(ServerType type)
    {
        if (type == ServerType.Paper)
        {
            var loaders = new[] { "paper", "spigot", "bukkit", "purpur", "folia" };
            var cats = string.Join(",", loaders.Select(l => $"\"categories:{l}\""));
            var arr = string.Join(",", loaders.Select(l => $"\"{l}\""));
            return ($"[{cats}]", "plugin", $"[{arr}]");
        }
        var one = type.ToString().ToLowerInvariant();
        return ($"[\"categories:{one}\"]", "mod", $"[\"{one}\"]");
    }

    /// <summary>
    /// Searches Modrinth for content compatible with a server. <paramref name="extraFacetGroups"/>
    /// adds already-formatted facet groups (e.g. <c>["categories:optimization"]</c>) that are ANDed
    /// with the loader/version/type filter — this is what the category chips use.
    /// </summary>
    public async Task<SearchResponse?> SearchModsAsync(string query, ServerType loader, string mcVersion,
        string index = "relevance", int offset = 0, int limit = 20,
        IReadOnlyCollection<string>? extraFacetGroups = null, CancellationToken ct = default)
    {
        var (categoriesGroup, projectType, _) = TargetFor(loader);
        var groups = new List<string>
        {
            categoriesGroup,
            $"[\"versions:{mcVersion}\"]",
            $"[\"project_type:{projectType}\"]",
            "[\"server_side:required\",\"server_side:optional\"]"
        };
        if (extraFacetGroups is { Count: > 0 }) groups.AddRange(extraFacetGroups);

        var facets = $"[{string.Join(",", groups)}]";
        var url = $"{ApiBaseUrl}/search?query={Uri.EscapeDataString(query)}" +
                  $"&facets={Uri.EscapeDataString(facets)}" +
                  $"&index={Uri.EscapeDataString(index)}&offset={offset}&limit={limit}";

        // Only cache the unsearched, first-page browsing (and the related-mods queries, which use
        // an empty query too). A typed search should feel live rather than replay an old answer.
        if (string.IsNullOrWhiteSpace(query))
            return await StoreCache.Shared.GetOrFetchAsync($"search:{url}", SearchTtl,
                token => GetJsonAsync<SearchResponse>(url, token), ct);

        return await GetJsonAsync<SearchResponse>(url, ct);
    }

    /// <summary>Full project: long description, gallery, links and licence.</summary>
    public Task<ProjectDetail?> GetProjectAsync(string idOrSlug, CancellationToken ct = default) =>
        StoreCache.Shared.GetOrFetchAsync($"project:{idOrSlug}", ProjectTtl,
            token => GetJsonAsync<ProjectDetail>($"{ApiBaseUrl}/project/{Uri.EscapeDataString(idOrSlug)}", token), ct);

    /// <summary>
    /// Every published version of a project, newest first. When <paramref name="mcVersion"/> is
    /// given the list is narrowed to what runs on this server's loader and Minecraft version — the
    /// same filter the installer uses, so what the details page offers is what actually installs.
    /// </summary>
    public Task<List<VersionResult>?> GetProjectVersionsAsync(string projectId, ServerType loader,
        string? mcVersion = null, CancellationToken ct = default)
    {
        var (_, _, loaders) = TargetFor(loader);
        var url = $"{ApiBaseUrl}/project/{Uri.EscapeDataString(projectId)}/version?loaders={Uri.EscapeDataString(loaders)}";
        if (!string.IsNullOrEmpty(mcVersion))
            url += $"&game_versions={Uri.EscapeDataString($"[\"{mcVersion}\"]")}";

        return StoreCache.Shared.GetOrFetchAsync($"versions:{url}", VersionsTtl,
            token => GetJsonAsync<List<VersionResult>>(url, token), ct);
    }

    /// <summary>
    /// Several projects in a single request. Used for dependencies and related mods, so opening a
    /// mod with five dependencies costs one request rather than five.
    /// </summary>
    public async Task<List<ProjectDetail>?> GetProjectsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        // Sorted + deduplicated so two callers asking for the same set share one cache entry.
        var list = ids.Where(i => !string.IsNullOrWhiteSpace(i))
                      .Distinct(StringComparer.Ordinal)
                      .OrderBy(i => i, StringComparer.Ordinal)
                      .ToList();
        if (list.Count == 0) return new List<ProjectDetail>();

        var idsJson = "[" + string.Join(",", list.Select(i => JsonSerializer.Serialize(i))) + "]";
        var url = $"{ApiBaseUrl}/projects?ids={Uri.EscapeDataString(idsJson)}";

        return await StoreCache.Shared.GetOrFetchAsync($"projects:{url}", ProjectTtl,
            token => GetJsonAsync<List<ProjectDetail>>(url, token), ct);
    }

    /// <summary>The project's team, which is where the author names live.</summary>
    public Task<List<TeamMember>?> GetTeamMembersAsync(string teamId, CancellationToken ct = default) =>
        StoreCache.Shared.GetOrFetchAsync($"team:{teamId}", TeamTtl,
            token => GetJsonAsync<List<TeamMember>>($"{ApiBaseUrl}/team/{Uri.EscapeDataString(teamId)}/members", token), ct);

    public async Task<VersionResult?> GetLatestProjectVersionAsync(string projectId, ServerType loader, string mcVersion, CancellationToken ct = default)
    {
        // The API returns them sorted by newest first.
        var versions = await GetProjectVersionsAsync(projectId, loader, mcVersion, ct);
        return versions is { Count: > 0 } ? versions[0] : null;
    }

    /// <summary>Shared GET + JSON deserialisation. Returns null on any failure, including offline.</summary>
    private static async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Given the SHA-1 of each installed jar, asks Modrinth (in a single request) for the latest
    /// version of each corresponding project that is compatible with this server's loader and Minecraft
    /// version. Returns a map keyed by the SAME input hash the caller passed. Hashes that Modrinth
    /// doesn't recognise (jars from CurseForge or built by hand) are simply absent from the result.
    /// </summary>
    public async Task<Dictionary<string, VersionResult>> GetLatestVersionsByHashAsync(
        IEnumerable<string> sha1Hashes, ServerType loader, string mcVersion, CancellationToken ct = default)
    {
        var hashes = sha1Hashes.Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, VersionResult>(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count == 0) return result;

        var (_, _, loadersJson) = TargetFor(loader);
        var body = new JsonObject
        {
            ["hashes"] = new JsonArray(hashes.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
            ["algorithm"] = "sha1",
            ["loaders"] = JsonNode.Parse(loadersJson),
            ["game_versions"] = new JsonArray(JsonValue.Create(mcVersion)!)
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/version_files/update");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();

            var map = await response.Content.ReadFromJsonAsync<Dictionary<string, VersionResult>>(cancellationToken: ct);
            if (map != null)
                foreach (var kv in map)
                    result[kv.Key] = kv.Value;
        }
        catch
        {
            // Offline or API error: report no updates rather than failing.
        }
        return result;
    }

    /// <summary>
    /// Downloads a mod/plugin file from Modrinth. Mods are third-party jars chosen by the user, so
    /// whenever Modrinth provides a hash for the file (it always does), the download is verified
    /// against it; a mismatch deletes the file and throws instead of installing it. Sha512 is
    /// preferred (stronger); Sha1 is used only if Modrinth didn't provide a Sha512 for this file.
    /// </summary>
    public async Task DownloadModAsync(string downloadUrl, string destinationPath, string? expectedSha512 = null,
        string? expectedSha1 = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Atomic: reinstalling or updating a mod that is already there must not destroy the working
        // jar if the download fails halfway.
        await AtomicDownload.ToFileAsync(response.Content, destinationPath,
            verifyAsync: async (part, token) =>
            {
                if (!string.IsNullOrEmpty(expectedSha512))
                    await DownloadVerifier.VerifyAsync(part, expectedSha512, HashAlgorithmName.SHA512, token);
                else if (!string.IsNullOrEmpty(expectedSha1))
                    await DownloadVerifier.VerifyAsync(part, expectedSha1, HashAlgorithmName.SHA1, token);
            },
            progress: progress,
            ct: ct);
    }
}
