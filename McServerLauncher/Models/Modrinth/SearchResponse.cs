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

/// <summary>
/// One search hit. Modrinth returns far more than a card needs, and every extra field here is one
/// request the details page doesn't have to make: the side (client/server), the categories, the
/// follower count and the dates are enough to classify and describe a project on their own.
/// </summary>
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

    /// <summary>"mod", "plugin", "shader"… — what kind of thing this is.</summary>
    [JsonPropertyName("project_type")]
    public string? ProjectType { get; set; }

    /// <summary>Raw category slugs. Includes the loaders (fabric, paper…), not only real categories.</summary>
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    /// <summary>Categories Modrinth itself considers worth showing (a subset of <see cref="Categories"/>).</summary>
    [JsonPropertyName("display_categories")]
    public List<string> DisplayCategories { get; set; } = new();

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    /// <summary>Followers. Named "follows" in search results and "followers" on the project endpoint.</summary>
    [JsonPropertyName("follows")]
    public int Follows { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    /// <summary>"required", "optional", "unsupported" or "unknown".</summary>
    [JsonPropertyName("client_side")]
    public string? ClientSide { get; set; }

    /// <summary>"required", "optional", "unsupported" or "unknown".</summary>
    [JsonPropertyName("server_side")]
    public string? ServerSide { get; set; }

    /// <summary>SPDX id of the licence (the project endpoint returns an object instead).</summary>
    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset? DateModified { get; set; }

    /// <summary>Minecraft versions the project supports.</summary>
    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = new();

    /// <summary>Gallery image URLs (plain strings here; the project endpoint returns objects).</summary>
    [JsonPropertyName("gallery")]
    public List<string> Gallery { get; set; } = new();
}
