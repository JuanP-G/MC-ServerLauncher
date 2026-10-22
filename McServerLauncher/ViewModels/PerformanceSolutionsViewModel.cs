using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>One thing the Performance page offers, with the button that does it.</summary>
public sealed partial class SolutionRow : ObservableObject
{
    private readonly Func<Task<string?>> _apply;

    public SolutionRow(string title, string detail, string buttonText, Func<Task<string?>>? apply)
    {
        Title = title;
        Detail = detail;
        ButtonText = buttonText;
        _apply = apply ?? (() => Task.FromResult<string?>(null));
        CanApply = apply is not null;
    }

    public string Title { get; }
    public string Detail { get; }
    public string ButtonText { get; }

    /// <summary>The line that replaces the button once done (or why it could not be): empty until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState), nameof(ShowButton))]
    private string _state = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowButton))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowButton))]
    private bool _canApply;

    public bool HasState => State.Length > 0;
    public bool ShowButton => CanApply && !IsBusy && State.Length == 0;

    /// <summary>Does it. The task returns the line to show afterwards.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            State = await _apply() ?? "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Replaces what the row says with a message: it has nothing to offer.</summary>
    public void SetState(string text, bool canApply = false)
    {
        State = text;
        CanApply = canApply;
    }
}

/// <summary>
/// The page that replaces the server's tabs when its owner asks how to make it run lighter: settings
/// that cost less, memory if it is the problem and the machine can spare it, and mods that do not
/// change the game.
/// </summary>
/// <remarks>
/// Everything is a button that does it. Nothing takes effect until the server restarts, and the page
/// says so at the bottom with a button for it, because a change made on a running server that
/// appears to do nothing is the quickest way to make somebody stop trusting the page.
/// </remarks>
public sealed partial class PerformanceSolutionsViewModel : ObservableObject
{
    private readonly ServerViewModel _host;
    private readonly CancellationTokenSource _lifetime = new();

    /// <param name="host">The server the page is about.</param>
    /// <param name="status">The verdict that opened it.</param>
    /// <param name="totalRamMb">The machine's memory; read from the system when null (a test says its own).</param>
    /// <param name="availableRamMb">What is free; read from the system when null.</param>
    public PerformanceSolutionsViewModel(ServerViewModel host, PerformanceStatus status,
        long? totalRamMb = null, long? availableRamMb = null)
    {
        _host = host;
        Cause = status.Level == PerformanceLevel.Unknown ? "" : host.PerformanceBandText;
        Build(status, totalRamMb ?? SystemMemory.TotalMb(), availableRamMb ?? SystemMemory.AvailableMb());
    }

    /// <summary>Why the page was opened, in the band's words.</summary>
    public string Cause { get; }

    public bool HasCause => Cause.Length > 0;

    /// <summary>Changes to the server's settings and memory.</summary>
    public ObservableCollection<SolutionRow> Settings { get; } = new();

    /// <summary>Mods or plugins that lighten the server.</summary>
    public ObservableCollection<SolutionRow> Content { get; } = new();

    public bool HasSettings => Settings.Count > 0;
    public bool HasContent => Content.Count > 0;

    /// <summary>True when the server has nothing to install mods into (Vanilla).</summary>
    public bool IsVanilla => _host.Config.Type == ServerType.Vanilla;

    /// <summary>True when there is nothing at all to suggest.</summary>
    public bool NothingToSuggest => !HasSettings && !HasContent && !IsVanilla;

    public bool NeedsPlugins => ServerTypeCatalog.IsPluginBased(_host.Config.Type);

    public IAsyncRelayCommand RestartCommand => _host.RestartCommand;

    [RelayCommand]
    private void Back() => _host.CloseSolutions();

    /// <summary>Stops the lookups the page started.</summary>
    public void Shutdown() => _lifetime.Cancel();

