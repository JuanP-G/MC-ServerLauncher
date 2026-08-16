using System.Collections.Generic;
using System.Linq;
using McServerLauncher.Models.Modrinth;

namespace McServerLauncher.Models.Store;

/// <summary>
/// What the store knows about one thing it can offer, independently of where it came from.
/// <para>
/// Today everything is a Modrinth mod or plugin, and the two Modrinth shapes (a search hit and a
/// full project) carry the same facts under slightly different names. Tagging and summarising work
/// on this type instead of on either of them, so a shader, a resource pack or a modpack — or a
/// second source altogether — only needs its own conversion, not its own classifier.
/// </para>
/// </summary>
public class StoreItem
{
    public string Id { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>The author's own one-line summary.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Author name when the source knows it. Search results carry it; the project endpoint doesn't
    /// (it returns a team id instead), so an empty value means "look it up", not "no author".
    /// </summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>"mod", "plugin"… Empty when the source didn't say.</summary>
    public string ProjectType { get; init; } = string.Empty;

    public string? IconUrl { get; init; }

    public long Downloads { get; init; }

    public int Followers { get; init; }

    /// <summary>Raw category slugs, loaders included (that is how Modrinth stores them).</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>"required", "optional", "unsupported" or "unknown" — null when unknown.</summary>
    public string? ClientSide { get; init; }

    /// <summary>"required", "optional", "unsupported" or "unknown" — null when unknown.</summary>
    public string? ServerSide { get; init; }

    public DateTimeOffset? Updated { get; init; }

    /// <summary>True when players must install it too, not only the server.</summary>
    public bool NeedsClient =>
        string.Equals(ClientSide, "required", StringComparison.OrdinalIgnoreCase);

    public static StoreItem From(ProjectResult hit) => new()
    {
        Id = hit.ProjectId,
        Slug = hit.Slug,
        Title = hit.Title,
        Description = hit.Description,
        Author = hit.Author,
        ProjectType = hit.ProjectType ?? string.Empty,
        IconUrl = hit.IconUrl,
        Downloads = hit.Downloads,
        Followers = hit.Follows,
        Categories = hit.Categories,
        ClientSide = hit.ClientSide,
        ServerSide = hit.ServerSide,
        Updated = hit.DateModified
    };

    public static StoreItem From(ProjectDetail project) => new()
    {
        Id = project.Id,
        Slug = project.Slug,
        Title = project.Title,
        Description = project.Description,
        ProjectType = project.ProjectType ?? string.Empty,
        IconUrl = project.IconUrl,
        Downloads = project.Downloads,
        Followers = project.Followers,
        // The detail endpoint splits the categories in two; the classifier wants them together.
        Categories = project.Categories.Concat(project.AdditionalCategories).ToList(),
        ClientSide = project.ClientSide,
        ServerSide = project.ServerSide,
        Updated = project.Updated
    };
}
