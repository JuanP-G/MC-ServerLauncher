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

/// <summary>One line of a "most of what" list: a name, a count and its share of the total.</summary>
public sealed class StatBarViewModel
{
    public StatBarViewModel(StatEntry entry, long total)
    {
        Label = MinecraftIds.Pretty(entry.Id);
        CountText = entry.Count.ToString("N0", CultureInfo.CurrentUICulture);
        Percent = total > 0 ? entry.Count * 100.0 / total : 0;
        // One decimal, because the tail of a long list is all fractions of a percent and a column
        // of "0 %" would say nothing at all.
        PercentText = Percent.ToString("N1", CultureInfo.CurrentUICulture) + " %";
    }

    public string Label { get; }
    public string CountText { get; }

    /// <summary>Its share of the list it belongs to, 0 to 100, for the bar beside it.</summary>
    public double Percent { get; }

    public string PercentText { get; }
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

    /// <summary>How many rows of each "most of what" list are shown before "show more".</summary>
    private const int BarsShown = 10;

    private PlayerStats? _stats;
    private bool _allBars;
    private int _statsLoading;

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
        _ = LoadStatsAsync();
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

    [ObservableProperty] private bool _hasBlocks;
    [ObservableProperty] private string _minedTotalText = "";
    [ObservableProperty] private string _usedTotalText = "";
    [ObservableProperty] private bool _hasMoreBars;

    [ObservableProperty] private bool _hasCuriosities;
    [ObservableProperty] private string _statsJumps = "";
    [ObservableProperty] private string _statsDamageDealt = "";
    [ObservableProperty] private string _statsDamageTaken = "";
    [ObservableProperty] private string _statsWalked = "";
    [ObservableProperty] private string _statsFlown = "";
    [ObservableProperty] private string _statsElytra = "";
    [ObservableProperty] private string _statsBlocksPerHour = "";
    [ObservableProperty] private string _statsOresPerHour = "";
    [ObservableProperty] private string _statsTopKill = "";
    [ObservableProperty] private string _statsTopKiller = "";

    /// <summary>Blocks broken, by block. The server counts these exactly.</summary>
    public ObservableCollection<StatBarViewModel> MinedBlocks { get; } = new();

    /// <summary>Items used, by item — which for a block means placed. See <see cref="PlayerStats.Used"/>.</summary>
    public ObservableCollection<StatBarViewModel> UsedItems { get; } = new();

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

