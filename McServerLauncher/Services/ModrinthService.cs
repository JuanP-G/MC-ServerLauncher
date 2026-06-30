using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
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

    public async Task<SearchResponse?> SearchModsAsync(string query, ServerType loader, string mcVersion, string index = "relevance", int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        var loaderStr = loader.ToString().ToLowerInvariant();
        var facets = $"[[\"categories:{loaderStr}\"],[\"versions:{mcVersion}\"],[\"project_type:mod\"],[\"server_side:required\",\"server_side:optional\"]]";
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
        var loaderStr = loader.ToString().ToLowerInvariant();
        var loadersJson = Uri.EscapeDataString($"[\"{loaderStr}\"]");
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

    public async Task DownloadModAsync(string downloadUrl, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using var fs = File.Create(destinationPath);
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);

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
}
