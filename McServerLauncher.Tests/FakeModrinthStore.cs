using System.Security.Cryptography;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A <see cref="ModrinthService"/> that answers from memory and "downloads" by writing text, so the
/// installer, the updater and the dependency installer can be run without the network.
/// </summary>
internal sealed class FakeModrinthStore : ModrinthService
{
    /// <summary>What <see cref="GetLatestProjectVersionAsync"/> answers; null means "nothing compatible".</summary>
    public VersionResult? Latest { get; set; }

    /// <summary>What <see cref="GetVersionsByHashAsync"/> knows, by SHA-1.</summary>
    public Dictionary<string, VersionResult> ByHash { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="GetLatestVersionsByHashAsync"/> answers; null means "could not ask".</summary>
    public Dictionary<string, VersionResult>? LatestByHash { get; set; }

    /// <summary>Thrown by the next download, if set.</summary>
    public Exception? DownloadFails { get; set; }

    public int LatestCalls { get; private set; }

    /// <summary>Answers a search; receives (query, offset, facets). Null means "could not ask".</summary>
    public Func<string, int, IReadOnlyCollection<string>?, Task<SearchResponse?>>? OnSearch { get; set; }

    public override Task<SearchResponse?> SearchModsAsync(string query, ServerType loader, string mcVersion,
        string index = "relevance", int offset = 0, int limit = 20,
        IReadOnlyCollection<string>? extraFacetGroups = null, StoreSideFilter side = StoreSideFilter.All,
        CancellationToken ct = default) =>
        OnSearch?.Invoke(query, offset, extraFacetGroups) ?? Task.FromResult<SearchResponse?>(null);

    public List<string> Downloaded { get; } = new();

    public override Task<VersionResult?> GetLatestProjectVersionAsync(string projectId, ServerType loader,
        string mcVersion, CancellationToken ct = default)
    {
        LatestCalls++;
        return Task.FromResult(Latest);
    }

    public override Task<Dictionary<string, VersionResult>> GetVersionsByHashAsync(
        IEnumerable<string> sha1Hashes, CancellationToken ct = default) =>
        Task.FromResult(sha1Hashes.Where(ByHash.ContainsKey)
            .ToDictionary(h => h, h => ByHash[h], StringComparer.OrdinalIgnoreCase));

    public override Task<Dictionary<string, VersionResult>?> GetLatestVersionsByHashAsync(
        IEnumerable<string> sha1Hashes, ServerType loader, string mcVersion, CancellationToken ct = default) =>
        Task.FromResult(LatestByHash);

    public override Task DownloadModAsync(string downloadUrl, string destinationPath, string? expectedSha512 = null,
        string? expectedSha1 = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (DownloadFails is { } failure) throw failure;

        Downloaded.Add(downloadUrl);
        File.WriteAllText(destinationPath, "downloaded from " + downloadUrl);
        return Task.CompletedTask;
    }

    public static string Sha1Of(string path) => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path)))
        .ToLowerInvariant();

    /// <summary>A version of <paramref name="projectId"/> that is one file, the main one.</summary>
    public static VersionResult Version(string projectId, string fileName, string sha1 = "0000", string number = "2.0.0") => new()
    {
        Id = projectId + "-" + number,
        ProjectId = projectId,
        VersionNumber = number,
        Files = new List<VersionFile>
        {
            new()
            {
                Url = "https://cdn.example.invalid/" + fileName,
                Filename = fileName,
                Primary = true,
                Hashes = new FileHashes { Sha1 = sha1, Sha512 = "5" },
            },
        },
    };
}
