using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Manages world backups for a server: lists existing zip snapshots, and lets the user trigger one
/// manually, restore an earlier one, or delete one. Automatic backups (before every start, after an
/// explicit stop) are triggered by <see cref="ServerViewModel"/> itself; this view model is just the
/// UI surface over the same <see cref="WorldBackupService"/>.
/// </summary>
public partial class ServerBackupsViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private readonly WorldBackupService _backupService = new();
    private bool _hasLoadedOnce;

    public ServerConfig Config => _server.Config;

    public ObservableCollection<BackupItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// Backups can only be made/restored while the server is stopped: zipping (or replacing) the
    /// world folder while the JVM has it open and is actively writing risks a torn/inconsistent
    /// snapshot. Both automatic triggers (pre-start, post-stop) already only run while stopped.
    /// </summary>
    public bool CanBackupNow => !_server.IsRunning && !IsBusy;

    public ServerBackupsViewModel(ServerViewModel server)
    {
        _server = server;
        _server.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerViewModel.IsRunning))
                RaiseCanBackupNowChanged();
        };
    }

    partial void OnIsBusyChanged(bool value) => RaiseCanBackupNowChanged();

    private void RaiseCanBackupNowChanged()
    {
        OnPropertyChanged(nameof(CanBackupNow));
        BackupNowCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Loads the backup list the first time the tab is shown.</summary>
    public void EnsureLoaded()
    {
        if (_hasLoadedOnce) return;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        _hasLoadedOnce = true;
        Items.Clear();
        foreach (var b in _backupService.ListBackups(Config))
            Items.Add(new BackupItemViewModel(b));
    }

    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private async Task BackupNow()
    {
        IsBusy = true;
        StatusText = Localizer.Get("Backup_Creating");
        try
        {
            var path = await _backupService.CreateBackupAsync(Config, "manual", new Progress<string>(s => StatusText = s));
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

    [RelayCommand(CanExecute = nameof(CanBackupNow))]
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
            await _backupService.RestoreBackupAsync(Config, item.FilePath, new Progress<string>(s => StatusText = s));
            StatusText = Localizer.Get("Msg_BackupRestored");
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

/// <summary>A single backup entry shown in the list.</summary>
public partial class BackupItemViewModel : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public string CreatedAtText { get; }
    public string SizeText { get; }
    public string TriggerText { get; }

    public BackupItemViewModel(WorldBackupService.BackupInfo info)
    {
        FilePath = info.FilePath;
        FileName = info.FileName;
        CreatedAtText = info.CreatedAt.ToString("g");
        SizeText = FormatSize(info.SizeBytes);
        TriggerText = Localizer.Get(info.Trigger switch
        {
            "start" => "Backup_TriggerStart",
            "stop" => "Backup_TriggerStop",
            "manual" => "Backup_TriggerManual",
            "before-restore" => "Backup_TriggerBeforeRestore",
            _ => "Backup_TriggerUnknown"
        });
    }

    private static string FormatSize(long bytes)
    {
        var mb = bytes / (1024.0 * 1024.0);
        return mb >= 1 ? $"{mb:0.#} MB" : $"{bytes / 1024.0:0.#} KB";
    }
}