    private void Build(PerformanceStatus status, long totalRamMb, long availableRamMb)
    {
        var config = _host.Config;
        var props = _host.ReadProperties();
        var sim = IntOf(props, "simulation-distance");
        var view = IntOf(props, "view-distance");
        var (paperView, paperSim) = ServerTypeCatalog.IsPluginBased(config.Type)
            ? PaperDistanceOverride.Read(config.FolderPath)
            : (false, false);

        var inputs = new AdviceInputs(config.Type, sim, view, _host.CurrentCeilingMb(),
            totalRamMb, availableRamMb, _host.CanMeasureSpeed, _host.SparkAvailable);
        var advice = PerformanceAdvice.For(status, inputs);

        foreach (var a in advice)
        {
            switch (a.Kind)
            {
                case AdviceKind.LowerSimulationDistance:
                    Settings.Add(DistanceRow("simulation-distance", a, paperSim, "Sol_SimFmt", "Sol_SimDetail"));
                    break;
                case AdviceKind.LowerViewDistance:
                    Settings.Add(DistanceRow("view-distance", a, paperView, "Sol_ViewFmt", "Sol_ViewDetail"));
                    break;
                case AdviceKind.RaiseMemory when _host.CanRaiseMemory:
                    Settings.Add(MemoryRow(a));
                    break;
            }
        }

        if (config.Type != ServerType.Vanilla)
            _ = LoadContentAsync(status, advice.Any(a => a.Kind == AdviceKind.InstallSpark));
    }

    private static int IntOf(IReadOnlyDictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : 10;

    private SolutionRow DistanceRow(string key, Advice advice, bool overriddenByPaper, string titleKey,
        string detailKey)
    {
        var title = string.Format(Localizer.Get(titleKey), advice.From, advice.To);
        var detail = Localizer.Get(detailKey);
        if (overriddenByPaper)
        {
            var blocked = new SolutionRow(title, detail, "", null);
            blocked.SetState(Localizer.Get("Sol_PaperOverride"));
            return blocked;
        }

        return new SolutionRow(title, detail, Localizer.Get("Sol_Apply"), () =>
        {
            _host.WriteProperties(new Dictionary<string, string> { [key] = advice.To.ToString()! });
            return Task.FromResult<string?>(Localizer.Get("Sol_AppliedRestart"));
        });
    }

    private SolutionRow MemoryRow(Advice advice)
    {
        var from = ByteSize.Format((advice.From ?? 0) * 1024 * 1024);
        var to = ByteSize.Format((advice.To ?? 0) * 1024 * 1024);
        var manual = _host.IsManualMemory;
        return new SolutionRow(
            string.Format(Localizer.Get("Sol_RamFmt"), from, to),
            Localizer.Get(manual ? "Sol_RamDetailManual" : "Sol_RamDetailAuto"),
            Localizer.Get("Sol_Apply"),
            () =>
            {
                _host.RaiseMemory(advice.To ?? 0);
                return Task.FromResult<string?>(Localizer.Get("Sol_AppliedRestart"));
            });
    }

    private async Task LoadContentAsync(PerformanceStatus status, bool includeSpark)
    {
        var mods = _host.Mods;
        var (ids, files) = mods.InstalledContent();
        var picks = PerformancePicks.Shared.Suggest(_host.Config.Type, status.Cause, ids, files,
            sparkBundled: _host.SparkIsBundled, includeSpark);

        // Created at once, in their order, saying they are being checked: the page does not jump
        // when the answers come in, and a pick with no build for this server just disappears.
        var rows = new List<(PerformancePick Pick, SolutionRow Row, VersionResultHolder Version)>();
        foreach (var pick in picks)
        {
            var summary = StoreSummaryService.Shared.CuratedBySlug(pick.Slug);
            var holder = new VersionResultHolder();
            var row = new SolutionRow(summary?.Title ?? pick.Slug, summary?.Text ?? "",
                Localizer.Get("Sol_Install"), () => InstallAsync(pick, holder));
            row.CanApply = false;
            row.SetState(Localizer.Get("Sol_Checking"));
            rows.Add((pick, row, holder));
            Content.Add(row);
        }
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(NothingToSuggest));

        foreach (var (pick, row, holder) in rows)
        {
            if (_lifetime.IsCancellationRequested) return;
            var version = await mods.FindCompatibleAsync(pick.Slug, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;

            if (version is null || version.Files.Count == 0)
            {
                Content.Remove(row);
                continue;
            }
            holder.Version = version;
            row.State = "";
            row.CanApply = true;
        }
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(NothingToSuggest));
    }

    private async Task<string?> InstallAsync(PerformancePick pick, VersionResultHolder holder)
    {
        var ok = await _host.Mods.InstallModAsync(pick.Slug, holder.Version);
        return Localizer.Get(ok ? "Sol_InstalledRestart" : "Sol_InstallFailed");
    }

    /// <summary>The build found for a pick, filled in once Modrinth answers.</summary>
    private sealed class VersionResultHolder
    {
        public McServerLauncher.Models.Modrinth.VersionResult? Version { get; set; }
    }
}
