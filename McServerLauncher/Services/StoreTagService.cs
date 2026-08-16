using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using McServerLauncher.Models.Store;

namespace McServerLauncher.Services;

/// <summary>
/// Turns a store item into the app's own tags.
/// <para>
/// Modrinth's categories are a good start but they are coarse — nearly half of the top server mods
/// are filed under "utility" — and they don't say the one thing a server owner cares most about:
/// whether players have to install the mod as well. So the categories are combined with keyword
/// rules and with the client/server side, all of which live in <c>Resources/store-tags.json</c>.
/// </para>
/// <para>
/// The catalogue is read from the embedded copy, unless
/// <c>%APPDATA%\McServerLauncher\store-tags.json</c> exists, in which case that file is used
/// instead. Tags can therefore be added, translated, recoloured or re-mapped without a new build.
/// </para>
/// </summary>
public sealed class StoreTagService
{
    /// <summary>Shared instance; the catalogue is read once per run.</summary>
    public static StoreTagService Shared { get; } = new();

    private const string EmbeddedResourceName = "McServerLauncher.Resources.store-tags.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<StoreTagDefinition> _tags;

    /// <summary>Every tag in the catalogue, ordered by priority.</summary>
    public IReadOnlyList<StoreTagDefinition> All => _tags;

    public StoreTagService(string? overridePath = null)
    {
        var path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McServerLauncher", "store-tags.json");

        _tags = Load(path)
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .OrderBy(t => t.Priority)
            .ToList();
    }

    private static List<StoreTagDefinition> Load(string overridePath)
    {
        // A user-supplied catalogue wins, but a broken one must not leave the app with no tags at
        // all — fall back to the copy that shipped with the binary.
        try
        {
            if (File.Exists(overridePath))
            {
                var custom = JsonSerializer.Deserialize<StoreTagCatalog>(File.ReadAllText(overridePath), JsonOptions);
                if (custom is { Tags.Count: > 0 }) return custom.Tags;
            }
        }
        catch { /* fall through to the embedded catalogue */ }

        try
        {
            using var stream = typeof(StoreTagService).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is not null)
            {
                var embedded = JsonSerializer.Deserialize<StoreTagCatalog>(stream, JsonOptions);
                if (embedded is not null) return embedded.Tags;
            }
        }
        catch { /* an unusable catalogue means no tags, never a crash */ }

        return new List<StoreTagDefinition>();
    }

    /// <summary>
    /// The tags that apply to <paramref name="item"/>, most important first. A tag applies when any
    /// of its rules matches; a project with no matching rule simply has no tags, which the UI shows
    /// by leaving the row out.
    /// </summary>
    public IReadOnlyList<StoreTagDefinition> Classify(StoreItem item, int max = int.MaxValue,
        string? onlyKind = null)
    {
        if (max <= 0) return Array.Empty<StoreTagDefinition>();

        var categories = new HashSet<string>(item.Categories, StringComparer.OrdinalIgnoreCase);
        var haystack = Normalize(item.Title + " " + item.Description);

        var matched = new List<StoreTagDefinition>();
        foreach (var tag in _tags)
        {
            if (onlyKind is not null && !string.Equals(tag.Kind, onlyKind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Matches(tag, categories, haystack, item))
            {
                matched.Add(tag);
                if (matched.Count == max) break;
            }
        }
        return matched;
    }

    private static bool Matches(StoreTagDefinition tag, HashSet<string> categories, string haystack, StoreItem item)
    {
        if (tag.Categories.Any(categories.Contains)) return true;

        if (tag.ClientSide.Count > 0 && item.ClientSide is { } client &&
            tag.ClientSide.Any(v => string.Equals(v, client, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (tag.ServerSide.Count > 0 && item.ServerSide is { } server &&
            tag.ServerSide.Any(v => string.Equals(v, server, StringComparison.OrdinalIgnoreCase)))
            return true;

        return tag.Keywords.Any(k => !string.IsNullOrEmpty(k) && haystack.Contains(Normalize(k), StringComparison.Ordinal));
    }

    /// <summary>
    /// The tags offered as filter chips for a given project type ("mod" or "plugin"), so a Paper
    /// server is not offered "Magic" and a Fabric server is not offered "Permissions &amp; ranks".
    /// </summary>
    public const string TopicKind = "topic";

    public IReadOnlyList<StoreTagDefinition> BrowseTags(string projectType) =>
        _tags.Where(t => t.Browse)
             .Where(t => t.ProjectTypes.Count == 0 ||
                         t.ProjectTypes.Any(p => string.Equals(p, projectType, StringComparison.OrdinalIgnoreCase)))
             .ToList();

    /// <summary>Finds a tag by id; null when the catalogue doesn't define it.</summary>
    public StoreTagDefinition? Find(string id) =>
        _tags.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The tag's name in the interface language, falling back to English and finally to the raw id
    /// — a catalogue that forgot a translation still shows something usable.
    /// </summary>
    public static string LabelFor(StoreTagDefinition tag)
    {
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (tag.Labels.TryGetValue(lang, out var label) && !string.IsNullOrWhiteSpace(label)) return label;
        if (tag.Labels.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english)) return english;
        return tag.Id;
    }

    /// <summary>Lowercases and strips accents so keyword rules survive "Génération" vs "generation".</summary>
    private static string Normalize(string text)
    {
        var lowered = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }
}
