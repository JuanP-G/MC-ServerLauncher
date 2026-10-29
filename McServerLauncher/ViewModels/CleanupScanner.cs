using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Works out what the "you can clean up" panel should list: the facts, and the rows they make.
/// </summary>
/// <remarks>
/// <para>
/// What is listed is decided by <see cref="CleanupAdvisor"/>; this finds the facts it needs — the
/// jars, what the store knows about them, the ledger of who needed what and since when — and turns
/// its answer into rows. Putting the rows on screen, and every question of when to scan, stays with
/// the panel.
/// </para>
/// <para>
/// Only ever suggests. A suggestion that could not be worked out is simply not made, which is the
/// right failure for something the user did not ask for.
/// </para>
/// </remarks>
internal sealed class CleanupScanner
{
    private readonly ServerConfig _config;
    private readonly ModrinthService _store;
    private readonly InstalledContentIndex _index;
    private readonly Func<ContentLedgerStore> _ledger;

    /// <param name="config">The server whose content is scanned.</param>
    /// <param name="store">Asked what category each recognised project is in.</param>
    /// <param name="index">What the store says each jar is.</param>
    /// <param name="ledger">The ledger; asked for only when a scan runs, never when the panel is built.</param>
    public CleanupScanner(ServerConfig config, ModrinthService store, InstalledContentIndex index,
        Func<ContentLedgerStore> ledger)
    {
        _config = config;
        _store = store;
        _index = index;
        _ledger = ledger;
    }

    /// <summary>Scans <paramref name="folder"/> and returns the rows to show.</summary>
    /// <param name="folder">The server's content folder.</param>
    /// <param name="now">Today, for the ledger's dates and the snoozing.</param>
    /// <param name="ct">Cancels the scan.</param>
    public async Task<IReadOnlyList<CleanupSuggestionViewModel>> ScanAsync(string folder, DateTime now,
        CancellationToken ct)
    {
        var entries = await Task.Run(() => ContentManifest.ReadAll(folder, _index.Manifests.Read), ct);

        var ledger = _ledger();
        Reconcile(ledger, entries, now);

        var (jars, storeAnswered) = await IdentifyAsync(entries, ct);
        ct.ThrowIfCancellationRequested();

        return BuildRows(ledger, jars, storeAnswered, now);
    }

    /// <summary>Brings the ledger in line with what is on disk, writing it only when it changed.</summary>
    private static void Reconcile(ContentLedgerStore ledger, IReadOnlyList<ContentManifest.Entry> entries,
        DateTime now)
    {
        lock (ledger.Gate)
        {
            var files = entries.Select(e => (ContentReplacement.DisplayName(e.Path), e.Enabled));
            if (CleanupAdvisor.Reconcile(ledger.Current, files, now)) ledger.Save();
        }
    }

