using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace McServerLauncher.Models.Modrinth;

public class VersionResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = string.Empty;

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = new();

    [JsonPropertyName("files")]
    public List<VersionFile> Files { get; set; } = new();
}

public class VersionFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("hashes")]
    public FileHashes? Hashes { get; set; }
}

/// <summary>Official checksums Modrinth provides for a file, used to verify the download.</summary>
public class FileHashes
{
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; set; }
}
