using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The performance half of <see cref="ServerViewModel"/>: the card that says how a running server is
/// coping, and the one-line band that appears when it is not.
/// </summary>
/// <remarks>
/// The judging is <see cref="PerformanceMonitor"/>, which is pure; the asking is
/// <see cref="PerformanceProbe"/>. This part feeds them from the places the view model already
/// looks: the console, the stats timer, the backups and the memory measurements.
/// </remarks>
public partial class ServerViewModel
{
    private readonly PerformanceMonitor _perf = new();
    private PerformanceProbe? _probe;
    private int _probeTicks;
    private bool _bandDismissed;

    /// <summary>One probe in this many ticks of the 2 s stats timer: every 30 s.</summary>
    private const int ProbeEveryTicks = 15;

    /// <summary>The big figure on the Performance card: TPS when they can be measured, else a word.</summary>
    [ObservableProperty]
    private string _performanceText = "—";

    /// <summary>The line under it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPerformanceSubText))]
    private string _performanceSubText = "";

    /// <summary>The colour of the figure: green, amber or red.</summary>
    [ObservableProperty]
    private IBrush _performanceBrush = BrushGray;

    /// <summary>What the card's tooltip says.</summary>
    [ObservableProperty]
    private string _performanceTip = "";

    /// <summary>True while a sustained problem is on and the owner has not closed the band.</summary>
    [ObservableProperty]
    private bool _showPerformanceBand;

    /// <summary>The band's one line.</summary>
    [ObservableProperty]
    private string _performanceBandText = "";

    /// <summary>The band's colours: amber when tight, red when saturated.</summary>
    [ObservableProperty]
    private bool _performanceBandIsSevere;

    /// <summary>True when there is a line under the figure.</summary>
    public bool HasPerformanceSubText => PerformanceSubText.Length > 0;

    private void InitPerformance()
    {
        PerformanceTip = Localizer.Get("Perf_Tip");

        // Lag during a copy of the world is the copy's, so nothing is judged for its duration.
        BackupCoordinator.CopyingChanged += copying => RunOnUi(() =>
        {
            if (copying) _perf.BackupStarted();
            else _perf.BackupEnded(DateTime.UtcNow);
        });
    }

    /// <summary>The server finished starting: begins judging, after the grace period.</summary>
    private void BeginPerformanceWatch()
    {
        var now = DateTime.UtcNow;
        _perf.Ready(now, CurrentCeilingMb());
        _bandDismissed = false;
        _previewedCeilingMb = null;
        _probeTicks = 0;
        _probe = new PerformanceProbe(PerformanceProbe.Candidates(Config.Type, Config.GameVersion, SparkInstalled()));
        ApplyPerformance(now);
    }

    /// <summary>The server stopped.</summary>
    private void EndPerformanceWatch()
    {
        _perf.Stopped();
        _probe = null;
        PerformanceText = "—";
        PerformanceSubText = "";
        PerformanceBrush = BrushGray;
        ShowPerformanceBand = false;
    }

