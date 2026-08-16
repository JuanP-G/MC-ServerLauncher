using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using McServerLauncher.Localization;
using McServerLauncher.Models.Store;

namespace McServerLauncher.Services;

/// <summary>
/// Answers "what does this actually do to my server?" in the user's own language.
/// <para>
/// Modrinth's own one-line description is written by the author, in English, and often for other
/// modders ("An intermediary api aimed to ease developing multiplatform mods"). So the store ships
/// a hand-written catalogue for the projects a server owner is most likely to meet, in the five
/// languages the app speaks. It is looked up locally: no request, no key, no cost, and it works
/// offline.
/// </para>
/// <para>
/// Anything outside the catalogue falls back to a sentence built from what Modrinth does tell us —
/// the kind of project, its main tag and, above all, whether players have to install it too, which
/// is the one thing that decides whether a mod is usable on a public server.
/// </para>
/// <para>
/// The catalogue can be replaced without rebuilding by dropping a
/// <c>store-summaries.json</c> in the user's data folder.
/// </para>
/// </summary>
public sealed class StoreSummaryService
{
    /// <summary>Shared instance; the catalogue is read once per run.</summary>
    public static StoreSummaryService Shared { get; } = new();

    private const string EmbeddedResourceName = "McServerLauncher.Resources.store-summaries.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>By project id.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _byId;

    /// <summary>By slug, so a catalogue entry still matches if we only know the slug.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _bySlug;

    public StoreSummaryService(string? overridePath = null)
    {
        var path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McServerLauncher", "store-summaries.json");

        _byId = Load(path);
        _bySlug = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _byId.Values)
            if (entry.TryGetValue("slug", out var slug) && !string.IsNullOrWhiteSpace(slug))
                _bySlug[slug] = entry;
    }

    /// <summary>How many projects the catalogue covers (useful when checking it loaded at all).</summary>
    public int Count => _byId.Count;

    private static Dictionary<string, Dictionary<string, string>> Load(string overridePath)
    {
        try
        {
            if (File.Exists(overridePath))
            {
                var custom = JsonSerializer.Deserialize<StoreSummaryCatalog>(File.ReadAllText(overridePath), JsonOptions);
                if (custom is { Summaries.Count: > 0 })
                    return new Dictionary<string, Dictionary<string, string>>(custom.Summaries, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* a broken override must not cost us the built-in catalogue */ }

        try
        {
            using var stream = typeof(StoreSummaryService).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is not null)
            {
                var embedded = JsonSerializer.Deserialize<StoreSummaryCatalog>(stream, JsonOptions);
                if (embedded is not null)
                    return new Dictionary<string, Dictionary<string, string>>(embedded.Summaries, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* no catalogue simply means every project uses the fallback */ }

        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hand-written summary for this project in the interface language, or null when it isn't
    /// in the catalogue. English is used when a language is missing from an entry.
    /// </summary>
    public string? Curated(StoreItem item)
    {
        var entry = Lookup(item);
        if (entry is null) return null;

        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (entry.TryGetValue(lang, out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        if (entry.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english)) return english;
        return null;
    }

    private Dictionary<string, string>? Lookup(StoreItem item)
    {
        if (!string.IsNullOrEmpty(item.Id) && _byId.TryGetValue(item.Id, out var byId)) return byId;
        if (!string.IsNullOrEmpty(item.Slug) && _bySlug.TryGetValue(item.Slug, out var bySlug)) return bySlug;
        return null;
    }

    /// <summary>
    /// The summary to show: the hand-written one when it exists, otherwise a sentence built from
    /// the project's kind, its main tag and how it has to be installed. Never empty.
    /// </summary>
    public string Describe(StoreItem item, IReadOnlyList<StoreTagDefinition>? tags = null)
    {
        var curated = Curated(item);
        if (!string.IsNullOrWhiteSpace(curated)) return curated;
        return Lead(item, tags) + " " + SideSentence(item);
    }

    /// <summary>
    /// What a card shows about a project: a summary in the user's language, plus the author's own
    /// line underneath when it says something the summary doesn't.
    /// <para>
    /// This is the single answer to "what do we print for this project", and every surface uses it
    /// — the browser cards, the dependencies, the related strip and the details header. Before it
    /// existed each of those repeated its own version of the rule, and two of them stopped at
    /// "hand-written text or else the author's English", which is why the same mod could read in
    /// Spanish on its own page and in English in another mod's related strip.
    /// </para>
    /// <para>
    /// Unlike <see cref="Describe"/> this leaves out the install-side sentence: it is the same for
    /// most projects, so on a strip of six cards it would repeat six times, and the surfaces that
    /// need it show it on their own (a badge on the card, a full sentence on the details page).
    /// </para>
    /// </summary>
    public StoreBlurb Blurb(StoreItem item, IReadOnlyList<StoreTagDefinition>? tags = null)
    {
        var curated = Curated(item);
        var summary = string.IsNullOrWhiteSpace(curated) ? Lead(item, tags) : curated!;

        // The author's line is worth a second line only when it isn't just the summary again.
        var tagline = item.Description?.Trim() ?? string.Empty;
        if (string.Equals(tagline, summary.Trim(), StringComparison.OrdinalIgnoreCase))
            tagline = string.Empty;

        return new StoreBlurb(summary, tagline);
    }

    /// <summary>
    /// The opening sentence built from what Modrinth tells us: the kind of project and its main
    /// topic. Classifies on demand, so a caller with no tags at hand still gets "world generation
    /// mod" instead of the bare "a mod for your server".
    /// </summary>
    private static string Lead(StoreItem item, IReadOnlyList<StoreTagDefinition>? tags)
    {
        var isPlugin = string.Equals(item.ProjectType, "plugin", StringComparison.OrdinalIgnoreCase);

        // The lead-in names the main tag when there is one worth naming. Tags that describe how a
        // project is installed rather than what it does would produce "mod for server-side only",
        // so they never lead the sentence.
        tags ??= StoreTagService.Shared.Classify(item, max: 1, onlyKind: StoreTagService.TopicKind);
        var headline = tags.FirstOrDefault(t =>
            string.Equals(t.Kind, StoreTagService.TopicKind, StringComparison.OrdinalIgnoreCase));

        return headline is null
            ? Localizer.Get(isPlugin ? "Store_Fallback_Plugin" : "Store_Fallback_Mod")
            : string.Format(
                Localizer.Get(isPlugin ? "Store_Fallback_PluginFmt" : "Store_Fallback_ModFmt"),
                InSentence(StoreTagService.LabelFor(headline)));
    }

    /// <summary>
    /// The part that actually matters for a server: does everyone have to install this, or only you?
    /// </summary>
    public static string SideSentence(StoreItem item)
    {
        if (item.NeedsClient) return Localizer.Get("Store_Side_NeedsClient");
        if (string.Equals(item.ClientSide, "unsupported", StringComparison.OrdinalIgnoreCase))
            return Localizer.Get("Store_Side_ServerOnly");
        return Localizer.Get("Store_Side_ClientOptional");
    }

    /// <summary>
    /// Tag labels are written for chips, where they start with a capital. Dropped into the middle
    /// of a sentence that capital is wrong in Spanish, English, French and Portuguese — but right
    /// in German, where the labels are nouns.
    /// </summary>
    private static string InSentence(string label)
    {
        if (string.IsNullOrEmpty(label)) return label;
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de") return label;
        return char.ToLower(label[0], CultureInfo.CurrentUICulture) + label[1..];
    }
}
