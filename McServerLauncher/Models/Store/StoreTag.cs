using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace McServerLauncher.Models.Store;

/// <summary>
/// The tag catalogue, loaded from <c>Resources/store-tags.json</c> (and optionally overridden in
/// the user's data folder). It is deliberately plain data: a new tag, a new keyword or a different
/// colour is a change to that file, never to this code.
/// </summary>
public class StoreTagCatalog
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("tags")]
    public List<StoreTagDefinition> Tags { get; set; } = new();
}

/// <summary>
/// One tag and the rules that give it to a project. Every rule list is optional; a tag with no
/// rules simply never matches, which is a harmless way to stage a tag before filling it in.
/// </summary>
public class StoreTagDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Ordering. Lower wins when a card only has room for a few tags.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;

    /// <summary>
    /// What the tag is about: "topic" (the default) is what the project does, "install" is how it
    /// has to be installed. The interface shows only topic tags as chips, because the install side
    /// is already spelled out in words next to the compatibility, and repeating it as a chip both
    /// crowds the row and pushes a real category out of it.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "topic";

    /// <summary>Chip colour as "#RRGGBB". Falls back to a neutral grey when absent or invalid.</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>Display name per language code ("es", "en", "de", "fr", "pt").</summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>Modrinth category slugs that imply this tag.</summary>
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    /// <summary>Lowercase substrings looked for in the title and the short description.</summary>
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    /// <summary>Matches the project's <c>client_side</c>: required / optional / unsupported.</summary>
    [JsonPropertyName("clientSide")]
    public List<string> ClientSide { get; set; } = new();

    /// <summary>Matches the project's <c>server_side</c>.</summary>
    [JsonPropertyName("serverSide")]
    public List<string> ServerSide { get; set; } = new();

    /// <summary>Whether the tag is offered as a filter chip in the browser.</summary>
    [JsonPropertyName("browse")]
    public bool Browse { get; set; }

    /// <summary>Ready-made Modrinth facet groups, e.g. <c>["categories:optimization"]</c>.</summary>
    [JsonPropertyName("facets")]
    public List<string> Facets { get; set; } = new();

    /// <summary>Search text used to browse this tag when Modrinth has no facet for it.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>Restricts the chip to "mod" or "plugin" servers. Empty means both.</summary>
    [JsonPropertyName("projectTypes")]
    public List<string> ProjectTypes { get; set; } = new();
}
