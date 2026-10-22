using System.Collections.Generic;
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

        var notes = new List<string>();
        if (plan.Mode == MemoryMode.Manual) return (plan, notes);

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
        return (plan, notes);
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
}
