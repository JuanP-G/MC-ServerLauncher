using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The "you can clean up" panel: libraries nothing uses any more, and mods switched off for a month.
/// </summary>
/// <remarks>
/// <para>
/// Only suggested, never done: the panel lists, the user ticks, the user presses the button. What is
/// listed is decided by <see cref="CleanupAdvisor"/>; this half finds the facts it needs (the jars,
/// what the store knows about them, the ledger) and puts its answer on screen.
/// </para>
/// <para>
/// <b>Nothing here runs until the tab has been shown.</b> The panel is also built for servers whose
/// tab is never opened, and by tests that have no business writing to the user's application data
/// or asking Modrinth anything; <see cref="EnsureLoaded"/> is what arms it.
/// </para>
/// </remarks>
public partial class ServerModsViewModel
{
    /// <summary>What the panel lists right now.</summary>
    public ObservableCollection<CleanupSuggestionViewModel> CleanupSuggestions { get; } = new();

    public bool HasCleanupSuggestions => CleanupSuggestions.Count > 0;

    /// <summary>How many rows are ticked.</summary>
    public int SelectedCleanupCount => CleanupSuggestions.Count(s => s.IsSelected);

    /// <remarks>
    /// Not while "update all" runs: it walks the installed rows and swaps their files, and removing
    /// rebuilds those rows under it.
    /// </remarks>
    public bool CanRemoveSelectedCleanup => SelectedCleanupCount > 0 && !IsUpdatingAll;

    /// <summary>"Quitar 1 seleccionado" / "Quitar 3 seleccionados".</summary>
    public string RemoveSelectedText => SelectedCleanupCount == 1
        ? Localizer.Get("Cleanup_RemoveSelectedOne")
        : string.Format(Localizer.Get("Cleanup_RemoveSelectedFmt"), SelectedCleanupCount);

    /// <summary>Set once the tab has been shown; until then, nothing reads or writes the ledger.</summary>
    private bool _cleanupArmed;

    private CancellationTokenSource? _cleanupScan;

    private ContentLedgerStore? _ledger;

    private ContentLedgerStore Ledger => _ledger ??= ContentLedgerStore.For(_config.Id);

    /// <summary>Starts the panel's scans: the tab is on screen.</summary>
    private void ArmCleanup()
    {
        _cleanupArmed = true;
        _ = RefreshCleanupAsync();
    }

    /// <summary>Scans again after the folder changed, if the panel is armed.</summary>
    /// <remarks>
    /// After a short pause, so that switching several mods in a row asks the store once, not once
    /// per switch: each new call cancels the one still waiting.
    /// </remarks>
    private void RescanCleanup()
    {
        if (_cleanupArmed) _ = RefreshCleanupAsync(RescanPause);
    }

    private static readonly TimeSpan RescanPause = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Works out what can be cleaned up and shows it. A newer call cancels an older one.
    /// </summary>
    /// <remarks>
    /// Never throws and never shows an error: a suggestion that could not be worked out is simply
    /// not made, which is the right failure for something the user did not ask for.
    /// </remarks>
    internal async Task RefreshCleanupAsync(TimeSpan pause = default)
    {
        _cleanupScan?.Cancel();
        _cleanupScan?.Dispose();
        var scan = _cleanupScan = new CancellationTokenSource();
        var ct = scan.Token;

        if (pause > TimeSpan.Zero)
        {
            try { await Task.Delay(pause, ct); }
            catch (OperationCanceledException) { return; }   // a newer scan is on its way
        }

        var folder = Path.Combine(_config.FolderPath, ContentFolder);
        if (_config.Type == ServerType.Vanilla || !Directory.Exists(folder))
        {
            ShowCleanup(Array.Empty<CleanupSuggestionViewModel>());
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var entries = await Task.Run(() => ContentManifest.ReadAll(folder, _manifests.Read), ct);

            var ledger = Ledger;
            lock (ledger.Gate)
            {
                var files = entries.Select(e => (ContentReplacement.DisplayName(e.Path), e.Enabled));
                if (CleanupAdvisor.Reconcile(ledger.Current, files, now)) ledger.Save();
            }

            var (jars, storeAnswered) = await IdentifyAsync(entries, ct);
            ct.ThrowIfCancellationRequested();

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
                    rows.Add(new CleanupSuggestionViewModel(off.Jar.Path, off.Jar.FileName, ReasonFor(off),
                        key));
                }
            }

