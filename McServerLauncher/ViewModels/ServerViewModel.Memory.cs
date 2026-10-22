using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The memory half of <see cref="ServerViewModel"/>: what the server starts with, and the notice a
/// server moved to Automatic mode by the update shows.
/// </summary>
/// <remarks>
/// The decision itself is <see cref="MemoryProfile.Plan"/>, which is pure. This part gathers what it
/// needs, which means starting a JVM to ask what it is and whether it takes the flags, so it runs off
/// the UI thread, and it keeps the budget in step with the process.
/// </remarks>
public partial class ServerViewModel
{
    /// <summary>What the current, or last, run started with. Null before the first start.</summary>
    public MemoryPlan? MemoryPlan { get; private set; }

    /// <summary>True while the server runs Automatic without its owner having chosen it.</summary>
    public bool ShowMemoryNotice => MemoryProfile.ShowsMigrationNotice(Config);

    /// <summary>The notice's way back, with the numbers it goes back to.</summary>
    public string MemoryNoticeManualText =>
        string.Format(Localizer.Get("Mem_NoticeManualFmt"), Config.MinRamGb, Config.MaxRamGb);

    /// <summary>Stays Automatic, which is a choice now, so the notice goes.</summary>
    [RelayCommand]
    private void KeepAutomaticMemory()
    {
        Config.MemoryMode = MemoryMode.Automatic;
        ConfigChanged?.Invoke();
    }

