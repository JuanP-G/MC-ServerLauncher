using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace McServerLauncher.Models.Modrinth;

public class VersionResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Human-readable version name, e.g. "6.0.8 for 1.20.1".</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = string.Empty;

    /// <summary>"release", "beta" or "alpha".</summary>
    [JsonPropertyName("version_type")]
    public string? VersionType { get; set; }

    [JsonPropertyName("date_published")]
    public DateTimeOffset? DatePublished { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = new();

    [JsonPropertyName("files")]
    public List<VersionFile> Files { get; set; } = new();

    /// <summary>Other projects this version needs (or conflicts with).</summary>
    [JsonPropertyName("dependencies")]
    public List<VersionDependency> Dependencies { get; set; } = new();
}

public class VersionFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    /// <summary>File size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

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

/// <summary>
/// A dependency declared by a version. <see cref="DependencyType"/> is "required", "optional",
/// "incompatible" or "embedded"; only the project id is guaranteed, so the project itself has to be
/// looked up to show a name.
/// </summary>
public class VersionDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("dependency_type")]
    public string? DependencyType { get; set; }
}