    /// <summary>True when a spark jar is in the folder the server loads its mods or plugins from.</summary>
    private bool SparkInstalled()
    {
        try
        {
            var folder = Path.Combine(Config.FolderPath, ServerTypeCatalog.ContentFolder(Config.Type));
            return Directory.Exists(folder)
                   && Directory.EnumerateFiles(folder, "spark*.jar").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Takes in the sample the stats timer just made and asks the server how it is doing when it is
    /// time to. Called with the same sample that feeds the CPU and RAM cards: sampling the process a
    /// second time would break the CPU figure, which is the difference between two samples.
    /// </summary>
    private void FeedPerformance(ProcessStatsService.Stats sample)
    {
        var now = DateTime.UtcNow;
        _perf.AddCpu(now, sample.CpuPercent);
        ApplyPerformance(now);
    }

    /// <summary>
    /// Asks the server for its speed every 30 s. Counted before the throttle that slows the other
    /// work down with the window in the tray: a server nobody is watching is exactly the one whose
    /// trouble the owner will want to hear about when they come back.
    /// </summary>
    private void ProbePerformance()
    {
        if (_probe is not { } probe || State != ServerState.Running) return;
        if (++_probeTicks % ProbeEveryTicks != 0) return;

        var now = DateTime.UtcNow;
        if (!_perf.IsJudging(now)) return;
        if (probe.Ask(now) is { } command) _process.SendCommand(command);
    }

    /// <summary>Offers a console line to the probe. True when it was the answer and must not be shown.</summary>
    private bool SwallowProbeAnswer(string line)
    {
        if (_probe is not { } probe) return false;
        if (!probe.TryConsume(line, DateTime.UtcNow, out var reading)) return false;
        if (reading is not null) RunOnUi(() => OnTickReading(reading));
        return true;
    }

    private void OnTickReading(TickReading reading)
    {
        var now = DateTime.UtcNow;
        _perf.AddTickReading(now, reading.Source, reading.Tps, reading.Mspt);
        _lastReading = reading;
        ApplyPerformance(now);
    }

    private TickReading? _lastReading;

    /// <summary>Notes the server's own "Can't keep up!" warning.</summary>
    private void WatchForLag(string line)
    {
        if (!line.Contains("keep up", StringComparison.Ordinal)) return;
        var parsed = PerformanceParsers.Parse(line);
        if (parsed.Kind != PerfLineKind.LagWarning) return;
        var behind = TimeSpan.FromMilliseconds(parsed.Numbers[0]);
        RunOnUi(() => _perf.AddLagWarning(DateTime.UtcNow, behind));
    }

    // --- The solutions page ---

    /// <summary>The page that replaces the tabs, or null while the tabs show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingSolutions), nameof(ShowingTabs))]
    private PerformanceSolutionsViewModel? _solutions;

    public bool ShowingSolutions => Solutions is not null;
    public bool ShowingTabs => Solutions is null;

    [RelayCommand]
    private void ShowPerformanceSolutions()
    {
        Solutions?.Dispose();
        Solutions = new PerformanceSolutionsViewModel(this, _perf.Evaluate(DateTime.UtcNow));
    }

    /// <summary>Back to the tabs.</summary>
    internal void CloseSolutions()
    {
        Solutions?.Dispose();
        Solutions = null;
    }

    internal IReadOnlyDictionary<string, string> ReadProperties() => _properties.Read(Config.PropertiesPath);

    internal void WriteProperties(IDictionary<string, string> changes) =>
        _properties.Update(Config.PropertiesPath, changes);

    /// <summary>The heap ceiling the server runs with, or would: the last start's, else the saved maximum.</summary>
    internal long CurrentCeilingMb() => MemoryPlan?.CeilingMb ?? Config.MaxRamGb * 1024L;

    /// <summary>True when the memory is the app's or the owner's numbers to change, not their own JVM arguments'.</summary>
    internal bool CanRaiseMemory => !JvmArgs.SetsMemory(Config.ExtraJvmArgs);

    internal bool IsManualMemory => MemoryProfile.Effective(Config) == MemoryMode.Manual;

    /// <summary>
    /// Raises the memory. Manual servers keep it as their maximum; Automatic ones get it once, at the
    /// next start, and go back to sizing themselves after.
    /// </summary>
    internal void RaiseMemory(long toMb)
    {
        if (IsManualMemory)
        {
            Config.MaxRamGb = (int)Math.Ceiling(toMb / 1024.0);
            ConfigChanged?.Invoke();
        }
        else
        {
            MemoryHistoryStore.For(Config.Id).SetNextStartBoost(PerformanceAdvice.RaiseByMb);
        }
    }

    /// <summary>True when the server ships spark itself (Paper and Purpur, from 1.21).</summary>
    internal bool SparkIsBundled =>
        Config.Type is ServerType.Paper or ServerType.Purpur
        && PerformanceProbe.VersionAtLeast(Config.GameVersion, 1, 21, 0);

    internal bool SparkAvailable => SparkIsBundled || SparkInstalled();

    /// <summary>True when some command can still tell how fast the server is.</summary>
    internal bool CanMeasureSpeed => _probe is { } probe
        ? probe.Current is not null
        : PerformanceProbe.Candidates(Config.Type, Config.GameVersion, SparkInstalled()).Count > 0;

    /// <summary>What the owner typed arrives next in the console; the probe stops waiting for its own answer.</summary>
    private void DisarmProbe() => _probe?.Disarm();

    [RelayCommand]
    private void DismissPerformanceBand()
    {
        _bandDismissed = true;
        ShowPerformanceBand = false;
    }

    private void ApplyPerformance(DateTime now)
    {
        if (State != ServerState.Running) return;
        var status = _perf.Evaluate(now);
        if (status.MemoryReason != MemoryPressureReason.None) _memTracker?.NotePressure();
        var reading = _lastReading;
        var tps = status.Tps;

        PerformanceBrush = status.Level switch
        {
            PerformanceLevel.Good => BrushGreen,
            PerformanceLevel.Tight => BrushAmber,
            PerformanceLevel.Saturated => BrushRed,
            _ => BrushGray
        };

        var word = status.Level switch
        {
            PerformanceLevel.Good => Localizer.Get("Perf_Good"),
            PerformanceLevel.Tight => Localizer.Get("Perf_Tight"),
            PerformanceLevel.Saturated => Localizer.Get("Perf_Saturated"),
            _ => Localizer.Get("Perf_Measuring")
        };

        if (status.Level == PerformanceLevel.Unknown)
        {
            PerformanceText = word;
            PerformanceSubText = "";
        }
        else if (tps is { } t)
        {
            // The figure is the TPS, because that is what people know; the word says how to read it.
            PerformanceText = string.Format(Localizer.Get("Perf_TpsFmt"), Math.Min(t, 20).ToString("0.0", CultureInfo.CurrentCulture));
            PerformanceSubText = reading?.Mspt is { } mspt
                ? string.Format(Localizer.Get("Perf_WordMsptFmt"), word, mspt.ToString("0.0", CultureInfo.CurrentCulture))
                : word;
        }
        else
        {
            PerformanceText = word;
            PerformanceSubText = status.Level == PerformanceLevel.Good ? "" : CauseText(status);
        }

        var problem = status.Level >= PerformanceLevel.Tight;
        PerformanceBandIsSevere = status.Level == PerformanceLevel.Saturated;
        PerformanceBandText = problem ? CauseText(status) : "";
        ShowPerformanceBand = problem && !_bandDismissed;
    }

    private long? _previewedCeilingMb;
    private bool _previewedAfterOom;

    /// <summary>
    /// What a start from now would give this server, now that it has been found short of memory: the
    /// number the band promises. Worked out with the same plan the start uses, once per cause.
    /// </summary>
    private long PreviewNextCeilingMb(bool outOfMemory)
    {
        if (_previewedCeilingMb is { } known && _previewedAfterOom == outOfMemory) return known;

        var raised = MemoryLearner.AfterPressure(CurrentCeilingMb(), outOfMemory);
        long next;
        try
        {
            var mods = MemoryProfile.CountMods(Config);
            next = MemoryProfile.Plan(new MemoryInputs(Config, _java.GetRuntime(Config.JavaPath), mods,
                SystemMemory.TotalMb(), MemoryBudget.ReservedByOthersMb(Config.Id), MemoryPreferences.DedicatedMachine,
                Learned: new LearnedCeiling(raised, MemoryCeilingReason.RaisedAfterPressure))).CeilingMb;
        }
        catch
        {
            next = raised;
        }
        _previewedAfterOom = outOfMemory;
        return (_previewedCeilingMb = next).Value;
    }

    /// <summary>The band's line for a server short of memory, with the memory it will have next time.</summary>
    private string MemoryCauseText(PerformanceStatus status)
    {
        // A Manual server's memory is the owner's to change; the page offers it.
        var outOfMemory = status.MemoryReason == MemoryPressureReason.OutOfMemory;
        if (IsManualMemory)
            return Localizer.Get(outOfMemory ? "Perf_CauseOom" : "Perf_CauseMemoryManual");
        var next = PreviewNextCeilingMb(outOfMemory);
        return string.Format(Localizer.Get(outOfMemory ? "Perf_CauseOomFmt" : "Perf_CauseMemoryFmt"),
            ByteSize.Format(next * 1024 * 1024));
    }

    /// <summary>The reason for the verdict, in a few words.</summary>
    private string CauseText(PerformanceStatus status) => status.Cause switch
    {
        PerformanceCause.OutOfMemory => MemoryCauseText(status),
        PerformanceCause.Ticks => status.Tps is { } tps
            ? string.Format(Localizer.Get("Perf_CauseTicksFmt"), Math.Min(tps, 20).ToString("0.0", CultureInfo.CurrentCulture))
            : Localizer.Get("Perf_CauseTicks"),
        PerformanceCause.LagWarnings => string.Format(Localizer.Get("Perf_CauseLagFmt"), status.LagWarnings),
        PerformanceCause.Cpu => Localizer.Get("Perf_CauseCpu"),
        PerformanceCause.Memory => MemoryCauseText(status),
        _ => ""
    };
}
