using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace McServerLauncher.Models.Modrinth;

public class SearchResponse
{
    [JsonPropertyName("hits")]
    public List<ProjectResult> Hits { get; set; } = new();

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("total_hits")]
    public int TotalHits { get; set; }
}

public class ProjectResult
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}
