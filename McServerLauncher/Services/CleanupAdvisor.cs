using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McServerLauncher.Services;

/// <summary>
/// Which installed jars a server no longer needs: libraries nothing uses any more, and mods that
/// have been switched off for a long time.
/// </summary>
/// <remarks>
/// <para>
/// Only ever a suggestion. The user ticks and confirms; nothing here deletes anything. That is what
/// lets it be useful at all — but a suggestion that is wrong once is one nobody trusts again, so
/// every rule leans towards keeping a jar.
/// </para>
/// <para>
/// <b>What counts as a library</b> is decided by <see cref="IsLibrary"/>, not by Modrinth's category
/// alone: JEI is filed under "library" there, and so is GeckoLib, and offering to remove the recipe
/// viewer people open every day because "no mod needs it" is exactly the wrong suggestion.
/// </para>
/// <para>
/// <b>What still needs a library</b> is everything installed that is not a library, switched on or
/// off — a disabled mod is one the user may turn back on, and its library must still be there when
/// they do. Needs are followed through every way a jar can say it uses another (hard and soft
/// dependencies, recommendations, optional ones, and the store's own dependency list), and from
/// library to library, so a chain is kept or let go as a whole.
/// </para>
/// </remarks>
internal static partial class CleanupAdvisor
{
    /// <summary>How long a mod has to have been off before it is suggested.</summary>
    internal static readonly TimeSpan DisabledThreshold = TimeSpan.FromDays(30);

    /// <summary>How long "not now" keeps a suggestion away.</summary>
    internal static readonly TimeSpan Snooze = TimeSpan.FromDays(30);

