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
        : Localizer.Format("Cleanup_RemoveSelectedFmt", SelectedCleanupCount);

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
            var rows = await _cleanupScanner.ScanAsync(folder, DateTime.UtcNow, ct);
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
        if (removed.Count > 0) parts.Add(Localizer.Format("Cleanup_RemovedFmt",
            string.Join(", ", removed)));
        if (failed.Count > 0) parts.Add(Localizer.Format("Cleanup_FailedFmt",
            string.Join(", ", failed)));
        if (changed.Count > 0)
            parts.Add(Localizer.Format("Cleanup_ChangedFmt", string.Join(", ", changed)));
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

    /// <summary>Why a library is on the panel; see <see cref="CleanupScanner"/>.</summary>
    internal string ReasonFor(CleanupAdvisor.Orphan orphan) => _cleanupScanner.ReasonFor(orphan);

    /// <summary>Why a disabled mod is on the panel; see <see cref="CleanupScanner"/>.</summary>
    internal static string ReasonFor(CleanupAdvisor.LongDisabled off) => CleanupScanner.ReasonFor(off);

    /// <summary>The day a mod went off, in the app's language; see <see cref="CleanupScanner"/>.</summary>
    internal static string FormatDay(DateTime utc, DateTime? today = null) => CleanupScanner.FormatDay(utc, today);
}
