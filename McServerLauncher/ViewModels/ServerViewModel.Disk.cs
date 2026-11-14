using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// What the server takes on disk: the line under its status and the Storage page of its settings.
/// </summary>
/// <remarks>
/// Measured off the UI thread, and only when someone is looking or the answer has just changed: when
/// the server is selected, when it stops, when a backup or a restore ends, and on request. Never on
/// the stats tick: walking a big world every few seconds would cost more than the number is worth.
/// A server nobody has looked at since the app opened is not measured at all.
/// </remarks>
public partial class ServerViewModel
{
    private CancellationTokenSource? _diskCts;

    /// <summary>The last measurement, or null before the first one (or when the folder is missing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiskUsage), nameof(DiskUsageText), nameof(DiskTotalText),
        nameof(DiskUsageTip), nameof(DiskWorldText), nameof(DiskBackupsText), nameof(DiskContentText),
        nameof(DiskLogsText), nameof(DiskOtherText), nameof(DiskBackupsLabel), nameof(DiskWorldShare),
        nameof(DiskBackupsShare), nameof(DiskContentShare), nameof(DiskLogsShare), nameof(DiskOtherShare),
        nameof(DiskWorldPercent), nameof(DiskBackupsPercent), nameof(DiskContentPercent),
        nameof(DiskLogsPercent), nameof(DiskOtherPercent))]
    private ServerDiskUsage? _diskUsage;

    /// <summary>When <see cref="DiskUsage"/> was taken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskMeasuredText))]
    private DateTime? _diskMeasuredAt;

    /// <summary>True while a measurement is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskMeasuredText))]
    [NotifyCanExecuteChangedFor(nameof(MeasureDiskCommand))]
    private bool _isMeasuringDisk;

    public bool HasDiskUsage => DiskUsage is not null;

    /// <summary>The header's line: "4.2 GB on disk".</summary>
    public string DiskUsageText => DiskUsage is { } u ? Localizer.Format("Disk_OnDiskFmt", ByteSize.Format(u.Total)) : "";

    /// <summary>The total alone, for the Storage page's big number.</summary>
    public string DiskTotalText => DiskUsage is { } u ? ByteSize.Format(u.Total) : "—";

    /// <summary>The breakdown, for the header line's tooltip.</summary>
    public string DiskUsageTip => DiskUsage is { } u
        ? string.Join(Environment.NewLine,
            $"{Localizer.Get("Disk_World")}: {ByteSize.Format(u.World)}",
            $"{DiskBackupsLabel}: {ByteSize.Format(u.Backups)}",
            $"{DiskContentLabel}: {ByteSize.Format(u.Content)}",
            $"{Localizer.Get("Disk_Logs")}: {ByteSize.Format(u.Logs)}",
            $"{Localizer.Get("Disk_Other")}: {ByteSize.Format(u.Other)}",
            "",
            Localizer.Format("Disk_TipMoreFmt", Localizer.Get("Configure"), Localizer.Get("Sset_Storage")))
        : "";

    public string DiskWorldText => Size(u => u.World);
    public string DiskBackupsText => Size(u => u.Backups);
    public string DiskContentText => Size(u => u.Content);
    public string DiskLogsText => Size(u => u.Logs);
    public string DiskOtherText => Size(u => u.Other);

    /// <summary>"Backups · 6": how many there are, since that is what makes the folder big.</summary>
    public string DiskBackupsLabel => Localizer.Format("Disk_BackupsFmt", DiskUsage?.BackupCount ?? 0);

    /// <summary>"Mods" or "Plugins", as the server calls its add-ons.</summary>
    public string DiskContentLabel => Localizer.Get(ServerTypeCatalog.IsPluginBased(Config.Type) ? "Plugins" : "Mods");

    // Shares of the total, as star widths for the stacked bar (0..1) and as whole percentages.
    public double DiskWorldShare => Share(u => u.World);
    public double DiskBackupsShare => Share(u => u.Backups);
    public double DiskContentShare => Share(u => u.Content);
    public double DiskLogsShare => Share(u => u.Logs);
    public double DiskOtherShare => Share(u => u.Other);

    public string DiskWorldPercent => Percent(u => u.World);
    public string DiskBackupsPercent => Percent(u => u.Backups);
    public string DiskContentPercent => Percent(u => u.Content);
    public string DiskLogsPercent => Percent(u => u.Logs);
    public string DiskOtherPercent => Percent(u => u.Other);

    /// <summary>"Measured at 18:42", or that it is measuring now.</summary>
    public string DiskMeasuredText =>
        IsMeasuringDisk ? Localizer.Get("Disk_Measuring")
        : DiskMeasuredAt is { } at ? Localizer.Format("Disk_MeasuredFmt", at.ToString("t"))
        : HasDiskUsage ? "" : Localizer.Get("Disk_Missing");

    private string Size(Func<ServerDiskUsage, long> part) => DiskUsage is { } u ? ByteSize.Format(part(u)) : "—";

    private double Share(Func<ServerDiskUsage, long> part) =>
        DiskUsage is { Total: > 0 } u ? (double)part(u) / u.Total : 0;

    private string Percent(Func<ServerDiskUsage, long> part) =>
        DiskUsage is { Total: > 0 } ? $"{Math.Round(Share(part) * 100):0} %" : "";

    /// <summary>Measures again, now. The Storage page's button.</summary>
    [RelayCommand(CanExecute = nameof(CanMeasureDisk))]
    private Task MeasureDisk() => MeasureDiskAsync();

    private bool CanMeasureDisk() => !IsMeasuringDisk;

    /// <summary>
    /// Measures the folder in the background and publishes the result. A measurement already under
    /// way is dropped for the new one, so the last answer is always the newest.
    /// </summary>
    internal async Task MeasureDiskAsync()
    {
        _diskCts?.Cancel();
        var cts = _diskCts = new CancellationTokenSource();
        IsMeasuringDisk = true;
        try
        {
            var config = Config;
            var usage = await Task.Run(() => ServerDiskUsageService.Measure(config, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            DiskUsage = usage;
            DiskMeasuredAt = usage is null ? null : DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            // A newer measurement took over; it will publish.
        }
        catch (Exception ex)
        {
            CrashLog.Write("DiskUsage", ex);
        }
        finally
        {
            if (ReferenceEquals(_diskCts, cts)) IsMeasuringDisk = false;
        }
    }

    /// <summary>The answer may have changed: measures again, but only a server someone has looked at.</summary>
    private void RemeasureDiskIfShown()
    {
        if (DiskUsage is not null || IsMeasuringDisk) _ = MeasureDiskAsync();
    }

    private void InitDisk()
    {
        BackupCoordinator.CopyingChanged += copying => { if (!copying) RunOnUi(RemeasureDiskIfShown); };
        BackupCoordinator.RestoringChanged += () => RunOnUi(() =>
        {
            if (!IsRestoring) RemeasureDiskIfShown();
        });
    }
}