    /// <summary>
    /// Libraries that are nothing but libraries, by the id their jar gives itself.
    /// </summary>
    /// <remarks>
    /// For the ones Modrinth also files under something else — malilib is "library, utility" exactly
    /// as JEI is, so the category cannot tell them apart. Written by hand, and only names whose whole
    /// purpose is to be used by other mods or plugins.
    /// </remarks>
    internal static readonly HashSet<string> KnownLibraryIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fabric, Forge, NeoForge
        "fabric-api", "fabric-language-kotlin", "kotlinforforge",
        "cloth-config", "cloth-config2", "yet_another_config_lib_v3", "yacl", "midnightlib",
        "forgeconfigapiport", "mezz_config",
        "architectury", "balm", "malilib", "puzzleslib", "geckolib", "owo", "collective",
        "creativecore", "citadel", "moonlight", "iceberg", "prism", "konkrete", "jamlib",
        "bookshelf", "resourcefullib", "supermartijn642corelib", "cristellib", "placeholder-api",
        "framework", "fzzy_config", "trender", "lithostitched",
        // Bukkit, Paper, Purpur
        "ProtocolLib", "packetevents", "CommandAPI", "NBTAPI", "PlaceholderAPI"
    };

    /// <summary>One jar as the advisor sees it.</summary>
    /// <param name="Path">The file on disk.</param>
    /// <param name="Enabled">False for a <c>.jar.disabled</c>.</param>
    /// <param name="Manifest">What the jar says about itself.</param>
    /// <param name="ProjectId">Its store project, or null if the store does not know it.</param>
    /// <param name="IsLibrary">From <see cref="IsLibrary"/>.</param>
    /// <param name="StoreDependencies">The project ids the store lists as its dependencies, any kind but incompatible.</param>
    internal sealed record Jar(string Path, bool Enabled, ContentManifest.Manifest Manifest,
        string? ProjectId, bool IsLibrary, IReadOnlyCollection<string> StoreDependencies)
    {
        /// <summary>The file name without <c>.disabled</c>: the ledger's key for it.</summary>
        public string FileName => ContentReplacement.DisplayName(Path);

        /// <summary>What to call it in a sentence: its own name, else its id, else its file.</summary>
        public string Label => Manifest.Name ?? Manifest.Id ?? FileName;

        /// <summary>The key a library is remembered under: its id, which survives an update.</summary>
        public string LibraryKey => Manifest.Id ?? FileName;
    }

    /// <summary>Why a library is suggested.</summary>
    internal enum OrphanReason
    {
        /// <summary>The mods that last needed it are no longer installed.</summary>
        DependentsRemoved,

        /// <summary>What last needed it is still here, but is itself something nothing needs.</summary>
        DependentsAlsoOrphan,

        /// <summary>Nothing installed uses it, and the app never saw what did.</summary>
        NoKnownUser
    }

    /// <summary>A library nothing installed needs.</summary>
    internal sealed record Orphan(Jar Jar, OrphanReason Reason, IReadOnlyList<string> FormerUsers);

    /// <summary>A jar switched off for longer than <see cref="DisabledThreshold"/>.</summary>
    /// <param name="Jar">The jar.</param>
    /// <param name="SinceUtc">Since when it has been off.</param>
    /// <param name="SinceIsFirstSeen">True when the date is only when the app first saw it off.</param>
    /// <param name="Days">Whole days off.</param>
    internal sealed record LongDisabled(Jar Jar, DateTime SinceUtc, bool SinceIsFirstSeen, int Days);

    /// <summary>
    /// Whether a jar is a library: on the hand-written list, or filed by the store as a library and
    /// as nothing else.
    /// </summary>
    /// <param name="manifest">What the jar says about itself.</param>
    /// <param name="storeCategories">The project's main categories, or null when the store does not know it.</param>
    internal static bool IsLibrary(ContentManifest.Manifest manifest, IReadOnlyCollection<string>? storeCategories)
    {
        if (manifest.Id is { } id && KnownLibraryIds.Contains(id)) return true;

        return storeCategories is { Count: > 0 } &&
               storeCategories.All(c => string.Equals(c, "library", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a reference most likely means as an installable mod: Fabric's old <c>fabric</c> and its
    /// separately named modules (<c>fabric-resource-loader-v1</c>, <c>fabric-api-base</c>) all mean
    /// fabric-api.
    /// </summary>
    private static string Canonical(string reference) =>
        string.Equals(reference, "fabric", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reference, "fabric-api-base", StringComparison.OrdinalIgnoreCase) ||
        FabricModule().IsMatch(reference)
            ? "fabric-api"
            : reference;

    [System.Text.RegularExpressions.GeneratedRegex(@"^fabric-[a-z0-9-]+-v\d+$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FabricModule();

    /// <summary>
    /// The libraries <paramref name="user"/> uses: the ones that <em>are</em> what it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In three steps, each only when the one before found nothing: the library whose own id or
    /// alias is the name; the one whose id the name stands for (a Fabric API module means
    /// fabric-api); and only then any library that merely carries the name inside itself. Taking
    /// the carriers first is how malilib — which bundles three Fabric API modules — looked used by
    /// every mod that uses those modules, and was never suggested.
    /// </para>
    /// <para>
    /// The last step is what keeps it safe: a name nothing else answers to still keeps whatever
    /// carries it.
    /// </para>
    /// </remarks>
    private static IEnumerable<Jar> UsedBy(Jar user, IReadOnlyList<Jar> libraries)
    {
        var used = new HashSet<Jar>(ReferenceEqualityComparer.Instance);

        foreach (var library in libraries)
            if (library.ProjectId is { } project && user.StoreDependencies.Contains(project, StringComparer.Ordinal))
                used.Add(library);

        foreach (var reference in user.Manifest.References)
        {
            var owners = Owning(reference, libraries);
            if (owners.Count == 0) owners = Owning(Canonical(reference), libraries);
            if (owners.Count == 0)
                owners = libraries.Where(l => l.Manifest.Provides.Contains(reference, StringComparer.OrdinalIgnoreCase)).ToList();
            used.UnionWith(owners);
        }

        used.Remove(user);
        return used;
    }

    private static List<Jar> Owning(string id, IReadOnlyList<Jar> libraries) =>
        libraries.Where(l => l.Manifest.OwnProvides.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Every library something installed still needs, followed through chains of libraries.</summary>
    private static HashSet<Jar> NeededLibraries(IReadOnlyList<Jar> jars)
    {
        var libraries = jars.Where(j => j.IsLibrary).ToList();
        var needed = new HashSet<Jar>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Jar>(jars.Where(j => !j.IsLibrary));

        while (queue.Count > 0)
        {
            foreach (var library in UsedBy(queue.Dequeue(), libraries))
            {
                if (needed.Add(library)) queue.Enqueue(library);
            }
        }

        return needed;
    }

    /// <summary>The libraries nothing installed needs.</summary>
    /// <param name="jars">Everything in the content folder, enabled or not.</param>
    /// <param name="knownDependents">From the ledger: who last needed each library, by library key.</param>
    /// <param name="storeAnswered">
    /// Whether the store identified the jars. Without it, the dependencies only the store knows are
    /// missing — a mod whose own metadata forgets a library it uses would leave that library looking
    /// unused — so nothing is suggested at all.
    /// </param>
    internal static IReadOnlyList<Orphan> FindOrphanLibraries(IReadOnlyList<Jar> jars,
        IReadOnlyDictionary<string, List<string>> knownDependents, bool storeAnswered)
    {
        if (!storeAnswered) return Array.Empty<Orphan>();

        var needed = NeededLibraries(jars);
        var orphans = jars.Where(j => j.IsLibrary && !needed.Contains(j)).ToList();
        var orphanLabels = orphans.Select(o => o.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presentLabels = jars.Select(j => j.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return orphans.Select(jar =>
        {
            var former = knownDependents.TryGetValue(jar.LibraryKey, out var names)
                ? (IReadOnlyList<string>)names
                : Array.Empty<string>();
            var stillHere = former.Where(presentLabels.Contains).ToList();

            var reason = former.Count == 0 ? OrphanReason.NoKnownUser
                : stillHere.Count == 0 ? OrphanReason.DependentsRemoved
                : stillHere.All(orphanLabels.Contains) ? OrphanReason.DependentsAlsoOrphan
                : OrphanReason.NoKnownUser;

            return new Orphan(jar, reason, former);
        }).ToList();
    }

    /// <summary>
    /// Writes down, for every library still needed, which jars need it — so that once they are gone
    /// the panel can say who it was for.
    /// </summary>
    /// <remarks>
    /// Libraries that are no longer installed lose their entry; libraries that nothing needs keep the
    /// one they had, which is the record of who used to.
    /// </remarks>
    internal static void RecordDependents(ContentLedger ledger, IReadOnlyList<Jar> jars)
    {
        var libraries = jars.Where(j => j.IsLibrary).ToList();
        var usedBy = new Dictionary<Jar, List<Jar>>(ReferenceEqualityComparer.Instance);
        foreach (var jar in jars) usedBy[jar] = UsedBy(jar, libraries).ToList();

        foreach (var library in NeededLibraries(jars))
        {
            var users = jars.Where(j => usedBy[j].Contains(library))
                .Select(j => j.Label)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (users.Count > 0) ledger.DependentsByLibrary[library.LibraryKey] = users;
        }

        var installed = jars.Where(j => j.IsLibrary).Select(j => j.LibraryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in ledger.DependentsByLibrary.Keys.Where(k => !installed.Contains(k)).ToList())
            ledger.DependentsByLibrary.Remove(gone);
    }

    /// <summary>The jars that have been off for longer than <paramref name="threshold"/>.</summary>
    internal static IReadOnlyList<LongDisabled> FindLongDisabled(IReadOnlyList<Jar> jars, ContentLedger ledger,
        DateTime nowUtc, TimeSpan threshold)
    {
        var found = new List<LongDisabled>();
        foreach (var jar in jars.Where(j => !j.Enabled))
        {
            if (!ledger.DisabledSinceUtc.TryGetValue(jar.FileName, out var since)) continue;
            var off = nowUtc - since;
            if (off <= threshold) continue;

            found.Add(new LongDisabled(jar, since, ledger.DisabledFirstSeen.Contains(jar.FileName), (int)off.TotalDays));
        }
        return found;
    }

    /// <summary>
    /// Brings the ledger in line with the folder: dates for disabled jars it has not seen, none for
    /// jars that are on or gone, and expired "not now"s dropped.
    /// </summary>
    /// <param name="ledger">The server's ledger.</param>
    /// <param name="files">Every jar's file name without <c>.disabled</c>, and whether it is on.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>True when anything changed and the ledger needs saving.</returns>
    internal static bool Reconcile(ContentLedger ledger, IEnumerable<(string Name, bool Enabled)> files, DateTime nowUtc)
    {
        var changed = false;
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, enabled) in files)
        {
            if (enabled) continue;
            disabled.Add(name);

            // Off before the app was keeping count, or turned off by hand: the day it is first seen
            // is the earliest date that can honestly be claimed.
            if (ledger.DisabledSinceUtc.TryAdd(name, nowUtc))
            {
                ledger.DisabledFirstSeen.Add(name);
                changed = true;
            }
        }

        foreach (var name in ledger.DisabledSinceUtc.Keys.Where(n => !disabled.Contains(n)).ToList())
        {
            ledger.DisabledSinceUtc.Remove(name);
            ledger.DisabledFirstSeen.Remove(name);
            changed = true;
        }

        foreach (var key in ledger.DismissedUtc.Where(d => nowUtc - d.Value > Snooze).Select(d => d.Key).ToList())
        {
            ledger.DismissedUtc.Remove(key);
            changed = true;
        }

        return changed;
    }

    /// <summary>Records that a jar was switched off just now, or forgets that it was.</summary>
    internal static void RecordToggle(ContentLedger ledger, string fileName, bool nowEnabled, DateTime nowUtc)
    {
        if (nowEnabled)
        {
            ledger.DisabledSinceUtc.Remove(fileName);
        }
        else
        {
            ledger.DisabledSinceUtc[fileName] = nowUtc;
        }
        ledger.DisabledFirstSeen.Remove(fileName);
    }

    /// <summary>
    /// Moves a disabled jar's date to its new file name, for an update that renamed it.
    /// </summary>
    internal static void CarryDisabledDate(ContentLedger ledger, string oldFileName, string newFileName)
    {
        if (!ledger.DisabledSinceUtc.Remove(oldFileName, out var since)) return;
        ledger.DisabledSinceUtc[newFileName] = since;
        if (ledger.DisabledFirstSeen.Remove(oldFileName)) ledger.DisabledFirstSeen.Add(newFileName);
    }

    /// <summary>Whether "not now" was said to this suggestion less than <see cref="Snooze"/> ago.</summary>
    internal static bool IsSnoozed(ContentLedger ledger, string key, DateTime nowUtc) =>
        ledger.DismissedUtc.TryGetValue(key, out var when) && nowUtc - when <= Snooze;

    /// <summary>The key a suggestion is snoozed under.</summary>
    internal static string OrphanKey(Jar jar) => "orphan:" + jar.LibraryKey;

    /// <inheritdoc cref="OrphanKey"/>
    internal static string DisabledKey(Jar jar) => "disabled:" + jar.FileName;
}
