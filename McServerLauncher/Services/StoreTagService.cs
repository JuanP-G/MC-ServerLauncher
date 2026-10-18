using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Every rule adds to a score and a tag applies from <see cref="Threshold"/>. That is what replaced
/// "any rule matching is enough", which tagged a password manager as combat (the keyword "sword"
/// inside "password") and a backpack mod as combat too (Modrinth files armour, tools and backpacks
/// under "equipment"). Keywords are whole words, and a project whose author ticked most of the
/// topics gets little from them: those authors tick everything, so the ticks say little. When
/// nothing reaches the threshold the project has no tags, and the card says so by showing none
/// rather than a guess.
/// </para>
/// <para>
/// The catalogue is read from the embedded copy, unless
/// <c>%APPDATA%\McServerLauncher\store-tags.json</c> exists, in which case that file is used
/// instead. Tags can therefore be added, translated, recoloured or re-mapped without a new build.
/// </para>
/// </summary>
public sealed class StoreTagService
{
    private const string EmbeddedResourceName = "McServerLauncher.Resources.store-tags.json";

    /// <summary>The score from which a tag applies.</summary>
    internal const double Threshold = 2.0;

    /// <summary>What a Modrinth category is worth.</summary>
    private const double CategoryPoints = 3.0;

    /// <summary>
    /// What a category is worth when the author ticked more than <see cref="NoisyTopicLimit"/> of
    /// the topics the catalogue knows: alone it no longer reaches the threshold, a keyword has to
    /// agree with it.
    /// </summary>
    private const double NoisyCategoryPoints = 1.5;

    private const int NoisyTopicLimit = 4;

    /// <summary>A keyword in the title.</summary>
    private const double TitlePoints = 3.0;

    /// <summary>A keyword in the description, and half a point for each further one, up to a point.</summary>
    private const double DescriptionPoints = 2.0;

    private const double ExtraDescriptionPoints = 0.5;
    private const double ExtraDescriptionCap = 1.0;

    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Shared instance; the catalogue is read once per run. Declared after the static fields it
    /// uses: static initialisers run in the order they are written, and the constructor needs them.
    /// </summary>
    public static StoreTagService Shared { get; } = new();

    private readonly List<StoreTagDefinition> _tags;

    /// <summary>Each tag with its keywords already turned into the form they are matched in.</summary>
    private readonly List<(StoreTagDefinition Tag, string[] Patterns)> _compiled;

    /// <summary>Every category some topic tag listens to, to tell a project that ticked them all.</summary>
    private readonly HashSet<string> _topicCategories;

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

