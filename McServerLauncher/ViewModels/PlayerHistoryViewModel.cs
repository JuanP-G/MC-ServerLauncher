using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>"Hace 3 h", and the other ways of saying how long ago something was.</summary>
public static class RelativeTime
{
    /// <summary>How long ago <paramref name="utc"/> was, in words; a date once it is weeks old.</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span < TimeSpan.FromMinutes(1)) return Localizer.Get("Time_JustNow");
        if (span < TimeSpan.FromHours(1)) return string.Format(Localizer.Get("Time_MinutesAgoFmt"), (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return string.Format(Localizer.Get("Time_HoursAgoFmt"), (int)span.TotalHours);
        if (span < TimeSpan.FromDays(30)) return string.Format(Localizer.Get("Time_DaysAgoFmt"), (int)span.TotalDays);
        return utc.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture);
    }

    /// <summary>A date and time in the user's own format.</summary>
    public static string Exact(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture);

    /// <summary>A length of time played: "12 h 5 min", or "5 min".</summary>
    public static string Duration(TimeSpan span)
    {
        var hours = (long)span.TotalHours;
        return hours > 0
            ? string.Format(Localizer.Get("Time_HoursMinutesFmt"), hours, span.Minutes)
            : string.Format(Localizer.Get("Time_MinutesFmt"), span.Minutes);
    }
}

/// <summary>One player in the list: name, whether they are on now, and when they were last seen.</summary>
public sealed class PlayerRowViewModel
{
    public PlayerRowViewModel(string name, bool isOnline, DateTime? lastSeenUtc, DateTime nowUtc)
    {
        Name = name;
        IsOnline = isOnline;
        LastSeenUtc = lastSeenUtc;
        LastSeenText = isOnline
            ? Localizer.Get("History_OnlineNow")
            : lastSeenUtc is { } t ? RelativeTime.Ago(t, nowUtc) : Localizer.Get("History_NeverRecorded");
        LastSeenTip = lastSeenUtc is { } exact ? RelativeTime.Exact(exact) : null;
    }

    public string Name { get; }
    public bool IsOnline { get; }
    public DateTime? LastSeenUtc { get; }
    public string LastSeenText { get; }
    public string? LastSeenTip { get; }
}

/// <summary>One line of a player's activity.</summary>
public sealed class PlayerEventRowViewModel
{
    public PlayerEventRowViewModel(StoredEvent e)
    {
        Kind = e.Kind;
        TimeText = RelativeTime.Exact(e.At);
        Icon = e.Kind switch
        {
            PlayerEventKind.Join => "→",
            PlayerEventKind.Leave => "←",
            PlayerEventKind.Chat => "💬",
            PlayerEventKind.Death => "☠",
            _ => "★"
        };
        Text = e.Kind switch
        {
            PlayerEventKind.Join => Localizer.Get("History_EventJoin"),
            PlayerEventKind.Leave => Localizer.Get("History_EventLeave"),
            PlayerEventKind.Advancement => string.Format(Localizer.Get("History_EventAdvancementFmt"), e.Text),
            _ => e.Text ?? ""
        };
        IsChat = e.Kind == PlayerEventKind.Chat;
    }

    public PlayerEventKind Kind { get; }
    public string TimeText { get; }
    public string Icon { get; }
    public string Text { get; }
    public bool IsChat { get; }
}

/// <summary>Which of a player's events the profile shows.</summary>
public enum PlayerEventFilter { All, Connections, Chat }

/// <summary>
/// A player's profile: everything the app and the server know about them.
/// </summary>
/// <remarks>
/// Put together when it is opened and not kept up to date on its own; the list above it calls
/// <see cref="Reload"/> when something about this player happens while it is on screen.
/// </remarks>
public sealed partial class PlayerDetailsViewModel : ObservableObject
{
    private const int PageSize = 100;

