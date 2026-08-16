using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace McServerLauncher.Models.Modrinth;

/// <summary>
/// Full project as returned by <c>GET /v2/project/{id|slug}</c>. It is the search hit plus
/// everything the details page shows: the long description, the gallery, the external links and
/// the licence. Every field is optional on purpose — Modrinth omits what an author never filled
/// in, and the UI must simply not show it rather than invent a value.
/// </summary>
public class ProjectDetail
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>One-line summary written by the author.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Long description, in Markdown (often with raw HTML mixed in).</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("project_type")]
    public string? ProjectType { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("additional_categories")]
    public List<string> AdditionalCategories { get; set; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = new();

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    /// <summary>Followers. The search endpoint calls the same number "follows".</summary>
    [JsonPropertyName("followers")]
    public int Followers { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("client_side")]
    public string? ClientSide { get; set; }

    [JsonPropertyName("server_side")]
    public string? ServerSide { get; set; }

    [JsonPropertyName("published")]
    public DateTimeOffset? Published { get; set; }

    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; set; }

    [JsonPropertyName("license")]
    public ProjectLicense? License { get; set; }

    [JsonPropertyName("issues_url")]
    public string? IssuesUrl { get; set; }

    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("wiki_url")]
    public string? WikiUrl { get; set; }

    [JsonPropertyName("discord_url")]
    public string? DiscordUrl { get; set; }

    [JsonPropertyName("donation_urls")]
    public List<DonationUrl> DonationUrls { get; set; } = new();

    [JsonPropertyName("gallery")]
    public List<GalleryImage> Gallery { get; set; } = new();

    /// <summary>Team id, used to look up the authors (the project endpoint has no author name).</summary>
    [JsonPropertyName("team")]
    public string? Team { get; set; }
}

public class ProjectLicense
{
    /// <summary>SPDX id, e.g. "MIT". Modrinth uses "LicenseRef-…" for non-SPDX licences.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Human-readable name. Often empty, in which case <see cref="Id"/> is what to show.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class DonationUrl
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// A gallery image. <see cref="Url"/> is a thumbnail Modrinth has already resized, which is what
/// the app shows; <see cref="RawUrl"/> is the full-size original.
/// </summary>
public class GalleryImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("raw_url")]
    public string? RawUrl { get; set; }

    [JsonPropertyName("featured")]
    public bool Featured { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("ordering")]
    public int Ordering { get; set; }
}

/// <summary>One member of a project's team (<c>GET /v2/team/{id}/members</c>).</summary>
public class TeamMember
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("user")]
    public TeamUser? User { get; set; }
}

public class TeamUser
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Display name; frequently null, in which case <see cref="Username"/> is used.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}