        _compiled = _tags
            .Select(tag => (tag, tag.Keywords.Select(Compile).Where(p => p.Length > 0).ToArray()))
            .ToList();
        _topicCategories = new HashSet<string>(
            _tags.Where(IsTopic).SelectMany(t => t.Categories), StringComparer.OrdinalIgnoreCase);
    }

    private static List<StoreTagDefinition> Load(string overridePath)
    {
        // A user-supplied catalogue wins, but a broken one must not leave the app with no tags at
        // all — fall back to the copy that shipped with the binary.
        try
        {
            if (File.Exists(overridePath))
            {
                var custom = JsonSerializer.Deserialize<StoreTagCatalog>(File.ReadAllText(overridePath),
                    JsonOptions);
                if (custom is { Tags.Count: > 0 }) return custom.Tags;
            }
        }
        catch { /* fall through to the embedded catalogue */ }

        try
        {
            using var stream = typeof(StoreTagService)
                .Assembly.GetManifestResourceStream(EmbeddedResourceName);
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
    /// The tags that apply to <paramref name="item"/>, best first: the higher the score the earlier,
    /// then the catalogue's priority. A project that scores nothing has no tags, which the UI shows
    /// by leaving the row out.
    /// </summary>
    public IReadOnlyList<StoreTagDefinition> Classify(StoreItem item, int max = int.MaxValue,
        string? onlyKind = null)
    {
        if (max <= 0) return Array.Empty<StoreTagDefinition>();

        var categories = new HashSet<string>(item.Categories, StringComparer.OrdinalIgnoreCase);
        var title = Words(item.Title);
        var description = Words(item.Description);
        var categoryPoints = categories.Count(_topicCategories.Contains) > NoisyTopicLimit
            ? NoisyCategoryPoints
            : CategoryPoints;

        var matched = new List<(StoreTagDefinition Tag, double Score)>();
        var fallbacks = new List<(StoreTagDefinition Tag, double Score)>();
        foreach (var (tag, patterns) in _compiled)
        {
            if (onlyKind is not null && !string.Equals(tag.Kind, onlyKind,
                StringComparison.OrdinalIgnoreCase))
                continue;
            if (!AppliesTo(tag, item)) continue;

            var score = Score(tag, patterns, categories, categoryPoints, title, description, item);
            if (score < Threshold) continue;
            (tag.Fallback ? fallbacks : matched).Add((tag, score));
        }

        // Install tags are a different kind of answer from topics, so they never compete with them:
        // topics first, then the install side, each ordered by score and then by the catalogue.
        var ordered = matched
            .OrderBy(m => IsTopic(m.Tag) ? 0 : 1)
            .ThenByDescending(m => m.Score)
            .ThenBy(m => m.Tag.Priority)
            .Select(m => m.Tag)
            .ToList();

        // A dominant tag (a library) goes first and leaves room for one more topic at most.
        var dominant = ordered.FirstOrDefault(t => t.Dominant && IsTopic(t));
        if (dominant is not null)
        {
            var rest = ordered.Where(t => t != dominant && IsTopic(t)).Take(1);
            ordered = new[] { dominant }.Concat(rest).Concat(ordered.Where(t => !IsTopic(t))).ToList();
        }

        // What is only worth showing as a last resort is shown when there is no topic at all.
        if (!ordered.Any(IsTopic))
            ordered.InsertRange(0, fallbacks.OrderBy(f => f.Tag.Priority).Select(f => f.Tag));

        return ordered.Count > max ? ordered.Take(max).ToList() : ordered;
    }

    private static bool IsTopic(StoreTagDefinition tag) =>
        string.Equals(tag.Kind, TopicKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a tag can apply to this kind of server. A tag for plugins only is not given to a
    /// mod: "Permissions &amp; ranks" on a Fabric mod is a keyword coincidence, not a description.
    /// An item that doesn't say what it is isn't restricted.
    /// </summary>
    private static bool AppliesTo(StoreTagDefinition tag, StoreItem item) =>
        tag.ProjectTypes.Count == 0 || string.IsNullOrEmpty(item.ProjectType) ||
        tag.ProjectTypes.Any(p => string.Equals(p, item.ProjectType, StringComparison.OrdinalIgnoreCase));

    private static double Score(StoreTagDefinition tag, string[] patterns, HashSet<string> categories,
        double categoryPoints, string title, string description, StoreItem item)
    {
        double score = 0;

        if (tag.Categories.Any(categories.Contains)) score += categoryPoints;

        // The install side is a fact about the project, not a guess about its topic.
        if (tag.ClientSide.Count > 0 && item.ClientSide is { } client &&
            tag.ClientSide.Any(v => string.Equals(v, client, StringComparison.OrdinalIgnoreCase)))
            score += Threshold;
        if (tag.ServerSide.Count > 0 && item.ServerSide is { } server &&
            tag.ServerSide.Any(v => string.Equals(v, server, StringComparison.OrdinalIgnoreCase)))
            score += Threshold;

        if (patterns.Length == 0) return score;

        if (patterns.Any(p => title.Contains(p, StringComparison.Ordinal))) score += TitlePoints;

        var inDescription = patterns.Count(p => description.Contains(p, StringComparison.Ordinal));
        if (inDescription > 0)
            score += DescriptionPoints +
                     Math.Min(ExtraDescriptionCap, (inDescription - 1) * ExtraDescriptionPoints);

        return score;
    }

    /// <summary>
    /// The tags offered as filter chips for a given project type ("mod" or "plugin"), so a Paper
    /// server is not offered "Magic" and a Fabric server is not offered "Permissions &amp; ranks".
    /// </summary>
    public const string TopicKind = "topic";

    public IReadOnlyList<StoreTagDefinition> BrowseTags(string projectType) =>
        _tags.Where(t => t.Browse)
             .Where(t => t.ProjectTypes.Count == 0 ||
                         t.ProjectTypes.Any(p => string.Equals(p, projectType,
                             StringComparison.OrdinalIgnoreCase)))
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
        if (tag.Labels.TryGetValue("en", out var english)
            && !string.IsNullOrWhiteSpace(english)) return english;
        return tag.Id;
    }

    /// <summary>
    /// The text as lowercase, accent-free words with a single space between them and one at each
    /// end. Matching a keyword then is a plain <c>Contains</c> on " word " (whole word) or " word"
    /// (any ending), and punctuation never glues two words together: "https://x" is "https" and
    /// "x", so it contains neither "tps" nor anything that starts with it.
    /// </summary>
    internal static string Words(string text)
    {
        var sb = new StringBuilder(" ");
        foreach (Match word in WordPattern.Matches(Normalize(text ?? string.Empty)))
            sb.Append(word.Value).Append(' ');
        return sb.ToString();
    }

    /// <summary>
    /// A keyword in the form it is searched for in <see cref="Words"/>. Empty when there is
    /// nothing to search for.
    /// </summary>
    private static string Compile(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return string.Empty;
        var anyEnding = keyword.TrimEnd().EndsWith('*');
        var words = Words(keyword.Replace("*", string.Empty)).TrimEnd();
        if (words.Length == 0) return string.Empty;
        return anyEnding ? words : words + " ";
    }

    /// <summary>
    /// Lowercases and strips accents so keyword rules survive "Génération" vs "generation".
    /// </summary>
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