    private readonly PlayerHistoryStore _store;
    private readonly string _serverFolder;
    private readonly Func<bool> _isOnlineNow;
    private IReadOnlyList<StoredEvent> _all = Array.Empty<StoredEvent>();
    private int _shown = PageSize;

    public PlayerDetailsViewModel(string name, PlayerHistoryStore store, string serverFolder,
        Func<bool> isOnline, bool isOp, bool isWhitelisted, bool isBanned, string? uuidFromCache,
        ICommand op, ICommand kick, ICommand ban, Action<string> onCleared, Action close)
    {
        Name = name;
        _store = store;
        _serverFolder = serverFolder;
        _isOnlineNow = isOnline;
        IsOp = isOp;
        IsWhitelisted = isWhitelisted;
        IsBanned = isBanned;
        _uuidFromCache = uuidFromCache;
        OpCommand = op;
        KickCommand = kick;
        BanCommand = ban;
        _onCleared = onCleared;
        _close = close;

        Reload();
        _ = LoadAvatarAsync();
    }

    private readonly string? _uuidFromCache;
    private readonly Action<string> _onCleared;
    private readonly Action _close;

    public string Name { get; }
    public bool IsOp { get; }
    public bool IsWhitelisted { get; }
    public bool IsBanned { get; }

    public ICommand OpCommand { get; }
    public ICommand KickCommand { get; }
    public ICommand BanCommand { get; }

    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private string? _uuid;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _firstSeenText = "—";
    [ObservableProperty] private string _lastSeenText = "—";
    [ObservableProperty] private string _sessionsText = "—";
    [ObservableProperty] private string _playedText = "—";
    [ObservableProperty] private string _messagesText = "—";