    /// <summary>The orphan libraries, then the long-disabled mods, skipping what the user snoozed.</summary>
    private List<CleanupSuggestionViewModel> BuildRows(ContentLedgerStore ledger,
        IReadOnlyList<CleanupAdvisor.Jar> jars, bool storeAnswered, DateTime now)
    {
        var rows = new List<CleanupSuggestionViewModel>();
        lock (ledger.Gate)
        {
            var record = ledger.Current;

            // Asked before recording: the record is who needed it *last time*, which is the one
            // fact that explains an orphan.
            var orphans = CleanupAdvisor.FindOrphanLibraries(jars, record.DependentsByLibrary,
                storeAnswered);
            // Written only when it changed: this runs after every toggle and delete.
            if (storeAnswered && CleanupAdvisor.RecordDependents(record, jars)) ledger.Save();

            foreach (var orphan in orphans)
            {
                var key = CleanupAdvisor.OrphanKey(orphan.Jar);
                if (CleanupAdvisor.IsSnoozed(record, key, now)) continue;
                rows.Add(new CleanupSuggestionViewModel(orphan.Jar.Path, orphan.Jar.FileName,
                    ReasonFor(orphan), key)
                {
                    // "Nothing installed needs it" is the weakest reason: a mod the store does not
                    // know may need it without saying so. Offered, but left for the user to tick.
                    IsSelected = orphan.Reason != CleanupAdvisor.OrphanReason.NoKnownUser,
                });
            }

            foreach (var off in CleanupAdvisor.FindLongDisabled(jars, record, now,
                CleanupAdvisor.DisabledThreshold))
            {
                var key = CleanupAdvisor.DisabledKey(off.Jar);
                if (CleanupAdvisor.IsSnoozed(record, key, now)) continue;
                if (rows.Any(r => string.Equals(r.Path, off.Jar.Path,
                    StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new CleanupSuggestionViewModel(off.Jar.Path, off.Jar.FileName, ReasonFor(off), key));
            }
        }
        return rows;
    }

    /// <summary>
    /// Every jar as the advisor needs it: what it says about itself, which store project it is, and
    /// whether it is a library. Also whether the store answered at all.
    /// </summary>
    private async Task<(IReadOnlyList<CleanupAdvisor.Jar> Jars, bool StoreAnswered)> IdentifyAsync(
        IReadOnlyList<ContentManifest.Entry> entries, CancellationToken ct)
    {
        var hashByPath = await _index.HashesAsync(entries.Select(e => e.Path), ct);
        await _index.LearnAsync(hashByPath.Values, ct);

        var versionByPath = hashByPath
            .Where(kv => _index.Known(kv.Value) is not null)
            .ToDictionary(kv => kv.Key, kv => _index.Known(kv.Value)!, StringComparer.OrdinalIgnoreCase);

        // "Answered" means it recognised something here. GetVersionsByHashAsync swallows every error
        // and returns an empty map, so an empty answer cannot be told apart from being offline — and
        // being wrong in that direction only means making no suggestion.
        var storeAnswered = versionByPath.Count > 0;

        var categories = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (storeAnswered)
        {
            var projects = await _store.GetProjectsAsync(versionByPath.Values.Select(v => v.ProjectId), ct);
            if (projects is null) storeAnswered = false;
            else foreach (var project in projects) categories[project.Id] = project.Categories;
        }

        var jars = entries.Select(entry =>
        {
            versionByPath.TryGetValue(entry.Path, out var version);
            return ToJar(entry, version, version is not null && categories.TryGetValue(version.ProjectId,
                out var found) ? found : null);
        }).ToList();

        return (jars, storeAnswered);
    }

    private static CleanupAdvisor.Jar ToJar(ContentManifest.Entry entry, VersionResult? version,
        List<string>? categories)
    {
        var storeDeps = version?.Dependencies
            .Where(d => d.ProjectId is not null &&
                        !string.Equals(d.DependencyType, "incompatible", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.ProjectId!)
            .ToList() ?? new List<string>();

        return new CleanupAdvisor.Jar(entry.Path, entry.Enabled, entry.Manifest, version?.ProjectId,
            CleanupAdvisor.IsLibrary(entry.Manifest, categories), storeDeps);
    }

    /// <summary>Why a library is on the panel.</summary>
    public string ReasonFor(CleanupAdvisor.Orphan orphan)
    {
        var users = string.Join(", ", orphan.FormerUsers);
        return orphan.Reason switch
        {
            CleanupAdvisor.OrphanReason.DependentsRemoved => string.Format(Localizer.Get(
                orphan.FormerUsers.Count == 1 ? "Cleanup_OrphanRemovedOneFmt"
                    : "Cleanup_OrphanRemovedManyFmt"), users),
            CleanupAdvisor.OrphanReason.DependentsAlsoOrphan
                => string.Format(Localizer.Get("Cleanup_OrphanChainFmt"), users),
            _ => Localizer.Get(ServerTypeCatalog.IsPluginBased(_config.Type) ? "Cleanup_OrphanNoPlugin"
                : "Cleanup_OrphanNoMod")
        };
    }

    /// <summary>Why a disabled mod is on the panel.</summary>
    public static string ReasonFor(CleanupAdvisor.LongDisabled off) =>
        string.Format(Localizer.Get(off.SinceIsFirstSeen ? "Cleanup_DisabledAtLeastFmt"
            : "Cleanup_DisabledSinceFmt"),
            FormatDay(off.SinceUtc), off.Days);

    /// <summary>
    /// "2 de agosto", "August 2", in the app's language — with the year when it is not this one.
    /// </summary>
    /// <remarks>
    /// The pattern is its own resource rather than part of the sentence: a resx value holding
    /// <c>{0:d MMMM}</c> would break the check that every translation keeps the same placeholders.
    /// Without the year, a mod off since an August more than a year ago read as "since 2 August ·
    /// 437 days".
    /// </remarks>
    /// <param name="utc">The day.</param>
    /// <param name="today">Today, for tests; the real one otherwise.</param>
    public static string FormatDay(DateTime utc, DateTime? today = null)
    {
        var day = utc.ToLocalTime();
        var key = day.Year == (today ?? DateTime.Today).Year
            ? "Cleanup_DateFormat"
            : "Cleanup_DateWithYearFormat";
        return day.ToString(Localizer.Get(key), System.Globalization.CultureInfo.CurrentUICulture);
    }
}
