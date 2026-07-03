using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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

    public async Task<SearchResponse?> SearchModsAsync(string query, ServerType loader, string mcVersion, string index = "relevance", int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        var (categoriesGroup, projectType, _) = TargetFor(loader);
        var facets = $"[{categoriesGroup},[\"versions:{mcVersion}\"],[\"project_type:{projectType}\"],[\"server_side:required\",\"server_side:optional\"]]";
        var escapedFacets = Uri.EscapeDataString(facets);
        var escapedQuery = Uri.EscapeDataString(query);
        var escapedIndex = Uri.EscapeDataString(index);

        var url = $"{ApiBaseUrl}/search?query={escapedQuery}&facets={escapedFacets}&index={escapedIndex}&offset={offset}&limit={limit}";

        try
        {
            var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<VersionResult?> GetLatestProjectVersionAsync(string projectId, ServerType loader, string mcVersion, CancellationToken ct = default)
    {
        var (_, _, loaders) = TargetFor(loader);
        var loadersJson = Uri.EscapeDataString(loaders);
        var gameVersionsJson = Uri.EscapeDataString($"[\"{mcVersion}\"]");

        var url = $"{ApiBaseUrl}/project/{projectId}/version?loaders={loadersJson}&game_versions={gameVersionsJson}";

        try
        {
            var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var versions = await response.Content.ReadFromJsonAsync<List<VersionResult>>(cancellationToken: ct);
            
            if (versions != null && versions.Count > 0)
            {
                return versions[0]; // The API returns them sorted by newest first
            }
        }
        catch
        {
            // Ignore errors and return null
        }
        return null;
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

        var totalBytes = response.Content.Headers.ContentLength;

        // The write handle must be closed before verifying: File.Create opens with FileShare.None,
        // so DownloadVerifier's read would otherwise fail with a sharing violation on Windows.
        await using (var fs = File.Create(destinationPath))
        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[8192];
            var isMoreToRead = true;
            var totalRead = 0L;

            do
            {
                var read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0)
                {
                    isMoreToRead = false;
                }
                else
                {
                    await fs.WriteAsync(buffer, 0, read, ct);
                    totalRead += read;

                    if (totalBytes.HasValue && progress != null)
                    {
                        progress.Report((double)totalRead / totalBytes.Value);
                    }
                }
            } while (isMoreToRead);
        }

        if (!string.IsNullOrEmpty(expectedSha512))
            await DownloadVerifier.VerifyAsync(destinationPath, expectedSha512, HashAlgorithmName.SHA512, ct);
        else if (!string.IsNullOrEmpty(expectedSha1))
            await DownloadVerifier.VerifyAsync(destinationPath, expectedSha1, HashAlgorithmName.SHA1, ct);
    }
}