    [ObservableProperty] private bool _hasStats;
    [ObservableProperty] private string _statsPlayTime = "";
    [ObservableProperty] private string _statsDeaths = "";
    [ObservableProperty] private string _statsMobKills = "";
    [ObservableProperty] private string _statsPlayerKills = "";
    [ObservableProperty] private string _statsDistance = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll), nameof(IsFilterConnections), nameof(IsFilterChat))]
    private PlayerEventFilter _filter;

    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _confirmingClear;

    public bool IsFilterAll => Filter == PlayerEventFilter.All;
    public bool IsFilterConnections => Filter == PlayerEventFilter.Connections;
    public bool IsFilterChat => Filter == PlayerEventFilter.Chat;

    public ObservableCollection<PlayerEventRowViewModel> Events { get; } = new();

    /// <summary>Reads everything again: the summary, the server's statistics, and the events.</summary>
    public void Reload()
    {
        var now = DateTime.UtcNow;
        var rec = _store.Get(Name);

        IsOnline = _isOnlineNow();
        Uuid = rec?.Uuid ?? _uuidFromCache;
        FirstSeenText = rec?.FirstSeen is { } first ? RelativeTime.Exact(first) : "—";
        LastSeenText = IsOnline
            ? Localizer.Get("History_OnlineNow")
            : rec?.LastSeen is { } last ? $"{RelativeTime.Ago(last, now)} · {RelativeTime.Exact(last)}" : "—";
        SessionsText = rec is null ? "—" : rec.Sessions.ToString(CultureInfo.CurrentUICulture);
        var played = TimeSpan.FromSeconds(rec?.SecondsPlayed ?? 0);
        if (rec?.OpenSince is { } open && now > open) played += now - open;   // the session in progress counts
        PlayedText = rec is null ? "—" : RelativeTime.Duration(played);
        MessagesText = rec is null ? "—" : rec.Messages.ToString(CultureInfo.CurrentUICulture);

        var stats = PlayerStatsReader.Read(_serverFolder, Uuid);
        HasStats = stats is not null;
        if (stats is not null)
        {
            StatsPlayTime = RelativeTime.Duration(stats.PlayTime);
            StatsDeaths = stats.Deaths.ToString("N0", CultureInfo.CurrentUICulture);
            StatsMobKills = stats.MobKills.ToString("N0", CultureInfo.CurrentUICulture);
            StatsPlayerKills = stats.PlayerKills.ToString("N0", CultureInfo.CurrentUICulture);
            StatsDistance = (stats.DistanceCm / 100_000.0).ToString("N1", CultureInfo.CurrentUICulture) + " km";
        }

        _all = _store.Events(Name);
        ShowEvents();
    }

    partial void OnFilterChanged(PlayerEventFilter value)
    {
        _shown = PageSize;
        ShowEvents();
    }

    private void ShowEvents()
    {
        var matching = _all.Where(e => Filter switch
        {
            PlayerEventFilter.Connections => e.Kind is PlayerEventKind.Join or PlayerEventKind.Leave,
            PlayerEventFilter.Chat => e.Kind == PlayerEventKind.Chat,
            _ => true
        }).ToList();

        Events.Clear();
        foreach (var e in matching.Take(_shown)) Events.Add(new PlayerEventRowViewModel(e));
        HasMore = matching.Count > _shown;
        IsEmpty = matching.Count == 0;
    }

    [RelayCommand] private void ShowAll() => Filter = PlayerEventFilter.All;
    [RelayCommand] private void ShowConnections() => Filter = PlayerEventFilter.Connections;
    [RelayCommand] private void ShowChat() => Filter = PlayerEventFilter.Chat;

    [RelayCommand]
    private void ShowMore()
    {
        _shown += PageSize;
        ShowEvents();
    }

    [RelayCommand] private void Back() => _close();

    /// <summary>First click asks, second click forgets: it cannot be undone.</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        if (!ConfirmingClear)
        {
            ConfirmingClear = true;
            return;
        }
        ConfirmingClear = false;
        _store.Clear(Name);
        _onCleared(Name);
        Reload();
    }

    [RelayCommand] private void CancelClear() => ConfirmingClear = false;

    /// <summary>The player's face, from their skin. Nothing is shown if it cannot be fetched.</summary>
    private async Task LoadAvatarAsync()
    {
        var bitmap = await ImageCache.GetAsync($"https://mc-heads.net/avatar/{Uri.EscapeDataString(Name)}/64");
        if (bitmap is not null) Dispatcher.UIThread.Post(() => Avatar = bitmap);
    }
}

