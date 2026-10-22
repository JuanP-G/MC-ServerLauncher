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
    private const string EmbeddedResourceName = "McServerLauncher.Resources.store-summaries.json";

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
    public static StoreSummaryService Shared { get; } = new(catalog: StoreCatalogService.Shared);

    /// <summary>By project id.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _byId;

    /// <summary>By slug, so a catalogue entry still matches if we only know the slug.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _bySlug;

    /// <summary>The downloaded catalogue, which fills in for projects nobody wrote a sentence for.</summary>
    private readonly StoreCatalogService? _catalog;

    public StoreSummaryService(string? overridePath = null, StoreCatalogService? catalog = null)
    {
        _catalog = catalog;
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
                var custom = JsonSerializer.Deserialize<StoreSummaryCatalog>(File.ReadAllText(overridePath),
                    JsonOptions);
                if (custom is { Summaries.Count: > 0 })
                    return new Dictionary<string, Dictionary<string,
                        string>>(custom.Summaries, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* a broken override must not cost us the built-in catalogue */ }

        try
        {
            using var stream = typeof(StoreSummaryService)
                .Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is not null)
            {
                var embedded = JsonSerializer.Deserialize<StoreSummaryCatalog>(stream, JsonOptions);
                if (embedded is not null)
                    return new Dictionary<string, Dictionary<string,
                        string>>(embedded.Summaries, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The catalogue's title and sentence for a project known by its slug, or null.</summary>
    /// <remarks>For lists the app writes itself, such as the Performance page's suggestions.</remarks>
    public (string Title, string? Text)? CuratedBySlug(string slug)
    {
        var item = new StoreItem { Slug = slug };
        if (Lookup(item) is not { } entry) return null;
        return (entry.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title) ? title : slug,
            Curated(item));
    }

    /// <summary>
    /// The sentence the downloaded catalogue has for this project, in the interface language. It
    /// comes second, after <see cref="Curated"/>: a person's sentence is always better than a
    /// model's, and the catalogue exists for the thousands nobody has written one for.
    /// </summary>
    private string? FromCatalogue(StoreItem item) =>
        _catalog?.SummaryFor(item.Id, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

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
        var curated = Curated(item) ?? FromCatalogue(item);
        if (!string.IsNullOrWhiteSpace(curated)) return curated;
        return (Lead(item, tags) ?? Generic(item)) + " " + SideSentence(item);
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
        var author = item.Description?.Trim() ?? string.Empty;

        // Without a hand-written summary a card says what the tags say, but only when a tag was
        // earned. A project nothing could be said about shows what its author wrote — in English,
        // but true — rather than "a combat mod" for a backpack.
        var curated = Curated(item) ?? FromCatalogue(item);
        var summary = !string.IsNullOrWhiteSpace(curated) ? curated!
            : Lead(item, tags) ?? (author.Length > 0 ? author : Generic(item));

        // The author's line is worth a second line only when it isn't just the summary again.
        var tagline = string.Equals(author, summary.Trim(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : author;

        return new StoreBlurb(summary, tagline);
    }

    /// <summary>
    /// The opening sentence built from what Modrinth tells us: the kind of project and its main
    /// topic. Classifies on demand, so a caller with no tags at hand still gets "world generation
    /// mod". Null when no topic was earned: a sentence that names none says nothing, so the caller
    /// decides what to show instead (see <see cref="Blurb"/>).
    /// </summary>
    private static string? Lead(StoreItem item, IReadOnlyList<StoreTagDefinition>? tags)
    {
        // The lead-in names the main tag when there is one worth naming. Tags that describe how a
        // project is installed rather than what it does would produce "mod for server-side only",
        // so they never lead the sentence.
        tags ??= StoreTagService.Shared.Classify(item, max: 1, onlyKind: StoreTagService.TopicKind);
        var headline = tags.FirstOrDefault(t =>
            string.Equals(t.Kind, StoreTagService.TopicKind, StringComparison.OrdinalIgnoreCase));
        if (headline is null) return null;

        return string.Format(Localizer.Get(FallbackKeys(item).Fmt),
            InSentence(StoreTagService.LabelFor(headline)));
    }

    /// <summary>The bare "a mod for your server", for a project nothing more can be said about.</summary>
    private static string Generic(StoreItem item) => Localizer.Get(FallbackKeys(item).Plain);

    /// <summary>
    /// Which sentence a project with nothing hand-written gets. "For your server" is wrong for a
    /// mod that does nothing on a server, so a client-only one says who it is for instead.
    /// </summary>
    private static (string Plain, string Fmt) FallbackKeys(StoreItem item) =>
        IsPlugin(item) ? ("Store_Fallback_Plugin", "Store_Fallback_PluginFmt")
        : item.Side == InstallSide.ClientOnly ? ("Store_Fallback_ClientMod", "Store_Fallback_ClientModFmt")
        : ("Store_Fallback_Mod", "Store_Fallback_ModFmt");

    private static bool IsPlugin(StoreItem item) =>
        string.Equals(item.ProjectType, "plugin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The part that actually matters for a server: does everyone have to install this, or only you?
    /// </summary>
    public static string SideSentence(StoreItem item)
    {
        // A mod that does nothing on a server is not "needed on the client too": it is for the
        // client alone, and the sentence says where it goes instead.
        if (item.Side == InstallSide.ClientOnly) return Localizer.Get("Store_Side_ClientOnly");
        if (item.NeedsClient) return Localizer.Get("Store_Side_NeedsClient");
        if (string.Equals(item.ClientSide, "unsupported", StringComparison.OrdinalIgnoreCase))
            return Localizer.Get("Store_Side_ServerOnly");

        // "Optional" is an answer; no answer is not. It used to fall through to the same sentence,
        // which told the owner a client install was optional on the strength of nothing at all.
        if (string.Equals(item.ClientSide, "optional", StringComparison.OrdinalIgnoreCase))
            return Localizer.Get("Store_Side_ClientOptional");
        return Localizer.Get("Store_Side_Unknown");
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