    /// <summary>Goes back to the two numbers the server had, from its next start.</summary>
    [RelayCommand]
    private void ReturnToManualMemory()
    {
        Config.MemoryMode = MemoryMode.Manual;
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Decides what this start gets, and the console lines that say so. Off the UI thread: it may
    /// start a JVM twice, once to read what it is and once to check it takes the flags.
    /// </summary>
    private (MemoryPlan Plan, List<string> Notes) PlanMemory()
    {
        var config = Config;
        var java = _java.GetRuntime(config.JavaPath);
        var mods = MemoryProfile.CountMods(config);
        var plan = MemoryProfile.Plan(new MemoryInputs(config, java, mods, SystemMemory.TotalMb(),
            MemoryBudget.ReservedByOthersMb(config.Id), MemoryPreferences.DedicatedMachine));

        _runJavaMajor = java.Major;
        _runModCount = mods;

        var notes = new List<string>();
        if (plan.Mode == MemoryMode.Manual) return (WithGcLog(plan, java), notes);

        var dropped = false;
        while (!_java.AcceptsFlags(config.JavaPath, plan.Flags) && MemoryProfile.WithoutLastOptionalFlag(plan) is { } fewer)
        {
            plan = fewer;
            dropped = true;
        }
        // Only said when the smaller set is one the Java took: a Java that does not run at all fails
        // every check, and its own error says far more than this would.
        if (dropped && _java.AcceptsFlags(config.JavaPath, plan.Flags))
            notes.Add(Localizer.Get("Msg_MemoryFlagsFallback"));

        notes.Add(string.Format(Localizer.Get("Msg_MemoryPlanFmt"),
            ByteSize.Format(plan.CeilingMb * 1024 * 1024), ReasonText(plan.Reason, mods)));
        if (plan.CappedTo32Bit)
            notes.Add(Localizer.Get("Msg_Java32BitCap"));
        return (WithGcLog(plan, java), notes);
    }

    /// <summary>The plan with the GC log flag after its memory flags, when the Java takes it.</summary>
    private MemoryPlan WithGcLog(MemoryPlan plan, JavaService.JavaRuntime java)
    {
        var log = GcLogging.FlagsFor(Config, java);
        return log.Count == 0 ? plan : plan with { Flags = plan.Flags.Concat(log).ToList() };
    }

    /// <summary>Why the ceiling is what it is, in a few words for the console and the settings.</summary>
    internal static string ReasonText(MemoryCeilingReason reason, int modCount) => reason switch
    {
        MemoryCeilingReason.KeptPrevious => Localizer.Get("Mem_ReasonKept"),
        MemoryCeilingReason.Budget => Localizer.Get("Mem_ReasonBudget"),
        MemoryCeilingReason.Floor => Localizer.Get("Mem_ReasonFloor"),
        _ when modCount > 0 => string.Format(Localizer.Get("Mem_ReasonModsFmt"), modCount),
        _ => Localizer.Get("Mem_ReasonNoMods")
    };

    // --- Measuring ---------------------------------------------------------------------------------
    // The JVM writes a GC log; this reads it as the run goes, and keeps what it says in the server's
    // memory history when the run ends. All of it is for the later phases to decide with; none of it
    // changes how a server starts.

    private MemorySessionTracker? _memTracker;
    private GcLogTail? _gcTail;
    private Task _gcPump = Task.CompletedTask;
    private volatile bool _sawOutOfMemory;
    private int _runJavaMajor;
    private int _runModCount;

    /// <summary>The line under the RAM figure: what Java has in use and what it holds, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRamDetail))]
    private string _ramDetailText = "";

    /// <summary>True when there is a Java reading to show under the RAM figure.</summary>
    public bool HasRamDetail => RamDetailText.Length > 0;

    /// <summary>Starts measuring the run that is about to launch.</summary>
    private void BeginMemoryRun(MemoryPlan plan)
    {
        var props = _properties.Read(Config.PropertiesPath);
        _sawOutOfMemory = false;
        _memTracker = new MemorySessionTracker(new MemorySessionStart(
            DateTime.UtcNow, plan.Mode, plan.CeilingMb, _runJavaMajor, _runModCount,
            Distance(props, "view-distance"), Distance(props, "simulation-distance")));
        _gcTail = plan.Flags.Any(f => f.StartsWith("-Xlog:gc:file=", StringComparison.Ordinal))
            ? new GcLogTail(GcLogging.LogPath(Config.FolderPath))
            : null;
        _gcPump = Task.CompletedTask;
    }

    private static int Distance(IReadOnlyDictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : 10;

    /// <summary>Gives up on a run whose process never started.</summary>
    private void AbandonMemoryRun()
    {
        _memTracker = null;
        _gcTail = null;
    }

    /// <summary>One look at the GC log, off the UI thread, and what it says shown on the card.</summary>
    private void PumpGcLog()
    {
        if (_memTracker is not { } tracker || _gcTail is not { } tail || !_gcPump.IsCompleted) return;
        _gcPump = ReadGcLogAsync(tracker, tail);
    }

    private async Task ReadGcLogAsync(MemorySessionTracker tracker, GcLogTail tail)
    {
        var lines = await Task.Run(tail.ReadNewLines);
        Apply(tracker, lines);
        if (ReferenceEquals(tracker, _memTracker)) ShowJavaHeap(tracker);
    }

    private static void Apply(MemorySessionTracker tracker, IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (GcLogLine.TryParse(line, out var gc)) tracker.Add(gc);
            else if (GcLogLine.TryParseCollector(line, out var collector)) tracker.NoteCollector(collector);
        }
    }

    private void ShowJavaHeap(MemorySessionTracker tracker) =>
        RamDetailText = tracker.CommittedMb > 0
            ? string.Format(Localizer.Get("Mem_RamJavaFmt"),
                ByteSize.Format(tracker.LiveMb * 1024 * 1024), ByteSize.Format(tracker.CommittedMb * 1024 * 1024))
            : "";

    /// <summary>Notes a console line that says the server ran out of memory.</summary>
    private void WatchForOutOfMemory(string line)
    {
        if (!line.Contains("java.lang.OutOfMemoryError", StringComparison.Ordinal)) return;
        _sawOutOfMemory = true;
        RunOnUi(_perf.OutOfMemory);
    }

    /// <summary>
    /// Ends the run being measured: reads what the log still has, and keeps the run in the history
    /// if it taught anything. Safe to call twice; the second call finds nothing to do.
    /// </summary>
    private async Task FinishMemoryRunAsync(SessionEnd end)
    {
        if (_memTracker is not { } tracker) return;
        var tail = _gcTail;
        var pump = _gcPump;
        var oom = _sawOutOfMemory;
        _memTracker = null;
        _gcTail = null;
        RamDetailText = "";

        try
        {
            await pump;
            if (tail is not null) Apply(tracker, await Task.Run(tail.ReadNewLines));
            if (oom) tracker.NoteOutOfMemory();
            if (!tracker.HasData) return;

            var session = tracker.Finish(DateTime.UtcNow, end);
            var id = Config.Id;
            await Task.Run(() => MemoryHistoryStore.For(id).Add(session));
        }
        catch
        {
            // What the history keeps is for tuning; failing to write it is not worth a message.
        }
    }
}