/// <summary>
/// The Players tab's history: everyone who has been on this server, and each one's profile.
/// </summary>
/// <remarks>
/// <para>
/// The store is opened the first time it is needed, not when the view model is built: a view model
/// built for a test, or for a server nobody opens, must not start writing into the user's app data.
/// </para>
/// <para>
/// Recording happens on the thread the server's output arrives on, so writing a line to disk never
/// holds up the console on the UI thread.
/// </para>
/// </remarks>
public sealed partial class PlayerHistoryViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private PlayerHistoryStore? _store;
    private int _started;

    public PlayerHistoryViewModel(ServerViewModel server) => _server = server;

    private PlayerHistoryStore Store => _store ??= PlayerHistoryStore.For(_server.Config.Id);

    public ObservableCollection<PlayerRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private PlayerDetailsViewModel? _details;
    [ObservableProperty] private bool _isImporting;

    /// <summary>Whether the history is switched off in the settings, so the tab can say so.</summary>
    public bool IsDisabled => !PlayerHistoryPreferences.Current.Enabled;

    public bool HasDetails => Details is not null;

    partial void OnDetailsChanged(PlayerDetailsViewModel? value) => OnPropertyChanged(nameof(HasDetails));

    partial void OnSearchChanged(string value) => Refresh();

    /// <summary>
    /// Once per run of the app: drops what the settings no longer allow, and imports the server's
    /// old logs if that was never done. In the background, since a server with years of logs takes
    /// a moment.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (!PlayerHistoryPreferences.Current.Enabled) return;

        IsImporting = !Store.Imported;
        var folder = _server.Config.FolderPath;
        var running = _server.IsRunning;
        _ = Task.Run(() =>
        {
            try
            {
                Store.Prune();
                PlayerLogImporter.ImportOnce(folder, Store, running);
            }
            catch
            {
                // Best-effort: the tab still works with whatever is there.
            }
            Dispatcher.UIThread.Post(() =>
            {
                IsImporting = false;
                Refresh();
            });
        });
    }

    /// <summary>
    /// Records a line of the server's output, if it is about a player. Called on the output's thread.
    /// </summary>
    public void OnServerLine(string line, IReadOnlySet<string> online)
    {
        if (!PlayerHistoryPreferences.Current.Enabled) return;

        if (PlayerEventParser.UuidOf(line) is { } uuid)
        {
            Store.RecordUuid(uuid.Player, uuid.Uuid);
            return;
        }

        if (PlayerEventParser.Parse(line, online) is not { } e) return;

        Store.Record(e, DateTime.UtcNow);

        Dispatcher.UIThread.Post(() =>
        {
            if (e.Kind is PlayerEventKind.Join or PlayerEventKind.Leave) Refresh();
            if (Details is { } d && string.Equals(d.Name, e.Player, StringComparison.OrdinalIgnoreCase)) d.Reload();
        });
    }

    /// <summary>The server stopped: whoever was still on it has left, whatever the log said.</summary>
    public void OnServerStopped()
    {
        if (_store is null) return;
        var store = _store;
        _ = Task.Run(() => store.CloseOpenSessions(DateTime.UtcNow));
    }

    /// <summary>The settings changed: the limits may be tighter now, and the tab may need to say it is off.</summary>
    public void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(IsDisabled));
        if (_store is null) return;
        var store = _store;
        _ = Task.Run(() =>
        {
            store.Prune();
            Dispatcher.UIThread.Post(Refresh);
        });
    }

    /// <summary>
    /// Rebuilds the list: everyone in the history, everyone the server itself remembers
    /// (usercache.json), and whoever is on right now. Connected first, then by last seen.
    /// </summary>
    public void Refresh()
    {
        var now = DateTime.UtcNow;
        var online = _server.ConnectedPlayers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only a store something has already opened: refreshing the list alone never creates one.
        var records = _store?.Players() ?? (IReadOnlyList<PlayerRecord>)Array.Empty<PlayerRecord>();

        var byName = new Dictionary<string, PlayerRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
            byName[r.Name] = new PlayerRowViewModel(r.Name, online.Contains(r.Name), r.LastSeen, now);
        foreach (var name in _server.KnownPlayers.Concat(online))
            if (!byName.ContainsKey(name))
                byName[name] = new PlayerRowViewModel(name, online.Contains(name), null, now);

        var search = Search.Trim();
        var rows = byName.Values
            .Where(r => search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.IsOnline)
            .ThenByDescending(r => r.LastSeenUtc ?? DateTime.MinValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
    }

    [RelayCommand]
    private void Open(PlayerRowViewModel? row)
    {
        if (row is null) return;
        var name = row.Name;
        Details = new PlayerDetailsViewModel(
            name, Store, _server.Config.FolderPath,
            isOnline: () => _server.ConnectedPlayers.Contains(name, StringComparer.OrdinalIgnoreCase),
            isOp: _server.OpPlayers.Contains(name, StringComparer.OrdinalIgnoreCase),
            isWhitelisted: _server.WhitelistPlayers.Contains(name, StringComparer.OrdinalIgnoreCase),
            isBanned: _server.BannedPlayers.Contains(name, StringComparer.OrdinalIgnoreCase),
            uuidFromCache: new PlayersService().UuidOf(_server.Config.FolderPath, name),
            op: _server.OpPlayerCommand, kick: _server.KickPlayerCommand, ban: _server.BanPlayerCommand,
            onCleared: _ => Refresh(),
            close: () => Details = null);
    }
}
