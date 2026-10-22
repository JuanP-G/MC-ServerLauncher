using System.IO;
using System.Text.Json;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>One mod or plugin the Performance page may suggest.</summary>
public sealed class PerformancePick
{
    /// <summary>The Modrinth project.</summary>
    public string Slug { get; set; } = "";

    /// <summary>The ids its jar answers to: how an installed copy is recognised without asking Modrinth.</summary>
    public List<string> ModIds { get; set; } = [];

    /// <summary>The <see cref="ServerType"/> names it runs on.</summary>
    public List<string> Loaders { get; set; } = [];

    /// <summary>The <see cref="PerformanceCause"/> names it relieves.</summary>
    public List<string> Helps { get; set; } = [];

    /// <summary>Slugs that must not be installed beside it.</summary>
    public List<string> Conflicts { get; set; } = [];

    /// <summary>The server already ships it: Paper carries spark from 1.21.</summary>
    public bool SkipWhenBundled { get; set; }
}

/// <summary>
/// The short list of mods and plugins that make a server faster without changing how the game plays,
/// from <c>Resources/performance-picks.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// A short, fixed list on purpose: the page promises that nothing it suggests changes the game, so
/// each entry was chosen by a person. The copy in the user's data folder
/// (<c>performance-picks.json</c>) replaces the embedded one when present, the way the store's tags
/// and summaries can be replaced, so the list can be fixed without a new build.
/// </para>
/// <para>
/// Whether a pick works for <em>this</em> server (a build for its loader and Minecraft version) is not
/// known here; the page asks Modrinth for that and hides what has none.
/// </para>
/// </remarks>
public sealed class PerformancePicks
{
    private const string EmbeddedResourceName = "McServerLauncher.Resources.performance-picks.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class Catalog
    {
        public List<PerformancePick> Picks { get; set; } = [];
    }

    /// <summary>The list as shipped (or as overridden), read once per run.</summary>
    public static PerformancePicks Shared { get; } = new();

    private readonly IReadOnlyList<PerformancePick> _picks;

    public PerformancePicks(string? overridePath = null)
    {
        var path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher",
            "performance-picks.json");
        _picks = Load(path);
    }

    /// <summary>Every entry.</summary>
    public IReadOnlyList<PerformancePick> All => _picks;

    private static IReadOnlyList<PerformancePick> Load(string overridePath)
    {
        try
        {
            if (File.Exists(overridePath)
                && JsonSerializer.Deserialize<Catalog>(File.ReadAllText(overridePath), JsonOptions) is { Picks.Count: > 0 } custom)
                return custom.Picks;
        }
        catch { /* a broken override must not cost the built-in list */ }

        try
        {
            using var stream = typeof(PerformancePicks).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is not null && JsonSerializer.Deserialize<Catalog>(stream, JsonOptions) is { } embedded)
                return embedded.Picks;
        }
        catch { /* no list simply means no suggestions */ }

        return [];
    }

    /// <summary>
    /// The picks worth offering a server: those for its loader that are not installed, that do not
    /// clash with something that is, and, with the cause that matters first.
    /// </summary>
    /// <param name="type">The server type.</param>
    /// <param name="cause">What is wrong now; the picks that relieve it come first.</param>
    /// <param name="installedModIds">Ids the installed jars answer to.</param>
    /// <param name="installedFiles">File names in the mods or plugins folder, for jars that carry no readable id.</param>
    /// <param name="sparkBundled">The server ships spark itself.</param>
    /// <param name="includeSpark">Offer spark: nothing else can measure the server.</param>
    public IReadOnlyList<PerformancePick> Suggest(ServerType type, PerformanceCause cause,
        IReadOnlySet<string> installedModIds, IEnumerable<string> installedFiles, bool sparkBundled,
        bool includeSpark)
    {
        var files = installedFiles.ToList();
        bool Installed(PerformancePick p) =>
            p.ModIds.Any(installedModIds.Contains)
            || files.Any(f => f.StartsWith(p.Slug, StringComparison.OrdinalIgnoreCase)
                              || p.ModIds.Any(id => f.StartsWith(id, StringComparison.OrdinalIgnoreCase)));

        var present = _picks.Where(Installed).Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _picks
            .Where(p => p.Loaders.Contains(type.ToString(), StringComparer.OrdinalIgnoreCase))
            .Where(p => !Installed(p))
            .Where(p => !p.Conflicts.Any(present.Contains))
            .Where(p => !(p.Slug == "spark" && (!includeSpark || sparkBundled)))
            .Where(p => !(p.SkipWhenBundled && sparkBundled))
            .OrderBy(p => p.Helps.Contains(cause.ToString(), StringComparer.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }
}
