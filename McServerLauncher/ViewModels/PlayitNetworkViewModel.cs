using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>How the network looks as a whole, for the colour of its dot.</summary>
public enum PlayitNetworkLevel { Unknown, Ok, Warning, Bad }

/// <summary>One region's card on the Playit network page.</summary>
public sealed class PlayitRegionViewModel
{
    private static readonly IBrush Green = new ImmutableSolidColorBrush(Color.Parse(NotificationPalette.DefaultSuccess));
    private static readonly IBrush Amber = new ImmutableSolidColorBrush(Color.Parse(NotificationPalette.DefaultWarning));
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Color.Parse(NotificationPalette.DefaultError));
    private static readonly IBrush Grey = new ImmutableSolidColorBrush(Color.Parse("#8B949E"));

    public PlayitRegionViewModel(PlayitRegionResult result, bool isYours)
    {
        Result = result;
        IsYours = isYours;
    }

    public PlayitRegionResult Result { get; }
    public PlayitRegionState State => Result.State;

    /// <summary>The region this machine is routed to: marked so it stands out from the rest.</summary>
    public bool IsYours { get; }

    public string Name => Localizer.Get(Result.Region.NameKey);

    /// <summary>The short line at the right of the card: the ping, or what is wrong.</summary>
    public string Detail => State switch
    {
        PlayitRegionState.Pending => "…",
        PlayitRegionState.Down => Localizer.Get("PlayNet_NoAnswer"),
        PlayitRegionState.Rerouted when Result.ServedBy is { } by =>
            Localizer.Format("PlayNet_ViaFmt", Localizer.Get(by.NameKey)),
        _ => Result.Probe?.PingMs is { } ms ? PlayitNetworkViewModel.Ms(ms) : "—",
    };

    public string StateText => Localizer.Get(State switch
    {
        PlayitRegionState.Operational => "PlayNet_State_Ok",
        PlayitRegionState.Rerouted => "PlayNet_State_Rerouted",
        PlayitRegionState.Down => "PlayNet_State_Down",
        _ => "PlayNet_State_Pending",
    });

    public IBrush Brush => State switch
    {
        PlayitRegionState.Operational => Green,
        PlayitRegionState.Rerouted => Amber,
        PlayitRegionState.Down => Red,
        _ => Grey,
    };

    public bool HasProblemDetail => State is PlayitRegionState.Rerouted or PlayitRegionState.Down;
    public bool HasPlainDetail => !HasProblemDetail;

    /// <summary>The datacenter that answered and the jitter, for whoever wants the detail.</summary>
    public string Tip
    {
        get
        {
            var lines = new List<string> { StateText };
            if (Result.Probe is { DataCenterName: { Length: > 0 } dc })
                lines.Add(Localizer.Format("PlayNet_Tip_DcFmt", PlayitNetworkCatalog.CityOf(dc), dc));
            if (Result.Probe is { PingMs: { } ping, JitterMs: { } jitter })
                lines.Add(Localizer.Format("PlayNet_Tip_PingFmt", PlayitNetworkViewModel.Ms(ping),
                    PlayitNetworkViewModel.Ms(jitter, decimals: 1)));
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// Playit's network seen from this machine: a one-line summary for the tunnels screen, and the page
/// behind it with every region.
/// </summary>
/// <remarks>
/// It measures when the tunnels screen opens (at most once every few minutes) and when asked; never in
/// the background. The data and how a region is judged are <see cref="PlayitNetworkCatalog"/>.
/// </remarks>
public partial class PlayitNetworkViewModel : ObservableObject
{
    /// <summary>How long a measurement stands before opening the screen measures again.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>The page a friend with lag can open to see where they land and how fast.</summary>
    public const string PingTestUrl = "https://ping.playit.gg";

    private static readonly IBrush[] LevelBrushes =
    [
        new ImmutableSolidColorBrush(Color.Parse("#8B949E")),
        new ImmutableSolidColorBrush(Color.Parse(NotificationPalette.DefaultSuccess)),
        new ImmutableSolidColorBrush(Color.Parse(NotificationPalette.DefaultWarning)),
        new ImmutableSolidColorBrush(Color.Parse(NotificationPalette.DefaultError)),
    ];

    private readonly PlayitNetworkService _service = new();
    private readonly Func<string, Task> _copy;
    private readonly Dictionary<string, PlayitRegionResult> _results = new();
    private string? _yourSlug;

    public PlayitNetworkViewModel(Func<string, Task> copy)
    {
        _copy = copy;
        Rebuild();
    }

    public ObservableCollection<PlayitRegionViewModel> Regions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastMeasuredText))]
    private bool _isMeasuring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastMeasuredText))]
    private DateTime? _lastMeasured;

    public string LastMeasuredText => IsMeasuring
        ? Localizer.Get("PlayNet_Measuring")
        : LastMeasured is { } at ? Localizer.Format("PlayNet_MeasuredFmt", at.ToString("t", CultureInfo.CurrentCulture)) : "";

    /// <summary>The line on the tunnels screen.</summary>
    public string Summary => Describe(_results.Values.ToList(), YourCity, YourPing, IsMeasuring).Text;

    /// <summary>
    /// The summary without where this machine enters: the page says that on the line below, in full.
    /// </summary>
    public string Headline => Describe(_results.Values.ToList(), null, null, IsMeasuring).Text;

    public IBrush SummaryBrush => LevelBrushes[(int)Describe(_results.Values.ToList(), YourCity, YourPing, IsMeasuring).Level];

    /// <summary>Where this machine enters Playit, said at the top of the page.</summary>
    public string YouText => YourCity is { Length: > 0 } city
        ? Localizer.Format("PlayNet_YouExplainFmt", city,
            PlayitNetworkCatalog.BySlug(_yourSlug) is { } r ? Localizer.Get(r.NameKey) : city)
        : Localizer.Get("PlayNet_AnycastExplain");

    // The global anycast host is what a player near this machine reaches: its datacenter is "where you
    // enter", and its time is the one worth quoting.
    private PlayitRegionProbe? Anycast =>
        _results.TryGetValue("gl", out var gl) && gl.State != PlayitRegionState.Down ? gl.Probe : null;

    private string? YourCity => Anycast?.DataCenterName is { } dc ? PlayitNetworkCatalog.CityOf(dc) : null;
    private double? YourPing => Anycast?.PingMs;

    /// <summary>Measures again unless the last measurement is recent; what opening the screen does.</summary>
    public Task MeasureIfStaleAsync() =>
        LastMeasured is { } at && DateTime.Now - at < StaleAfter ? Task.CompletedTask : MeasureAsync();

    [RelayCommand]
    public async Task MeasureAsync()
    {
        if (IsMeasuring) return;
        IsMeasuring = true;
        Raise();
        try
        {
            var yours = _service.YourRegionAsync(CancellationToken.None);
            var offline = await _service.OfflineRegionsAsync(CancellationToken.None);
            _results.Clear();
            Rebuild();
            await _service.MeasureAsync((region, probe) => Dispatcher.UIThread.Post(() =>
            {
                _results[region.Code] = PlayitNetworkCatalog.Classify(region, probe, offline.Contains(region.Slug));
                Rebuild();
            }), CancellationToken.None);
            _yourSlug = await yours ?? YourSlugFromAnycast();
            LastMeasured = DateTime.Now;
        }
        finally
        {
            // Posted, so it lands after the last region's own post.
            Dispatcher.UIThread.Post(() =>
            {
                IsMeasuring = false;
                Rebuild();
            });
        }
    }

    // When Playit will not say where this machine is routed, the anycast datacenter tells.
    private string? YourSlugFromAnycast() =>
        Anycast?.DataCenterId is { } dc ? PlayitNetworkCatalog.HomeOf(dc)?.Slug : null;

    [RelayCommand]
    private Task CopyPingTest() => _copy(PingTestUrl);

    /// <summary>Puts results on screen without measuring. Public so the page can be checked offline.</summary>
    public void Show(IEnumerable<PlayitRegionResult> results, string? yourSlug)
    {
        _results.Clear();
        foreach (var r in results) _results[r.Region.Code] = r;
        _yourSlug = yourSlug;
        LastMeasured = DateTime.Now;
        Rebuild();
    }

    // Card by card, in place: clearing and refilling the grid for every region that comes in made the
    // whole page blink 22 times while it measured.
    private void Rebuild()
    {
        var regions = PlayitNetworkCatalog.Regions;
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var result = _results.TryGetValue(region.Code, out var r)
                ? r
                : new PlayitRegionResult(region, PlayitRegionState.Pending, null, null);
            var isYours = string.Equals(region.Slug, _yourSlug, StringComparison.OrdinalIgnoreCase);
            if (i < Regions.Count)
            {
                if (!ReferenceEquals(Regions[i].Result, result) || Regions[i].IsYours != isYours)
                    Regions[i] = new PlayitRegionViewModel(result, isYours);
            }
            else
            {
                Regions.Add(new PlayitRegionViewModel(result, isYours));
            }
        }
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(SummaryBrush));
        OnPropertyChanged(nameof(YouText));
        OnPropertyChanged(nameof(LastMeasuredText));
    }

    /// <summary>Milliseconds as the page writes them.</summary>
    internal static string Ms(double ms, int decimals = 0) =>
        Localizer.Format("PlayNet_MsFmt",
            Math.Round(ms, decimals).ToString(decimals == 0 ? "0" : "0.0", CultureInfo.CurrentCulture));

    /// <summary>The summary line and its colour, from what has been measured so far.</summary>
    /// <remarks>
    /// Names what is wrong rather than counting it while there is room — "everything works except
    /// Poland, which goes through Sweden" says more than "1 incident". Red is kept for the cases that
    /// touch this machine: nothing answering at all, or the anycast address itself down.
    /// </remarks>
    internal static (string Text, PlayitNetworkLevel Level) Describe(IReadOnlyList<PlayitRegionResult> results,
        string? yourCity, double? yourPing, bool measuring)
    {
        var done = results.Where(r => r.State != PlayitRegionState.Pending).ToList();
        if (done.Count == 0)
            return (Localizer.Get(measuring ? "PlayNet_Measuring" : "PlayNet_NotMeasured"), PlayitNetworkLevel.Unknown);
        if (!measuring && done.All(r => r.State == PlayitRegionState.Down))
            return (Localizer.Get("PlayNet_Unreachable"), PlayitNetworkLevel.Bad);

        var issues = done.Where(r => r.State is PlayitRegionState.Rerouted or PlayitRegionState.Down).ToList();
        string Name(PlayitRegionResult r) => Localizer.Get(r.Region.NameKey);

        var head = issues.Count switch
        {
            0 => Localizer.Get("PlayNet_AllOk"),
            1 when issues[0] is { State: PlayitRegionState.Rerouted, ServedBy: { } by } =>
                Localizer.Format("PlayNet_OneReroutedFmt", Name(issues[0]), Localizer.Get(by.NameKey)),
            1 => Localizer.Format("PlayNet_OneDownFmt", Name(issues[0])),
            _ => Localizer.Format("PlayNet_SomeIssuesFmt", issues.Count,
                string.Join(", ", issues.Take(3).Select(Name)) + (issues.Count > 3 ? "…" : "")),
        };

        var text = yourCity is { Length: > 0 } && yourPing is { } ping
            ? head + " · " + Localizer.Format("PlayNet_YouShortFmt", yourCity, Ms(ping))
            : head;

        var anycastDown = done.Any(r => r.Region.AcceptsAny && r.State == PlayitRegionState.Down);
        var level = anycastDown ? PlayitNetworkLevel.Bad
            : issues.Count > 0 ? PlayitNetworkLevel.Warning
            : PlayitNetworkLevel.Ok;
        return (text, level);
    }
}