        _all = _store.Events(Name);
        ShowEvents();
    }

    /// <summary>
    /// Reads the server's own statistics for this player, off the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to happen inside <see cref="Reload"/>, on the UI thread — and <c>Reload</c> runs
    /// again on every console line about this player. That was tolerable when it read five numbers;
    /// it is not now that it walks every block the player has ever broken. So it moved here, beside
    /// the avatar, and a line arriving while a read is already in flight is dropped rather than
    /// queued: the read that is running will see the newer file anyway.
    /// </para>
    /// <para>
    /// Nothing is cleared while it loads. A profile that blanked its numbers for a moment on every
    /// chat message would be worse than one that is a second out of date.
    /// </para>
    /// </remarks>
    public async Task LoadStatsAsync()
    {
        if (Interlocked.Exchange(ref _statsLoading, 1) == 1) return;

        var folder = _serverFolder;
        var uuid = Uuid;
        try
        {
            var stats = await Task.Run(() => PlayerStatsReader.Read(folder, uuid));
            Dispatcher.UIThread.Post(() => ShowStats(stats));
        }
        catch
        {
            // The file belongs to the server; if it cannot be read, the profile simply has no
            // statistics card.
        }
        finally
        {
            Interlocked.Exchange(ref _statsLoading, 0);
        }
    }

    private void ShowStats(PlayerStats? stats)
    {
        _stats = stats;
        HasStats = stats is not null;
        HasBlocks = stats is not null && (stats.Mined.Count > 0 || stats.Used.Count > 0);
        HasCuriosities = stats is not null;
        if (stats is null) return;

        string Number(long n) => n.ToString("N0", CultureInfo.CurrentUICulture);
        string Km(long cm) => (cm / 100_000.0).ToString("N1", CultureInfo.CurrentUICulture) + " km";

        StatsPlayTime = RelativeTime.Duration(stats.PlayTime);
        StatsDeaths = Number(stats.Deaths);
        StatsMobKills = Number(stats.MobKills);
        StatsPlayerKills = Number(stats.PlayerKills);
        StatsDistance = Km(stats.DistanceCm);

        StatsJumps = Number(stats.Jumps);
        // Minecraft counts damage in tenths of a heart, so a heart is ten.
        StatsDamageDealt = (stats.DamageDealt / 10.0).ToString("N0", CultureInfo.CurrentUICulture);
        StatsDamageTaken = (stats.DamageTaken / 10.0).ToString("N0", CultureInfo.CurrentUICulture);
        StatsWalked = Km(stats.WalkedCm);
        StatsFlown = Km(stats.FlownCm);
        StatsElytra = Km(stats.ElytraCm);

        // Per hour played, which is the only way these compare between two players. They are
        // curiosities on a profile page, not a verdict about anybody.
        var hours = stats.PlayTime.TotalHours;
        StatsBlocksPerHour = hours >= 0.1 ? (stats.BlocksMined / hours).ToString("N0", CultureInfo.CurrentUICulture) : "—";
        StatsOresPerHour = hours >= 0.1 ? (stats.OresMined / hours).ToString("N1", CultureInfo.CurrentUICulture) : "—";

        StatsTopKill = Top(stats.Killed);
        StatsTopKiller = Top(stats.KilledBy);

        ShowBars();
    }

    private static string Top(IReadOnlyList<StatEntry> entries) =>
        entries.Count == 0
            ? "—"
            : MinecraftIds.Pretty(entries[0].Id)
              + " (" + entries[0].Count.ToString("N0", CultureInfo.CurrentUICulture) + ")";

    private void ShowBars()
    {
        if (_stats is not { } stats) return;

        var minedTotal = stats.BlocksMined;
        var usedTotal = stats.Used.Sum(e => e.Count);
        var take = _allBars ? int.MaxValue : BarsShown;

        MinedBlocks.Clear();
        foreach (var e in stats.Mined.Take(take)) MinedBlocks.Add(new StatBarViewModel(e, minedTotal));

        UsedItems.Clear();
        foreach (var e in stats.Used.Take(take)) UsedItems.Add(new StatBarViewModel(e, usedTotal));

        MinedTotalText = string.Format(Localizer.Get("PlayerDetails_BlocksTotalFmt"),
            minedTotal.ToString("N0", CultureInfo.CurrentUICulture));
        UsedTotalText = string.Format(Localizer.Get("PlayerDetails_BlocksTotalFmt"),
            usedTotal.ToString("N0", CultureInfo.CurrentUICulture));
        HasMoreBars = !_allBars && (stats.Mined.Count > BarsShown || stats.Used.Count > BarsShown);
    }

    /// <summary>Shows the whole of both lists instead of the first few.</summary>
    [RelayCommand]
    private void ShowAllBlocks()
    {
        _allBars = true;
        ShowBars();
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
        _ = LoadStatsAsync();
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
    private bool _hasLoadedOnce;
    private DispatcherTimer? _coalescer;

    /// <summary>How long requests to rebuild the list are gathered before one of them is obeyed.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

    public PlayerHistoryViewModel(ServerViewModel server) => _server = server;

    private PlayerHistoryStore Store => _store ??= PlayerHistoryStore.For(_server.Config.Id);

    /// <summary>
    /// One Reset per rebuild instead of one notification per player: the list is emptied and refilled
    /// whole, and every name in between is a state nobody needs to see.
    /// </summary>
    public BulkObservableCollection<PlayerRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private PlayerDetailsViewModel? _details;
    [ObservableProperty] private bool _isImporting;

    /// <summary>Whether the history is switched off in the settings, so the tab can say so.</summary>
    public bool IsDisabled => !PlayerHistoryPreferences.Current.Enabled;

    public bool HasDetails => Details is not null;

    partial void OnDetailsChanged(PlayerDetailsViewModel? value) => OnPropertyChanged(nameof(HasDetails));

    partial void OnSearchChanged(string value) => RequestRefresh();

    /// <summary>
    /// Builds the list the first time the Players tab is actually shown.
    /// </summary>
    /// <remarks>
    /// Until then nothing here is rebuilt, however much the server talks. Every running server used
    /// to rebuild its whole list on every join and every leave, selected or not, visible or not —
    /// a copy of every record, a sort, and a notification per row, for a tab that in most sessions
    /// is never opened at all.
    /// </remarks>
    public void EnsureLoaded()
    {
        if (_hasLoadedOnce) return;
        _hasLoadedOnce = true;
        Refresh();
    }

    /// <summary>
    /// Asks for a rebuild: soon, once, and only if there is a list on screen to be wrong.
    /// </summary>
    /// <remarks>
    /// Ten players joining at once is one rebuild, not ten. If the tab has never been opened there
    /// is nothing to rebuild: <see cref="EnsureLoaded"/> reads the current state when it is.
    /// </remarks>
    public void RequestRefresh()
    {
        if (!_hasLoadedOnce) return;

        _coalescer ??= CreateCoalescer();
        if (_coalescer.IsEnabled) return;
        _coalescer.Start();
    }

    private DispatcherTimer CreateCoalescer()
    {
        var timer = new DispatcherTimer { Interval = CoalesceWindow };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh();
        };
        return timer;
    }

    /// <summary>Stops the pending rebuild, if any. Mirrors nothing else: there is nothing to start.</summary>
    public void Shutdown() => _coalescer?.Stop();

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
                RequestRefresh();
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
            if (e.Kind is PlayerEventKind.Join or PlayerEventKind.Leave) RequestRefresh();
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
            Dispatcher.UIThread.Post(RequestRefresh);
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

        Rows.ReplaceAll(rows);
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
            onCleared: _ => RequestRefresh(),
            close: () => Details = null);
    }
}
