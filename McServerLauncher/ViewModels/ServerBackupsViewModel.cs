using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>Which backups the list shows.</summary>
public enum BackupFilter { All, Automatic, Manual }

/// <summary>
/// Manages world backups for a server: lists existing zip snapshots, and lets the user trigger one
/// manually, restore an earlier one, or delete one. Automatic backups (before every start, after an
/// explicit stop) are triggered by <see cref="ServerViewModel"/> itself; this view model is just the
/// UI surface over the same <see cref="WorldBackupService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The list is two piles, not one, and it says so. Retention counts the automatic backups (start,
/// stop, while playing) together against one limit and the user's own (manual, before a restore)
/// against another — <see cref="WorldBackupService"/> explains why. A flat list of five read as
/// "five of each kind" with some missing, when it was the automatic pile, full. So the summary shows
/// each pile against its limit, and the row the next backup of its pile will push out says so.
/// </para>
/// <para>
/// Grouped by day, and named by time and reason rather than by file: the zip's name is the same
/// facts written for a file system, and it is still there as the row's tooltip.
/// </para>
/// </remarks>
public partial class ServerBackupsViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private readonly WorldBackupService _backupService = new();
    private bool _hasLoadedOnce;

    public ServerConfig Config => _server.Config;

    /// <summary>Every backup, newest first, whatever the filter.</summary>
    public ObservableCollection<BackupItemViewModel> Items { get; } = new();

    /// <summary>The backups the filter lets through, by day, newest first.</summary>
    public ObservableCollection<BackupDayGroup> Groups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllFilter), nameof(IsAutomaticFilter), nameof(IsManualFilter))]
    private BackupFilter _filter = BackupFilter.All;

    public bool IsAllFilter => Filter == BackupFilter.All;
    public bool IsAutomaticFilter => Filter == BackupFilter.Automatic;
    public bool IsManualFilter => Filter == BackupFilter.Manual;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>There are backups, and none of the kind the filter asks for.</summary>
    public bool IsFilterEmpty => Items.Count > 0 && Groups.Count == 0;

    // ---- The summary ----

    public int AutomaticCount { get; private set; }
    public int ManualCount { get; private set; }

    /// <summary>The same floor of one that pruning uses: a limit of zero still keeps the newest.</summary>
    private int AutomaticLimit => Math.Max(1, Config.BackupRetention);
    private int ManualLimit => Math.Max(1, Config.ManualBackupRetention);

    public string AutomaticCountText => $"{AutomaticCount} / {AutomaticLimit}";
    public string ManualCountText => $"{ManualCount} / {ManualLimit}";

    /// <summary>How full each pile is, 0–100, for the bar under its count.</summary>
    public double AutomaticFill => Math.Min(100, AutomaticCount * 100.0 / AutomaticLimit);
    public double ManualFill => Math.Min(100, ManualCount * 100.0 / ManualLimit);

    public string TotalSizeText { get; private set; } = string.Empty;
    public string NextBackupText { get; private set; } = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// A backup can be made at any time now, running or not.
    /// </summary>
    /// <remarks>
    /// It used to need the server stopped, because zipping a world the JVM is writing to gives a
    /// torn copy. That is still true; what changed is that the server is now asked to let go of the
    /// world first — see <see cref="LiveWorldBackup"/>.
    /// </remarks>
    public bool CanBackupNow => !IsBusy;

    /// <summary>
    /// Restoring still needs the server stopped, and always will.
    /// </summary>
    /// <remarks>
    /// It deletes the world folder and unpacks another one in its place. There is no asking
    /// Minecraft to tolerate that. Nor while a start is getting ready: it would open whatever the
    /// restore had left half done. (Changes to that arrive with <c>IsRunning</c>, which
    /// <c>NotifyCommandStates</c> raises for both.)
    /// </remarks>
    public bool CanRestore => !_server.IsRunning && !_server.IsPreparing && !IsBusy;

    public ServerBackupsViewModel(ServerViewModel server)
    {
        _server = server;
        _server.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ServerViewModel.IsRunning)) return;
            RaiseCanBackupNowChanged();
            // Running or not decides what "next" means: on stop, by the clock, or on start.
            RefreshSummary();
        };
    }

    partial void OnIsBusyChanged(bool value) => RaiseCanBackupNowChanged();

    partial void OnFilterChanged(BackupFilter value) => Regroup();

    private void RaiseCanBackupNowChanged()
    {
        OnPropertyChanged(nameof(CanBackupNow));
        OnPropertyChanged(nameof(CanRestore));
        BackupNowCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Loads the backup list the first time the tab is shown.</summary>
    public void EnsureLoaded()
    {
        if (_hasLoadedOnce) return;
        Refresh();
    }

    /// <summary>Re-reads the list, but only when there is one on screen to be wrong.</summary>
    /// <remarks>
    /// The server's folder can be changed from its settings, which leaves this tab listing the
    /// snapshots of a different server. Loading it here instead would defeat <see cref="EnsureLoaded"/>:
    /// the list is deliberately not read until somebody opens the tab.
    /// </remarks>
    public void RefreshIfLoaded()
    {
        if (_hasLoadedOnce) Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        _hasLoadedOnce = true;
        Items.Clear();
        foreach (var b in _backupService.ListBackups(Config))
            Items.Add(new BackupItemViewModel(b));
        RefreshSummary();
    }

    [RelayCommand]
    private void ShowAll() => Filter = BackupFilter.All;

    [RelayCommand]
    private void ShowAutomatic() => Filter = BackupFilter.Automatic;

    [RelayCommand]
    private void ShowManual() => Filter = BackupFilter.Manual;

    /// <summary>The counts against their limits, the space and the next backup, from the list as read.</summary>
    /// <remarks>Also when nothing on disk changed: the limits and the clock live in the config.</remarks>
    internal void RefreshSummary()
    {
        AutomaticCount = Items.Count(i => i.IsAutomatic);
        ManualCount = Items.Count(i => i.IsManual);
        TotalSizeText = BackupItemViewModel.FormatSize(Items.Sum(i => i.SizeBytes));
        var next = Localizer.Get(NextKey(out var at));
        NextBackupText = at is { } when
            ? string.Format(next, when.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : next;

        // The oldest of a pile that is full is the one its next backup deletes.
        foreach (var item in Items) item.Goes = null;
        if (Config.BackupsEnabled && AutomaticCount >= AutomaticLimit)
            Items.Last(i => i.IsAutomatic).Goes = Localizer.Get("Backup_NextToGo");
        if (ManualCount >= ManualLimit)
            Items.Last(i => i.IsManual).Goes = Localizer.Get("Backup_NextManualToGo");

        foreach (var name in new[]
                 {
                     nameof(AutomaticCount), nameof(ManualCount), nameof(AutomaticCountText), nameof(ManualCountText),
                     nameof(AutomaticFill), nameof(ManualFill), nameof(TotalSizeText), nameof(NextBackupText),
                     nameof(IsEmpty),
                 })
            OnPropertyChanged(name);
        Regroup();
    }

    private string NextKey(out DateTime? at)
    {
        at = null;
        if (!Config.BackupsEnabled) return "Backup_NextOff";
        if (!_server.IsRunning) return "Backup_NextOnStart";
        at = _server.NextAutoBackupUtc;
        return at is null ? "Backup_NextOnStop" : "Backup_NextFmt";
    }

    private void Regroup()
    {
        Groups.Clear();
        var shown = Items.Where(i => Filter switch
        {
            BackupFilter.Automatic => i.IsAutomatic,
            BackupFilter.Manual => i.IsManual,
            _ => true,
        });
        foreach (var day in shown.GroupBy(i => i.CreatedAt.Date))
            Groups.Add(new BackupDayGroup(BackupDayGroup.TitleFor(day.Key, DateTime.Today), day.ToList()));
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private async Task BackupNow()
    {
        IsBusy = true;
        StatusText = Localizer.Get("Backup_Creating");
        try
        {
            // Through the server, not straight to the service: it is the one that knows whether
            // the world has to be prised out of a running JVM first, and it holds the one gate that
            // keeps this from overlapping with a backup the clock started a second earlier.
            var path = await _server.RunBackupAsync("manual");
            StatusText = path is not null ? Localizer.Get("Backup_Done") : Localizer.Get("Backup_NothingToBackUp");
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore(BackupItemViewModel? item)
    {
        if (item is null) return;

        var confirmed = await MessageBox.ConfirmAsync(
            string.Format(Localizer.Get("Backup_ConfirmRestoreFmt"), item.FileName),
            Localizer.Get("Backup_RestoreTitle"));
        if (!confirmed) return;

        IsBusy = true;
        StatusText = Localizer.Get("Msg_BackupRestoring");
        try
        {
            // Through the server, like a backup: it holds the gate that keeps this apart from a
            // backup in progress, and the wake listener that must not start the server meanwhile.
            var restored = await _server.RestoreBackupAsync(item.FilePath, new Progress<string>(s => StatusText = s));
            StatusText = Localizer.Get(restored ? "Msg_BackupRestored" : "Msg_BackupAlreadyRunning");
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Delete(BackupItemViewModel? item)
    {
        if (item is null) return;

        var confirmed = await MessageBox.ConfirmAsync(
            string.Format(Localizer.Get("Backup_ConfirmDeleteFmt"), item.FileName),
            Localizer.Get("Tip_Delete"));
        if (!confirmed) return;

        try { File.Delete(item.FilePath); }
        catch { /* best-effort */ }
        Refresh();
    }
}

/// <summary>The backups of one day, under a heading for it.</summary>
public sealed record BackupDayGroup(string Title, IReadOnlyList<BackupItemViewModel> Items)
{
    /// <summary>"Today", "Yesterday", or the day written out.</summary>
    internal static string TitleFor(DateTime day, DateTime today)
    {
        if (day == today) return Localizer.Get("Backup_Today");
        if (day == today.AddDays(-1)) return Localizer.Get("Backup_Yesterday");
        var culture = CultureInfo.CurrentCulture;
        var text = day.ToString(day.Year == today.Year ? "dddd, d MMMM" : "d MMMM yyyy", culture);
        return text.Length > 0 ? char.ToUpper(text[0], culture) + text[1..] : text;
    }
}

/// <summary>A single backup entry shown in the list.</summary>
public partial class BackupItemViewModel : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public DateTime CreatedAt { get; }
    public long SizeBytes { get; }
    public string CreatedAtText { get; }
    public string SizeText { get; }
    public string TriggerText { get; }

    /// <summary>"13:24 · on stop": what the row is called.</summary>
    public string Title { get; }

    /// <summary>The icon for why it was made.</summary>
    public Symbol Icon { get; }

    /// <summary>Counted against the automatic limit.</summary>
    public bool IsAutomatic { get; }

    /// <summary>Counted against the manual limit.</summary>
    public bool IsManual { get; }

    /// <summary>Why the next backup of its pile deletes this one; null when it does not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGoing))]
    private string? _goes;

    public bool IsGoing => Goes is not null;

    public BackupItemViewModel(WorldBackupService.BackupInfo info)
    {
        FilePath = info.FilePath;
        FileName = info.FileName;
        CreatedAt = info.CreatedAt;
        SizeBytes = info.SizeBytes;
        CreatedAtText = info.CreatedAt.ToString("g");
        SizeText = FormatSize(info.SizeBytes);
        IsAutomatic = WorldBackupService.IsAutomatic(info.Trigger);
        IsManual = WorldBackupService.IsTheUsers(info.Trigger);
        TriggerText = Localizer.Get(info.Trigger switch
        {
            "start" => "Backup_TriggerStart",
            "stop" => "Backup_TriggerStop",
            "manual" => "Backup_TriggerManual",
            "auto" => "Backup_TriggerAuto",
            "before-restore" => "Backup_TriggerBeforeRestore",
            _ => "Backup_Foreign"
        });
        Icon = info.Trigger switch
        {
            "start" => Symbol.Play,
            "stop" => Symbol.Stop,
            "auto" => Symbol.Clock,
            "manual" => Symbol.Person,
            "before-restore" => Symbol.ArrowUndo,
            _ => Symbol.Document,
        };
        Title = info.CreatedAt.ToString("t", CultureInfo.CurrentCulture) + " · " + TriggerText;
    }

    internal static string FormatSize(long bytes)
    {
        var gb = bytes / (1024.0 * 1024.0 * 1024.0);
        if (gb >= 1) return $"{gb:0.#} GB";
        var mb = bytes / (1024.0 * 1024.0);
        return mb >= 1 ? $"{mb:0.#} MB" : $"{bytes / 1024.0:0.#} KB";
    }
}