            ct.ThrowIfCancellationRequested();
            ShowCleanup(rows);
        }
        catch (OperationCanceledException)
        {
            // A newer scan is on its way.
        }
        catch
        {
            // Not worth an error: the panel just does not suggest anything this time.
        }
    }

    /// <summary>
    /// Every jar as the advisor needs it: what it says about itself, which store project it is, and
    /// whether it is a library. Also whether the store answered at all.
    /// </summary>
    private async Task<(IReadOnlyList<CleanupAdvisor.Jar> Jars, bool StoreAnswered)> IdentifyAsync(
        IReadOnlyList<ContentManifest.Entry> entries, CancellationToken ct)
    {
        var hashByPath = await HashesAsync(entries.Select(e => e.Path), ct);
        await LearnAsync(hashByPath.Values, ct);

        var versionByPath = hashByPath
            .Where(kv => Known(kv.Value) is not null)
            .ToDictionary(kv => kv.Key, kv => Known(kv.Value)!, StringComparer.OrdinalIgnoreCase);

        // "Answered" means it recognised something here. GetVersionsByHashAsync swallows every error
        // and returns an empty map, so an empty answer cannot be told apart from being offline — and
        // being wrong in that direction only means making no suggestion.
        var storeAnswered = versionByPath.Count > 0;

        var categories = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (storeAnswered)
        {
            var projects = await _modrinthService.GetProjectsAsync(versionByPath.Values.Select(v
                => v.ProjectId), ct);
            if (projects is null) storeAnswered = false;
            else foreach (var project in projects) categories[project.Id] = project.Categories;
        }

        var jars = entries.Select(entry =>
        {
            versionByPath.TryGetValue(entry.Path, out var version);
            var cats = version is not null && categories.TryGetValue(version.ProjectId, out var found) ? found
                : null;
            var storeDeps = version?.Dependencies
                .Where(d => d.ProjectId is not null &&
                            !string.Equals(d.DependencyType, "incompatible",
                                StringComparison.OrdinalIgnoreCase))
                .Select(d => d.ProjectId!)
                .ToList() ?? new List<string>();

            return new CleanupAdvisor.Jar(entry.Path, entry.Enabled, entry.Manifest, version?.ProjectId,
                CleanupAdvisor.IsLibrary(entry.Manifest, cats), storeDeps);
        }).ToList();

        return (jars, storeAnswered);
    }

    /// <summary>Replaces the panel's rows.</summary>
    internal void ShowCleanup(IReadOnlyList<CleanupSuggestionViewModel> rows)
    {
        foreach (var old in CleanupSuggestions) old.PropertyChanged -= OnCleanupRowChanged;
        CleanupSuggestions.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnCleanupRowChanged;
            CleanupSuggestions.Add(row);
        }
        NotifyCleanupChanged();
    }

    private void OnCleanupRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupSuggestionViewModel.IsSelected)) NotifyCleanupChanged();
    }

    private void NotifyCleanupChanged()
    {
        OnPropertyChanged(nameof(HasCleanupSuggestions));
        OnPropertyChanged(nameof(SelectedCleanupCount));
        OnPropertyChanged(nameof(CanRemoveSelectedCleanup));
        OnPropertyChanged(nameof(RemoveSelectedText));
        RemoveSelectedCleanupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Not now": hides every row on the panel for <see cref="CleanupAdvisor.Snooze"/>.</summary>
    [RelayCommand]
    private void DismissCleanup()
    {
        if (CleanupSuggestions.Count == 0) return;

        var ledger = Ledger;
        lock (ledger.Gate)
        {
            var now = DateTime.UtcNow;
            foreach (var row in CleanupSuggestions) ledger.Current.DismissedUtc[row.Key] = now;
            ledger.Save();
        }
        ShowCleanup(Array.Empty<CleanupSuggestionViewModel>());
    }

    /// <summary>Deletes the ticked rows' files, for good, and says how that went.</summary>
    /// <remarks>
    /// No confirmation window: ticking rows on a panel that says what each one is and why is the
    /// confirmation, and a second question about the same thing is one people learn to click through.
    /// Each file on its own — one held by a running server does not stop the others.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedCleanup))]
    private void RemoveSelectedCleanup()
    {
        var chosen = CleanupSuggestions.Where(s => s.IsSelected).ToList();
        if (chosen.Count == 0) return;

        var removed = new List<string>();
        var failed = new List<string>();
        var changed = new List<string>();
        foreach (var row in chosen)
        {
            if (!row.IsStillAsScanned)
            {
                changed.Add(row.FileName);
                continue;
            }

            try
            {
                File.Delete(row.Path);
                removed.Add(row.FileName);
            }
            catch
            {
                failed.Add(row.FileName);
            }
        }

        RefreshInstalledMods();

        // After the refresh, which rewrites the status line.
        var parts = new List<string>();
        if (removed.Count > 0) parts.Add(string.Format(Localizer.Get("Cleanup_RemovedFmt"),
            string.Join(", ", removed)));
        if (failed.Count > 0) parts.Add(string.Format(Localizer.Get("Cleanup_FailedFmt"),
            string.Join(", ", failed)));
        if (changed.Count > 0)
            parts.Add(string.Format(Localizer.Get("Cleanup_ChangedFmt"), string.Join(", ", changed)));
        UpdateStatus = string.Join(" ", parts);
    }

    /// <summary>Remembers that a jar was switched on or off just now, if the panel is armed.</summary>
    private void RecordToggle(string filePath, bool nowEnabled)
    {
        if (!_cleanupArmed) return;

        var ledger = Ledger;
        lock (ledger.Gate)
        {
            CleanupAdvisor.RecordToggle(ledger.Current, ContentReplacement.DisplayName(filePath), nowEnabled,
                DateTime.UtcNow);
            ledger.Save();
        }
    }

    /// <summary>Keeps a disabled mod's date when an update gives its file a new name.</summary>
    private void CarryDisabledDate(string oldPath, string newFileName)
    {
        if (!_cleanupArmed) return;

        var ledger = Ledger;
        lock (ledger.Gate)
        {
            CleanupAdvisor.CarryDisabledDate(ledger.Current, ContentReplacement.DisplayName(oldPath),
                newFileName);
            ledger.Save();
        }
    }

    /// <summary>Why a library is on the panel.</summary>
    internal string ReasonFor(CleanupAdvisor.Orphan orphan)
    {
        var users = string.Join(", ", orphan.FormerUsers);
        return orphan.Reason switch
        {
            CleanupAdvisor.OrphanReason.DependentsRemoved => string.Format(Localizer.Get(
                orphan.FormerUsers.Count == 1 ? "Cleanup_OrphanRemovedOneFmt"
                    : "Cleanup_OrphanRemovedManyFmt"), users),
            CleanupAdvisor.OrphanReason.DependentsAlsoOrphan
                => string.Format(Localizer.Get("Cleanup_OrphanChainFmt"), users),
            _ => Localizer.Get(IsPluginBased ? "Cleanup_OrphanNoPlugin" : "Cleanup_OrphanNoMod")
        };
    }

    /// <summary>Why a disabled mod is on the panel.</summary>
    internal static string ReasonFor(CleanupAdvisor.LongDisabled off) =>
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
    internal static string FormatDay(DateTime utc, DateTime? today = null)
    {
        var day = utc.ToLocalTime();
        var key = day.Year == (today ?? DateTime.Today).Year
            ? "Cleanup_DateFormat"
            : "Cleanup_DateWithYearFormat";
        return day.ToString(Localizer.Get(key), CultureInfo.CurrentUICulture);
    }
}
