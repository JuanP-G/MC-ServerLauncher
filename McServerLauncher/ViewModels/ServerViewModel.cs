using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Represents a server in the UI: its configuration, live state, embedded
/// console, command sending and Playit integration.
/// </summary>
public partial class ServerViewModel : ObservableObject
{
    private const int MaxConsoleLines = 2000;
    private const int ConsoleTrimBlock = 200;

    private readonly ServerProcessManager _process = new();
    private readonly PlayitManager _playit = PlayitManager.Shared;
    private readonly PlayitAgentRunner _agent = PlayitAgentRunner.Shared;
    private readonly Action<PlayitState> _onPlayitStateChanged;
    private readonly Action<AgentRunState> _onAgentStateChanged;
    private readonly ProcessStatsService _stats = new();
    private readonly ServerPropertiesService _properties = new();
    private readonly PortService _ports = new();
    private readonly JavaService _java = new();
    private readonly PlayitApiService _playitApi = new();
    private readonly CrashReportService _crashReports = new();
    private readonly WorldBackupService _backups = new();
    private int _playitTickCounter;
    private readonly DispatcherTimer _statsTimer;

    /// <summary>Refreshes the idle countdown once a second. Runs only while one is on screen.</summary>
    private readonly DispatcherTimer _idleCountdownTimer;
    private readonly DispatcherTimer _playitTimer;

    // --- Auto-restart on crash ---
    // If the server exits on its own (not via the Stop button), it's relaunched automatically, up
    // to a limited number of consecutive attempts so a persistently-crashing server doesn't loop
    // forever. The streak resets whenever a run has been stable (Running) for a while, or the user
    // starts the server manually.
    private const int MaxAutoRestarts = 3;
    private static readonly TimeSpan StabilityWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AutoRestartDelay = TimeSpan.FromSeconds(5);
    private int _consecutiveCrashes;
    private DateTime? _lastRunningAtUtc;

    public ServerConfig Config { get; }

    /// <summary>Lines of the embedded console (server stdout/stderr + Playit).</summary>
    public BulkObservableCollection<string> ConsoleLines { get; } = new();

    /// <summary>
    /// The lines currently shown in the console UI: all of <see cref="ConsoleLines"/> when the
    /// filter box is empty, or the matching subset (kept in order, updated incrementally as new
    /// lines arrive) while a filter is typed.
    /// </summary>
    public BulkObservableCollection<string> VisibleConsoleLines { get; } = new();

    [ObservableProperty]
    private string _consoleFilter = string.Empty;

    partial void OnConsoleFilterChanged(string value) => RebuildVisibleConsole();

    private bool MatchesConsoleFilter(string line) =>
        string.IsNullOrWhiteSpace(ConsoleFilter)
        || line.Contains(ConsoleFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildVisibleConsole() =>
        VisibleConsoleLines.ReplaceAll(ConsoleLines.Where(MatchesConsoleFilter));

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private ServerState _state = ServerState.Stopped;

    [ObservableProperty]
    private PlayitState _playitState = PlayitState.Stopped;

    [ObservableProperty]
    private string _playitStatusText = Localizer.Get("Playit_Checking");

    [ObservableProperty]
    private string? _tunnelAddress;

    [ObservableProperty]
    private string _commandText = string.Empty;

    [ObservableProperty]
    private bool _isCommandHelpOpen;

    /// <summary>Common commands with their explanation (for the console help).</summary>
    public IReadOnlyList<CommandHelp> CommandHelp => SharedCommandHelp;

    /// <summary>Builds a localized help entry from a resx key pair (Cmd_X_Title / Cmd_X_Desc).</summary>
    private static CommandHelp Cmd(string insert, string key) =>
        new(insert, Localizer.Get(key + "_Title"), Localizer.Get(key + "_Desc"));

    private static readonly IReadOnlyList<CommandHelp> SharedCommandHelp = new List<CommandHelp>
    {
        Cmd("say ", "Cmd_Say"),
        Cmd("list", "Cmd_List"),
        Cmd("op ", "Cmd_Op"),
        Cmd("deop ", "Cmd_Deop"),
        Cmd("kick ", "Cmd_Kick"),
        Cmd("ban ", "Cmd_Ban"),
        Cmd("pardon ", "Cmd_Pardon"),
        Cmd("whitelist add ", "Cmd_WlAdd"),
        Cmd("whitelist remove ", "Cmd_WlRemove"),
        Cmd("gamemode ", "Cmd_Gamemode"),
        Cmd("tp ", "Cmd_Tp"),
        Cmd("give ", "Cmd_Give"),
        Cmd("time set ", "Cmd_TimeSet"),
        Cmd("weather ", "Cmd_Weather"),
        Cmd("difficulty ", "Cmd_Difficulty"),
        Cmd("gamerule ", "Cmd_Gamerule"),
        Cmd("seed", "Cmd_Seed"),
        Cmd("save-all", "Cmd_SaveAll"),
        Cmd("stop", "Cmd_Stop"),
    };

    [ObservableProperty]
    private string _statusText = Localizer.Get("Status_Stopped");

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _ramText = "—";

    [ObservableProperty]
    private string _uptimeText = "—";

    [ObservableProperty]
    private string _portText = "—";

    /// <summary>How long until the empty server stops itself, or null when nothing is counting.</summary>
    /// <remarks>
    /// Only set while the clock is actually running, so the UI can bind its visibility straight to
    /// this: an idle countdown showing "0:00" on a server nobody configured to stop would be a lie.
    /// </remarks>
    [ObservableProperty]
    private string? _idleCountdownText;

    /// <summary>True while there is a countdown to show.</summary>
    public bool HasIdleCountdown => IdleCountdownText is not null;

    partial void OnIdleCountdownTextChanged(string? value) => OnPropertyChanged(nameof(HasIdleCountdown));

    // --- CPU/RAM history for the mini charts (2 s sampling → 150 samples ≈ last 5 minutes) ---
    private const int MaxStatSamples = 150;
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _ramHistory = new();

    [ObservableProperty]
    private IReadOnlyList<double>? _cpuSeries;

    [ObservableProperty]
    private IReadOnlyList<double>? _ramSeries;

    // --- Minecraft server-list style view ---

    [ObservableProperty]
    private Bitmap? _serverIcon;

    public bool HasIcon => ServerIcon is not null;

    public ServerModsViewModel Mods { get; }

    public ServerBackupsViewModel Backups { get; }

    public bool IsModded => Config.Type != ServerType.Vanilla;

    /// <summary>Server type shown as a badge (Vanilla/Fabric/Forge, and any future type).</summary>
    public string ServerTypeText => Config.Type.ToString();

    /// <summary>Minecraft version (empty until known).</summary>
    public string GameVersionText => Config.GameVersion;

    /// <summary>Badge color per type (shared palette; unknown/future types fall back to gray).</summary>
    public IBrush ServerTypeBrush => ServerTypeBrushes.For(Config.Type);

    // --- State properties ---
    [ObservableProperty]
    private string _motdText = "A Minecraft Server";

    [ObservableProperty]
    private string _playerCountText = "0/20";

    private int _maxPlayers = 20;

    private readonly PlayersService _players = new();
    private readonly WakeOnDemandListener _wake = new();

    /// <summary>Players connected right now (live, read from the console).</summary>
    public ObservableCollection<string> ConnectedPlayers { get; } = new();

    /// <summary>Operators (ops.json).</summary>
    public ObservableCollection<string> OpPlayers { get; } = new();

    /// <summary>Banned players (banned-players.json).</summary>
    public ObservableCollection<string> BannedPlayers { get; } = new();

    /// <summary>Players who have ever joined (usercache.json).</summary>
    public ObservableCollection<string> KnownPlayers { get; } = new();

    // --- Whitelist ---

    private readonly WhitelistService _whitelist = new();

    /// <summary>Players currently in the whitelist (names).</summary>
    public ObservableCollection<string> WhitelistPlayers { get; } = new();

    [ObservableProperty]
    private bool _whitelistEnabled;

    [ObservableProperty]
    private string _newWhitelistName = string.Empty;

    // Colors per state (status text/dot and the card's signal bars).
    private static readonly IBrush BrushGreen = Frozen("#3FB950");
    private static readonly IBrush BrushSignalGreen = Frozen("#55FF55");
    private static readonly IBrush BrushAmber = Frozen("#E3A82B");
    private static readonly IBrush BrushRed = Frozen("#E05561");
    private static readonly IBrush BrushGray = Frozen("#6E7681");

    [ObservableProperty]
    private IBrush _statusBrush = BrushRed;

    [ObservableProperty]
    private IBrush _signalBrush = BrushGray;

    [ObservableProperty]
    private string _signalHint = Localizer.Get("Signal_Off");

    [ObservableProperty]
    private bool _showTunnelWarning;

    private static IBrush Frozen(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    /// <summary>Raised when something persistable about the server changes (to save).</summary>
    public event Action? ConfigChanged;

    public ServerViewModel(ServerConfig config)
    {
        Config = config;
        _name = config.Name;
        _tunnelAddress = config.TunnelAddress;

        _process.OutputReceived += OnConsoleLine;
        _process.StateChanged += OnServerStateChanged;
        _process.UnexpectedExit += OnUnexpectedExit;
        // Keep a reference to the handler: the manager is shared, so it must be unsubscribed
        // in ShutdownAsync or replaced view models would leak.
        // Both the legacy system service (PlayitManager) and our embedded agent (PlayitAgentRunner)
        // can drive the tunnel; the panel reflects whichever is in play (see EffectivePlayitState).
        _onPlayitStateChanged = _ => RunOnUi(RefreshPlayit);
        _onAgentStateChanged = _ => RunOnUi(RefreshPlayit);
        _playit.StateChanged += _onPlayitStateChanged;
        _agent.StateChanged += _onAgentStateChanged;

        _idleCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleCountdownTimer.Tick += (_, _) => UpdateIdleCountdown();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) => OnStatsTimerTick();

        // The Playit service runs in the background; we poll its state periodically, and the
        // tunnel address (via the playit API) less often.
        _playitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _playitTimer.Tick += OnPlayitTimerTick;
        _playitTimer.Start();
        _playit.RefreshState();
        // Sync directly: the shared manager/agent may already know the state (another view model
        // refreshed it before we subscribed), in which case no change event will fire.
        RefreshPlayit();

        RefreshPort();
        RefreshInfo();
        Mods = new ServerModsViewModel(config);
        Backups = new ServerBackupsViewModel(this);
        _ = RefreshTunnelAddressAsync();

        // Crossplay is a remembered setting, so it gets checked rather than assumed: the tunnel's
        // public port can change, and Geyser has to be re-pointed at it or the server quietly stops
        // being reachable from Bedrock.
        _ = RefreshBedrockAddressAsync();

        // Also here, not only on the transition to Stopped: opening the app finds every server
        // already stopped and fires no state change, so waiting for one would mean wake-on-demand
        // never starting until you had run and stopped a server by hand first.
        StartWakeListener();
    }

    // --- Tray-aware polling (EFI-2) ---
    // The app is designed to live in the tray with the window hidden; there's no point refreshing
    // stats text, sparklines and Playit status nobody can see at full cadence. When the window is
    // hidden the periodic work runs at 1/10th its usual rate (crash detection and the toasts that
    // matter in the tray are event-driven, not timer-driven, so they're unaffected). Everything
    // returns to full speed on the first tick after the window is shown again.

    /// <summary>True when the main window is hidden, i.e. minimized to the tray (see MainWindow).</summary>
    private static bool MainWindowHidden =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
            is not { IsVisible: true };

    private int _hiddenStatsTicks;
    private int _hiddenPlayitTicks;

    private void OnStatsTimerTick()
    {
        // Before the throttle below, on purpose: the whole point of stopping an empty server is to
        // stop it while nobody is watching, which is exactly when the window is in the tray.
        CheckIdleShutdown();

        if (MainWindowHidden && ++_hiddenStatsTicks % 10 != 0) return;
        UpdateStats();
    }

    // --- Stopping an empty server ---

    /// <summary>Since when nobody has been connected, or null while someone is on.</summary>
    private DateTime? _emptySinceUtc;

    /// <summary>Set while the idle stop is running, so one timeout can't queue several stops.</summary>
    private bool _idleStopping;

    /// <summary>
    /// Stops the server once it has been empty for <see cref="ServerConfig.IdleShutdownMinutes"/>.
    /// </summary>
    /// <remarks>
    /// The clock starts when the last player leaves — or when the server finishes starting, if
    /// nobody ever joins — and is reset by anyone connecting, so a server in use is never stopped
    /// out from under its players.
    /// </remarks>
    private void CheckIdleShutdown()
    {
        if (_idleStopping) return;

        var now = DateTime.UtcNow;
        var minutes = Config.IdleShutdownMinutes;
        var counting = ShouldCountIdle(minutes, State == ServerState.Running, ConnectedPlayers.Count)
                       && !IsWithinWakeGrace(_wokeAtUtc, now);
        if (!counting)
        {
            _emptySinceUtc = null;
            StopIdleCountdown();
            return;
        }

        _emptySinceUtc ??= now;
        UpdateIdleCountdown();
        _idleCountdownTimer.Start();    // no-op when it is already running

        if (!IsIdleLongEnough(minutes, _emptySinceUtc.Value, now)) return;

        _idleStopping = true;
        StopIdleCountdown();
        OnConsoleLine(string.Format(Localizer.Get("Msg_IdleShutdownFmt"), minutes));
        NotifyIf(NotificationKind.IdleShutdown,
            string.Format(Localizer.Get("Notif_IdleStopped"), minutes));
        _ = StopBecauseIdleAsync();
    }

    // --- Showing the countdown ---
    // This check runs off the 2 s stats timer, which is the right cadence for deciding when to stop
    // but the wrong one for a clock: a seconds display driven by it would skip every other second.
    // Rather than speed up the stats timer (its 2 s cadence is what makes the CPU/RAM history worth
    // ~5 minutes) the countdown gets its own, which only runs while there is something to count.

    private void UpdateIdleCountdown()
    {
        if (_emptySinceUtc is null) { StopIdleCountdown(); return; }

        IdleCountdownText = string.Format(
            Localizer.Get("Idle_CountdownFmt"),
            FormatCountdown(IdleRemaining(Config.IdleShutdownMinutes, _emptySinceUtc.Value, DateTime.UtcNow)));
    }

    private void StopIdleCountdown()
    {
        _idleCountdownTimer.Stop();
        IdleCountdownText = null;
    }

    /// <summary>How long is left before an empty server stops itself. Never negative.</summary>
    internal static TimeSpan IdleRemaining(int minutes, DateTime emptySinceUtc, DateTime nowUtc)
    {
        var left = TimeSpan.FromMinutes(minutes) - (nowUtc - emptySinceUtc);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// Formats the countdown, rounding seconds <em>up</em>.
    /// </summary>
    /// <remarks>
    /// Truncating would park the display on 0:00 for up to a second while the server is still
    /// perfectly alive, which reads as a stuck clock. Rounding up means it shows 0:01 until the
    /// moment it really is zero.
    /// </remarks>
    internal static string FormatCountdown(TimeSpan left)
    {
        var t = TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Whether the empty-server clock should be running at all.</summary>
    /// <remarks>
    /// Pulled out as a plain function so the rule can be checked directly. Getting it wrong means
    /// either a server that never stops or — far worse — one that stops while people are playing.
    /// </remarks>
    internal static bool ShouldCountIdle(int minutes, bool running, int playerCount) =>
        minutes > 0 && running && playerCount == 0;

    /// <summary>Whether it has been empty long enough to stop.</summary>
    internal static bool IsIdleLongEnough(int minutes, DateTime emptySinceUtc, DateTime nowUtc) =>
        nowUtc - emptySinceUtc >= TimeSpan.FromMinutes(minutes);

    // --- Waking the server when somebody knocks ---

    /// <summary>When the server was last started by someone trying to join, if ever.</summary>
    private DateTime? _wokeAtUtc;

    /// <summary>
    /// How long after waking the idle timer is ignored, so a player has time to actually get in.
    /// </summary>
    /// <remarks>
    /// Without this, a server set to stop after a minute would wake, find itself still empty while
    /// the world loads, and shut down before the player finished connecting — over and over.
    /// </remarks>
    private static readonly TimeSpan WakeGrace = TimeSpan.FromMinutes(5);

    internal static bool IsWithinWakeGrace(DateTime? wokeAtUtc, DateTime nowUtc) =>
        wokeAtUtc is not null && nowUtc - wokeAtUtc.Value < WakeGrace;

    /// <summary>Answers on the server's port while it is stopped, if the owner asked for it.</summary>
    private void StartWakeListener()
    {
        if (!Config.WakeOnDemand) { _wake.Stop(); return; }

        var port = _properties.GetServerPort(Config.PropertiesPath);
        if (port is null) return;

        if (!_wake.Start(port.Value, BuildWakeStatus, OnJoinAttempt))
            OnConsoleLine(string.Format(Localizer.Get("Msg_WakePortBusyFmt"), port.Value));
    }

    // --- How the notice looks in the server list ---
    // The leading reset matters as much as the colour. Minecraft carries formatting across a line
    // break, so without it the notice inherited whatever colour the owner's MOTD happened to end
    // on — gold under one server, plain grey under the next — and read as a third line of their own
    // message instead of as the launcher speaking.

    /// <summary>Bold yellow: off, and waiting for you to do something about it.</summary>
    private const string SleepingStyle = "§r§e§l";

    /// <summary>Bold green: already on its way up, nothing to do but wait.</summary>
    private const string StartingStyle = "§r§a§l";

    /// <summary>Yellow, not bold: the disconnect screen is several lines and bold shouts.</summary>
    private const string KickStyle = "§e";

    /// <summary>Builds the two-line server-list entry: the owner's MOTD, then the notice.</summary>
    /// <remarks>
    /// Only the owner's FIRST line is kept. The list shows two lines and no more, so a MOTD that
    /// already uses both would push the notice off the bottom — and the notice is the one line that
    /// has to be read for any of this to work.
    /// </remarks>
    internal static string ComposeWakeMotd(string? motd, string notice)
    {
        if (string.IsNullOrWhiteSpace(motd)) return notice;

        var first = motd.Split((char)10, (char)13)[0].TrimEnd();
        return first.Length == 0 ? notice : first + (char)10 + notice;
    }

    /// <summary>What a client sees while the server sleeps: its own MOTD plus what is going on.</summary>
    private WakeStatus BuildWakeStatus()
    {
        var starting = State != ServerState.Stopped;
        var line = (starting ? StartingStyle : SleepingStyle) +
                   Localizer.Get(starting ? "Wake_MotdStarting" : "Wake_MotdSleeping");
        var icon = Path.Combine(Config.FolderPath, "server-icon.png");

        return new WakeStatus(
            Description: ComposeWakeMotd(MotdText, line),
            VersionName: string.IsNullOrWhiteSpace(Config.GameVersion) ? "?" : Config.GameVersion,
            MaxPlayers: _maxPlayers,
            IconPath: File.Exists(icon) ? icon : null,
            DisconnectMessage: KickStyle + Localizer.Get(starting ? "Wake_KickStarting" : "Wake_KickWaking"));
    }

    /// <summary>Somebody pressed Join on a sleeping server.</summary>
    private void OnJoinAttempt() => RunOnUi(() =>
    {
        if (State != ServerState.Stopped) return;   // already coming up from an earlier knock

        _wokeAtUtc = DateTime.UtcNow;
        OnConsoleLine(Localizer.Get("Msg_WakeStarting"));
        NotifyIf(NotificationKind.WokeOnDemand, Localizer.Get("Notif_Woke"));

        // isAutoRestart: nobody is sitting in front of the app to answer a dialog, which is exactly
        // what that flag already means everywhere else.
        _ = StartInternal(isAutoRestart: true);
    });

    private async Task StopBecauseIdleAsync()
    {
        // Through the normal Stop, so an automatic shutdown saves the world exactly like a manual
        // one — the last thing anyone wants from an unattended stop is a missing backup.
        try { await Stop(); }
        catch (Exception ex) { OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message)); }
        finally
        {
            _idleStopping = false;
            _emptySinceUtc = null;
        }
    }

    private void OnPlayitTimerTick(object? sender, EventArgs e)
    {
        if (MainWindowHidden && ++_hiddenPlayitTicks % 10 != 0) return;

        _playit.RefreshState();
        // Every ~30 s (10 ticks of 3 s; ~5 min while in the tray) refresh the tunnel address.
        if (++_playitTickCounter % 10 == 0)
        {
            _ = RefreshTunnelAddressAsync();
            _ = RefreshBedrockAddressAsync();   // no-op unless this server does crossplay
        }
    }

    /// <summary>Gets the tunnel address from the playit API, matching by port.</summary>
    private async Task RefreshTunnelAddressAsync()
    {
        try
        {
            var port = _properties.GetServerPort(Config.PropertiesPath);
            if (!port.HasValue) return;

            var address = await _playitApi.GetAddressForPortAsync(port.Value);
            if (!string.IsNullOrEmpty(address))
                RunOnUi(() => TunnelAddress = address);
        }
        catch
        {
            // Best-effort: if the API fails, the saved/manual address is kept.
        }
    }

    /// <summary>
    /// The tunnel data-plane state as the UI should see it: our embedded agent when the user has
    /// connected their account (the model this app uses), otherwise the legacy system service.
    /// </summary>
    private PlayitState EffectivePlayitState => _agent.HasSecret
        ? _agent.State switch
        {
            AgentRunState.Running => PlayitState.Running,
            AgentRunState.Downloading or AgentRunState.Starting => PlayitState.Starting,
            _ => PlayitState.Stopped
        }
        : _playit.State;

    /// <summary>Recomputes the tunnel status/signal from whichever data-plane is in play.</summary>
    private void RefreshPlayit()
    {
        PlayitState = EffectivePlayitState;
        UpdatePlayitStatusText();
        UpdateSignal();
    }

    private void UpdatePlayitStatusText()
    {
        // Embedded-agent model: report what the agent is actually doing (this is what forwards
        // traffic — the system "service" is irrelevant here and mustn't say "not installed").
        if (_agent.HasSecret)
        {
            PlayitStatusText = _agent.State switch
            {
                AgentRunState.Running => Localizer.Get("Playit_Active"),
                AgentRunState.Downloading => Localizer.Get("Pk_Agent_Downloading"),
                AgentRunState.Starting => Localizer.Get("Status_Starting"),
                AgentRunState.Failed => Localizer.Get("Playit_AgentFailed"),
                AgentRunState.Unsupported => Localizer.Get("Pk_Agent_Unsupported"),
                _ => Localizer.Get("Playit_AgentStopped")
            };
            return;
        }

        PlayitStatusText = PlayitState switch
        {
            PlayitState.Running => Localizer.Get(_playit.IsInstalled ? "Playit_ActiveBg" : "Playit_Active"),
            PlayitState.Starting => Localizer.Get("Status_Starting"),
            _ => Localizer.Get(_playit.IsInstalled ? "Playit_Stopped" : "Playit_NotInstalled")
        };
    }

    /// <summary>
    /// Computes the "signal" (real reachability): green only if the server is running and, when
    /// using Playit, the tunnel is active. If running without a tunnel, red + warning.
    /// </summary>
    private void UpdateSignal()
    {
        var running = State == ServerState.Running;
        var transitioning = State is ServerState.Starting or ServerState.Stopping;

        if (!running)
        {
            SignalBrush = transitioning ? BrushAmber : BrushGray;
            SignalHint = Localizer.Get(transitioning ? "Signal_Transition" : "Signal_Off");
            ShowTunnelWarning = false;
            return;
        }

        if (Config.PlayitEnabled && PlayitState != PlayitState.Running)
        {
            SignalBrush = BrushRed;
            SignalHint = Localizer.Get("Signal_NoTunnel");
            ShowTunnelWarning = true;
        }
        else
        {
            SignalBrush = BrushSignalGreen;
            SignalHint = Localizer.Get("Signal_Accessible");
            ShowTunnelWarning = false;
        }
    }

    public bool IsRunning => State is ServerState.Running or ServerState.Starting or ServerState.Stopping;
    public bool CanStart => State == ServerState.Stopped;
    public bool CanStop => State is ServerState.Running or ServerState.Starting;

    partial void OnNameChanged(string value) => Config.Name = value;

    private void OnServerStateChanged(ServerState state) => RunOnUi(() =>
    {
        State = state;
        StatusText = state switch
        {
            ServerState.Stopped => Localizer.Get("Status_Stopped"),
            ServerState.Starting => Localizer.Get("Status_Starting"),
            ServerState.Running => Localizer.Get("Status_Running"),
            ServerState.Stopping => Localizer.Get("Status_Stopping"),
            _ => "?"
        };

        StatusBrush = state switch
        {
            ServerState.Running => BrushGreen,
            ServerState.Starting or ServerState.Stopping => BrushAmber,
            _ => BrushRed
        };
        UpdateSignal();

        if (state == ServerState.Running)
        {
            _stats.Reset();
            _statsTimer.Start();
            _lastRunningAtUtc = DateTime.UtcNow;
        }
        else if (state == ServerState.Stopped)
        {
            _statsTimer.Stop();

            // Explicitly, not just by waiting for the next CheckIdleShutdown: that runs off the
            // stats timer, which has just been stopped, so a server that went straight from Running
            // to Stopped between two ticks would leave a countdown ticking away on a dead server.
            _emptySinceUtc = null;
            StopIdleCountdown();

            CpuText = RamText = UptimeText = "—";
            _cpuHistory.Clear();
            _ramHistory.Clear();
            CpuSeries = null;
            RamSeries = null;
            ConnectedPlayers.Clear();
            UpdatePlayerCount();
            RefreshPlayers(); // the files (ops/banned/whitelist) may have changed
            StartWakeListener();
        }
        else if (state == ServerState.Starting)
        {
            ConnectedPlayers.Clear();
            UpdatePlayerCount();
        }

        NotifyCommandStates();
    });

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        SendCommandCommand.NotifyCanExecuteChanged();
    }

    private void OnConsoleLine(string line)
    {
        // Off the UI thread on purpose (OnConsoleLine itself is often called from a background
        // thread): file I/O shouldn't block RunOnUi's dispatch of the in-memory console update.
        ConsoleLogService.Shared.Log(Name, line);

        RunOnUi(() =>
        {
            ConsoleLines.Add(line);
            if (MatchesConsoleFilter(line))
                VisibleConsoleLines.Add(line);

            // Trim in blocks (EFI-4): one RemoveAt(0) per line was an O(n) shift plus a UI
            // notification for EVERY line once the cap was reached. Letting the list overshoot by
            // ConsoleTrimBlock and cutting back to the cap in a single bulk operation makes the
            // per-line cost amortized O(1), at the price of momentarily holding up to 2200 lines.
            if (ConsoleLines.Count > MaxConsoleLines + ConsoleTrimBlock)
            {
                ConsoleLines.RemoveFromStart(ConsoleLines.Count - MaxConsoleLines);
                RebuildVisibleConsole(); // the visible list is a subset; rebuild it from what survived
            }

            TrackPlayers(line);
        });
    }

    // Live connected players, read from the join/leave messages in the console.
    private void TrackPlayers(string line)
    {
        var joined = NameBefore(line, " joined the game");
        if (joined is not null)
        {
            if (!ConnectedPlayers.Contains(joined)) ConnectedPlayers.Add(joined);
            UpdatePlayerCount();
            NotifyIf(NotificationKind.PlayerJoined, string.Format(Localizer.Get("Notif_PlayerJoinedFmt"), joined));
            return;
        }

        var left = NameBefore(line, " left the game");
        if (left is not null)
        {
            ConnectedPlayers.Remove(left);
            UpdatePlayerCount();
            NotifyIf(NotificationKind.PlayerLeft, string.Format(Localizer.Get("Notif_PlayerLeftFmt"), left));
            return;
        }

        var death = DeathMessageDetector.Detect(line);
        if (death is not null)
            NotifyIf(NotificationKind.PlayerDeath, death);
    }

    /// <summary>
    /// Raises a toast for <paramref name="kind"/> if it's enabled (global + per-server settings) and
    /// nobody is looking at the app — if you're watching the console you already saw the line. The
    /// toast title is the server name, so it's always clear which server it came from.
    /// </summary>
    private void NotifyIf(NotificationKind kind, string message)
    {
        if (ToastService.MainWindowInactive && NotificationPreferences.ShouldNotify(Config, kind))
            ToastService.Shared.Notify(Name, message);
    }

    /// <summary>
    /// Extracts the player name right before a marker (e.g. " joined the game"), but only from a
    /// real server log entry: the name must be the ONLY text between the log prefix
    /// ("[…] [Server thread/INFO]: ", or Paper's "[… INFO]: ") and the marker, and must be a valid
    /// Minecraft name (letters/digits/underscore, 1-16 chars). A chat line quoting the phrase
    /// ("&lt;Bob&gt; Alice joined the game") keeps the sender tag in between, so it is rejected
    /// instead of faking a join/leave.
    /// </summary>
    private static string? NameBefore(string line, string marker)
    {
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx <= 0) return null;
        var head = line[..idx];
        var colon = head.LastIndexOf(": ", StringComparison.Ordinal);
        var name = colon >= 0 ? head[(colon + 2)..] : head;
        return PlayerNameRegex().IsMatch(name) ? name : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial System.Text.RegularExpressions.Regex PlayerNameRegex();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        _consecutiveCrashes = 0; // a deliberate Start gives auto-restart a fresh budget
        await StartInternal(isAutoRestart: false);
    }

    private async Task StartInternal(bool isAutoRestart)
    {
        // Judged fresh on every attempt: whether THIS run stays up long enough to "forgive" a
        // previous crash streak must not be based on a stale timestamp from an earlier run/session.
        _lastRunningAtUtc = null;

        try
        {
            // Before RefreshPort and the busy-port check below: while the server sleeps we are the
            // ones holding its port, and the check would offer to kill our own process.
            _wake.Stop();

            RefreshPort();
            RefreshInfo();

            // If the port is busy, offer to close the process holding it. Skipped during an
            // unattended auto-restart: nobody would be there to answer the confirmation dialog.
            var port = _properties.GetServerPort(Config.PropertiesPath);
            if (port.HasValue && _ports.IsPortInUse(port.Value))
            {
                if (isAutoRestart)
                {
                    OnConsoleLine(string.Format(Localizer.Get("Msg_AutoRestartPortBusyFmt"), port.Value));
                    return;
                }
                if (!await TryFreePortAsync(port.Value))
                    return;
            }

            // Make sure the configured Java is compatible with this server's version.
            await EnsureCompatibleJavaAsync();

            // Back up the world right before touching it again: the safety net that matters most,
            // since it covers every start path (manual, Restart, and auto-restart after a crash).
            if (Config.BackupsEnabled)
                await _backups.CreateBackupAsync(Config, "start", new Progress<string>(OnConsoleLine));

            _process.Start(Config);
            // Playit already runs as a background service: we don't launch another agent.
            Backups.RefreshCommand.Execute(null);
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
        }
    }

    /// <summary>
    /// Reacts to the server process ending on its own (crash, killed externally, JVM exit). Shows
    /// the crash-report reason if one was written, then relaunches automatically unless the crash
    /// streak has hit the limit (a run that stays healthy for <see cref="StabilityWindow"/> resets
    /// the streak, so a single occasional crash doesn't count against a persistently-crashing server).
    /// </summary>
    private void OnUnexpectedExit(int? exitCode) => RunOnUi(() => _ = HandleUnexpectedExitAsync(exitCode));

    private async Task HandleUnexpectedExitAsync(int? exitCode)
    {
        try
        {
            var reason = _crashReports.FindRecentCrashReason(Config.FolderPath, _process.StartedAtUtc);
            var codeText = exitCode?.ToString() ?? "?";
            OnConsoleLine(reason is not null
                ? string.Format(Localizer.Get("Msg_ServerCrashedReasonFmt"), codeText, reason)
                : string.Format(Localizer.Get("Msg_ServerCrashedFmt"), codeText));
            NotifyIf(NotificationKind.ServerCrashed, Localizer.Get("Notif_Crashed"));

            var stableRun = _lastRunningAtUtc is { } last && DateTime.UtcNow - last >= StabilityWindow;
            if (stableRun) _consecutiveCrashes = 0;
            _consecutiveCrashes++;

            if (_consecutiveCrashes > MaxAutoRestarts)
            {
                OnConsoleLine(string.Format(Localizer.Get("Msg_AutoRestartGaveUpFmt"), MaxAutoRestarts));
                NotifyIf(NotificationKind.AutoRestartGaveUp, Localizer.Get("Notif_GaveUp"));
                return;
            }

            OnConsoleLine(string.Format(Localizer.Get("Msg_AutoRestartingFmt"), _consecutiveCrashes, MaxAutoRestarts));
            await Task.Delay(AutoRestartDelay);

            // Only proceed if nothing else already started it in the meantime (e.g. the user
            // clicked Start manually right after the crash).
            if (CanStart)
                await StartInternal(isAutoRestart: true);
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
        }
    }

    /// <summary>
    /// Checks that the configured Java works for this server's version (read from the jar).
    /// If not, installs/uses the correct Java and saves it in the config.
    /// </summary>
    private async Task EnsureCompatibleJavaAsync()
    {
        var required = _java.GetRequiredJavaFromJar(Config.JarFullPath);

        // Modern Forge launches via an @args-file and Config.JarFullPath points to a server.jar
        // that doesn't exist, so the lookup above always comes back null for it — leaving exactly
        // the most fragile servers without the Java safety net. Derive the requirement from the
        // vanilla server jar the Forge installer keeps under libraries/, or (last resort, needs
        // network) from Mojang's manifest via GameVersion.
        if (required is null && !string.IsNullOrWhiteSpace(Config.ForgeArgs))
            required = _java.GetRequiredJavaFromForgeLibraries(Config.FolderPath, Config.GameVersion)
                ?? await GetRequiredJavaFromManifestAsync();

        if (required is null) return; // cannot be determined (old jar): don't block the start

        var current = _java.GetMajorVersion(Config.JavaPath);
        if (current > 0 && JavaService.IsCompatible(current, required.Value))
            return;

        OnConsoleLine(current > 0
            ? string.Format(Localizer.Get("Msg_NeedsJavaCurrentPreparing"), required, current)
            : string.Format(Localizer.Get("Msg_NeedsJavaPreparing"), required));
        try
        {
            var path = await _java.EnsureJavaAsync(required.Value, new Progress<string>(OnConsoleLine));
            if (!string.Equals(path, Config.JavaPath, StringComparison.OrdinalIgnoreCase))
            {
                Config.JavaPath = path;
                ConfigChanged?.Invoke();
                OnConsoleLine(string.Format(Localizer.Get("Msg_JavaConfigured"), path));
            }
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_JavaPrepareFailStart"), required, ex.Message));
        }
    }

    /// <summary>
    /// Last-resort Java lookup (used for modern Forge when the local libraries don't yield it):
    /// asks Mojang's manifest for the Java that <see cref="ServerConfig.GameVersion"/> needs — the
    /// same source used when the server was created. Null when offline or the version is unknown;
    /// the caller then simply skips the check rather than blocking the start.
    /// </summary>
    private async Task<int?> GetRequiredJavaFromManifestAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.GameVersion)) return null;
        try
        {
            var versions = new MinecraftVersionService();
            var (_, list) = await versions.GetVersionsAsync();
            var match = list.FirstOrDefault(v =>
                string.Equals(v.Id, Config.GameVersion, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            return (await versions.GetVersionDetailsAsync(match)).JavaMajor;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The port is busy: identifies the process and offers to close it. Returns true if it became free.
    /// </summary>
    private async Task<bool> TryFreePortAsync(int port)
    {
        var pid = _ports.GetListeningPid(port);
        string procDesc = Localizer.Get("Msg_OtherApp");
        if (pid.HasValue)
        {
            try { procDesc = $"\"{System.Diagnostics.Process.GetProcessById(pid.Value).ProcessName}\" (PID {pid})"; }
            catch { procDesc = $"PID {pid}"; }
        }

        var accepted = await MessageBox.ConfirmAsync(
            string.Format(Localizer.Get("Msg_PortBusyConfirm"), port, procDesc),
            Localizer.Get("Msg_PortBusyTitle"));

        if (!accepted)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_PortBusyNotStarted"), port, procDesc));
            return false;
        }

        try
        {
            if (pid.HasValue)
            {
                // The confirmation dialog may have stayed open for a while: re-check that the SAME
                // process still holds the port right before killing. If the original one died in
                // the meantime, the OS can reuse its PID — killing blindly (with entireProcessTree)
                // could take down an innocent process the user never approved.
                var currentPid = _ports.GetListeningPid(port);
                if (currentPid is null)
                {
                    // Nobody is listening anymore: nothing to kill, fall through to the free check.
                }
                else if (currentPid != pid)
                {
                    OnConsoleLine(string.Format(Localizer.Get("Msg_PortOwnerChangedFmt"), port));
                    return false;
                }
                else
                {
                    System.Diagnostics.Process.GetProcessById(pid.Value).Kill(entireProcessTree: true);
                    OnConsoleLine(string.Format(Localizer.Get("Msg_ClosedPortProcess"), port, procDesc));
                }
            }
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_CannotKill"), ex.Message));
            return false;
        }

        // Wait for the port to become free.
        for (var i = 0; i < 12 && _ports.IsPortInUse(port); i++)
            await Task.Delay(300);

        if (_ports.IsPortInUse(port))
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_PortStillBusy"), port));
            return false;
        }
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        try
        {
            await _process.StopAsync(TimeSpan.FromSeconds(30));

            // A snapshot of the good state just reached by stopping cleanly. Not done for Restart's
            // internal stop or for the app-closing ShutdownAsync: the next Start's own pre-backup
            // (or, when closing, simply not needing one) already covers those.
            if (Config.BackupsEnabled)
            {
                await _backups.CreateBackupAsync(Config, "stop", new Progress<string>(OnConsoleLine));
                Backups.RefreshCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Restart()
    {
        await _process.StopAsync(TimeSpan.FromSeconds(30));
        _consecutiveCrashes = 0; // a deliberate Restart gives auto-restart a fresh budget too
        await StartInternal(isAutoRestart: false);
    }

    private bool CanSend => IsRunning && !string.IsNullOrWhiteSpace(CommandText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SendCommand()
    {
        var cmd = CommandText.Trim();
        if (cmd.Length == 0) return;
        OnConsoleLine("> " + cmd);
        _process.SendCommand(cmd);
        CommandText = string.Empty;
    }

    partial void OnCommandTextChanged(string value) => SendCommandCommand.NotifyCanExecuteChanged();

    /// <summary>Puts the chosen help command into the box (ready to complete and send).</summary>
    [RelayCommand]
    private void UseCommand(CommandHelp? item)
    {
        if (item is null) return;
        CommandText = item.Insert;
        IsCommandHelpOpen = false;
    }

    [RelayCommand]
    private async Task TogglePlayit()
    {
        // Embedded-agent model: there's no system service to toggle — (re)start our own agent so a
        // failed/stopped tunnel comes back up. The app manages one agent for all the user's tunnels.
        if (_agent.HasSecret)
        {
            if (_agent.State is not (AgentRunState.Running or AgentRunState.Starting or AgentRunState.Downloading))
                await _agent.RetryAsync();
            UpdatePlayitStatusText();
            return;
        }

        if (!_playit.IsInstalled)
        {
            OnConsoleLine(Localizer.Get("Msg_PlayitServiceNotInstalled"));
            return;
        }

        try
        {
            if (_playit.IsRunning)
                await _playit.StopServiceAsync();
            else
                await _playit.StartServiceAsync();
            UpdatePlayitStatusText();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_PlayitServiceChangeFail"), ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(HasTunnelAddress))]
    private async Task CopyTunnelAddress()
    {
        if (string.IsNullOrEmpty(TunnelAddress)) return;
        var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top?.Clipboard is { } cb)
            await cb.SetTextAsync(TunnelAddress);
    }

    private bool HasTunnelAddress => !string.IsNullOrEmpty(TunnelAddress);

    partial void OnTunnelAddressChanged(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (Config.TunnelAddress != normalized)
        {
            Config.TunnelAddress = normalized;
            ConfigChanged?.Invoke();
        }
        CopyTunnelAddressCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearConsole()
    {
        ConsoleLines.Clear();
        VisibleConsoleLines.Clear();
    }

    /// <summary>
    /// Creates (if it doesn't exist) the Playit tunnel for this server's port, using the write
    /// key. Messages are shown in the server's console.
    /// </summary>
    public async Task CreateTunnelAsync(string writeKey)
    {
        var port = _properties.GetServerPort(Config.PropertiesPath);
        if (!port.HasValue)
        {
            OnConsoleLine(Localizer.Get("Msg_PortUnknown"));
            return;
        }

        try
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_CreatingTunnel"), port));
            var created = await _playitApi.EnsureMinecraftTunnelAsync(writeKey, Name, port.Value);
            OnConsoleLine(created
                ? Localizer.Get("Msg_TunnelCreated")
                : string.Format(Localizer.Get("Msg_TunnelExists"), port));
            await RefreshTunnelAddressAsync();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_TunnelCreateError"), ex.Message));
        }
    }

    // --- Crossplay: Java and Bedrock on the same server ---

    private readonly CrossplayService _crossplay = new();

    /// <summary>The Bedrock host and port, shown separately. Null when there is no Bedrock tunnel.</summary>
    [ObservableProperty]
    private string? _bedrockHost;

    [ObservableProperty]
    private string? _bedrockPortText;

    /// <summary>True once there is a Bedrock address worth showing.</summary>
    public bool HasBedrockAddress => !string.IsNullOrEmpty(BedrockHost);

    partial void OnBedrockHostChanged(string? value) => OnPropertyChanged(nameof(HasBedrockAddress));

    /// <summary>
    /// Installs Geyser and Floodgate, creates the Bedrock tunnel and points Geyser at it.
    /// </summary>
    /// <remarks>
    /// The order matters. The tunnel has to exist before the config is written, because the one
    /// value Geyser cannot work out for itself is the tunnel's public port — and writing the config
    /// first would leave it advertising the wrong one until something happened to rewrite it.
    /// </remarks>
    public async Task SetUpCrossplayAsync(string? playitKey)
    {
        if (!CrossplayService.CanEnable(Config.Type))
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_CrossplayUnsupportedFmt"), Config.Type));
            return;
        }

        try
        {
            if (Config.BedrockPort <= 0)
                Config.BedrockPort = _crossplay.PickBedrockPort(Array.Empty<int>());

            var log = new Progress<string>(OnConsoleLine);
            await _crossplay.InstallAsync(Config, log);

            int? publicPort = null;
            if (Config.PlayitEnabled && !string.IsNullOrEmpty(playitKey))
                publicPort = await EnsureBedrockTunnelAsync(playitKey!);

            _crossplay.WriteConfig(Config, publicPort);
            Config.CrossplayEnabled = true;

            await RefreshBedrockAddressAsync();
            OnConsoleLine(Localizer.Get("Msg_CrossplayReady"));
            ConfigChanged?.Invoke();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
        }
    }

    /// <summary>Creates the Bedrock (UDP) tunnel if it isn't there, and returns its public port.</summary>
    private async Task<int?> EnsureBedrockTunnelAsync(string playitKey)
    {
        OnConsoleLine(string.Format(Localizer.Get("Msg_CrossplayTunnelFmt"), Config.BedrockPort));

        await _playitApi.EnsureMinecraftTunnelAsync(
            playitKey, Name + " (Bedrock)", Config.BedrockPort,
            PlayitApiService.TunnelEdition.Bedrock);

        var tunnel = await FindBedrockTunnelAsync();
        return tunnel?.PublicPort > 0 ? tunnel.PublicPort : null;
    }

    /// <summary>The server's Bedrock tunnel, matched on both the local port and the protocol.</summary>
    /// <remarks>
    /// On protocol too, not just the port: a crossplay server has two tunnels, and matching on the
    /// number alone would pick the Java one whenever the two local ports happened to coincide.
    /// </remarks>
    private Task<PlayitApiService.PlayitTunnel?> FindBedrockTunnelAsync() =>
        _playitApi.GetTunnelAsync(Config.BedrockPort, udp: true);

    /// <summary>
    /// Refreshes the Bedrock address, and re-points Geyser if the tunnel's public port has moved.
    /// </summary>
    /// <remarks>
    /// The re-pointing is the reason crossplay is a remembered setting rather than a one-off
    /// action. If the tunnel is ever reassigned a different public port, a config written once and
    /// never revisited would keep advertising the old one, and the server would simply stop being
    /// joinable from Bedrock with nothing to explain why.
    /// </remarks>
    private async Task RefreshBedrockAddressAsync()
    {
        if (!Config.CrossplayEnabled || Config.BedrockPort <= 0) return;

        try
        {
            var tunnel = await FindBedrockTunnelAsync();
            if (tunnel?.Address is not { } host || tunnel.PublicPort <= 0) return;

            RunOnUi(() =>
            {
                BedrockHost = host;
                BedrockPortText = tunnel.PublicPort.ToString();
            });

            _crossplay.WriteConfig(Config, tunnel.PublicPort);
        }
        catch
        {
            // Best-effort, like the Java address: a failed lookup keeps whatever was shown.
        }
    }

    /// <summary>Copies the Bedrock host. The port is shown beside it, and typed separately.</summary>
    [RelayCommand]
    private async Task CopyBedrockAddress()
    {
        if (string.IsNullOrEmpty(BedrockHost)) return;
        var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top?.Clipboard is { } cb)
            await cb.SetTextAsync(BedrockHost!);
    }

    /// <summary>Opens the Playit.gg tunnels panel in the browser (to create/view tunnels).</summary>
    [RelayCommand]
    private void OpenPlayitDashboard()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://playit.gg/account/tunnels",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_BrowserError"), ex.Message));
        }
    }

    private void RefreshPort()
    {
        var port = _properties.GetServerPort(Config.PropertiesPath);
        PortText = port?.ToString() ?? "—";
    }

    partial void OnServerIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    /// <summary>Re-reads data from disk (after editing server.properties).</summary>
    public void RefreshFromDisk()
    {
        RefreshPort();
        RefreshInfo();
    }

    /// <summary>Reads MOTD, max players and the server icon (Minecraft-style view).</summary>
    private void RefreshInfo()
    {
        var props = _properties.Read(Config.PropertiesPath);
        MotdText = props.TryGetValue("motd", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "A Minecraft Server";
        _maxPlayers = props.TryGetValue("max-players", out var mp) && int.TryParse(mp, out var n) ? n : 20;
        UpdatePlayerCount();
        LoadIcon();
        RefreshPlayers();
    }

    private void RefreshWhitelist(IDictionary<string, string>? props = null)
    {
        props ??= _properties.Read(Config.PropertiesPath);
        WhitelistEnabled = props.TryGetValue("white-list", out var w)
                           && w.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        WhitelistPlayers.Clear();
        foreach (var name in _whitelist.ReadNames(Config.FolderPath))
            WhitelistPlayers.Add(name);
    }

    [RelayCommand]
    private async Task AddToWhitelist()
    {
        var name = ValidPlayerNameOrWarn(NewWhitelistName);
        if (name is null) return;

        try
        {
            if (_process.IsRunning)
            {
                OnConsoleLine($"> whitelist add {name}");
                _process.SendCommand($"whitelist add {name}");
                await Task.Delay(500);
            }
            else
            {
                var props = _properties.Read(Config.PropertiesPath);
                var online = !props.TryGetValue("online-mode", out var om)
                             || !om.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                await _whitelist.AddAsync(Config.FolderPath, name, online);
            }
            NewWhitelistName = string.Empty;
            RefreshWhitelist();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_WhitelistError"), ex.Message));
        }
    }

    [RelayCommand]
    private async Task RemoveFromWhitelist(string? name)
    {
        if ((name = ValidPlayerNameOrWarn(name)) is null) return;

        try
        {
            if (_process.IsRunning)
            {
                OnConsoleLine($"> whitelist remove {name}");
                _process.SendCommand($"whitelist remove {name}");
                await Task.Delay(500);
            }
            else
            {
                _whitelist.Remove(Config.FolderPath, name);
            }
            RefreshWhitelist();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_WhitelistError"), ex.Message));
        }
    }

    private void LoadIcon()
    {
        // The icon players see in the server list is server-icon.png (root, 64x64).
        var path = Path.Combine(Config.FolderPath, "server-icon.png");
        if (!File.Exists(path))
        {
            ServerIcon = null;
            return;
        }
        try
        {
            // Read fully into memory so the file isn't locked and updates are picked up.
            using var fs = File.OpenRead(path);
            ServerIcon = new Bitmap(fs);
        }
        catch
        {
            ServerIcon = null;
        }
    }

    private void UpdatePlayerCount() => PlayerCountText = $"{ConnectedPlayers.Count}/{_maxPlayers}";

    /// <summary>Reads ops.json, banned-players.json, usercache.json and the whitelist.</summary>
    [RelayCommand]
    private void RefreshPlayers()
    {
        ReplaceAll(OpPlayers, _players.ReadOps(Config.FolderPath));
        ReplaceAll(BannedPlayers, _players.ReadBanned(Config.FolderPath));
        ReplaceAll(KnownPlayers, _players.ReadKnown(Config.FolderPath));
        RefreshWhitelist();
    }

    private static void ReplaceAll(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private bool EnsureRunning(string action)
    {
        if (_process.IsRunning) return true;
        OnConsoleLine(string.Format(Localizer.Get("Msg_NeedRunningFor"), action));
        return false;
    }

    private async Task PlayerCommandAsync(string command)
    {
        OnConsoleLine("> " + command);
        _process.SendCommand(command);
        await Task.Delay(450);
        RefreshPlayers();
    }

    /// <summary>
    /// Validates a player name before it's embedded in a console command sent over stdin or
    /// written to the whitelist/ban files. Only real Minecraft names pass
    /// (letters/digits/underscore, 1-16 chars — the same rule <see cref="PlayerNameRegex"/>
    /// enforces for join/leave detection): this both blocks command injection (a "\n" would add a
    /// second console command) and confusing extra arguments (a name with spaces would turn
    /// "ban a b" into banning "a" with reason "b"). Returns the trimmed name, or null after
    /// logging a clear message when the input isn't a valid name.
    /// </summary>
    private string? ValidPlayerNameOrWarn(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (PlayerNameRegex().IsMatch(name)) return name;

        // Show what was rejected, defanged for display (no line breaks, capped length).
        var shown = name.Replace("\r", "").Replace("\n", " ");
        if (shown.Length > 32) shown = shown[..32] + "…";
        OnConsoleLine(string.Format(Localizer.Get("Msg_InvalidPlayerNameFmt"), shown));
        return null;
    }

    [RelayCommand]
    private async Task OpPlayer(string? name)
    {
        if ((name = ValidPlayerNameOrWarn(name)) is null || !EnsureRunning(Localizer.Get("Action_Op"))) return;
        await PlayerCommandAsync($"op {name}");
    }

    [RelayCommand]
    private async Task DeopPlayer(string? name)
    {
        if ((name = ValidPlayerNameOrWarn(name)) is null || !EnsureRunning(Localizer.Get("Action_Deop"))) return;
        await PlayerCommandAsync($"deop {name}");
    }

    [RelayCommand]
    private async Task KickPlayer(string? name)
    {
        if ((name = ValidPlayerNameOrWarn(name)) is null || !EnsureRunning(Localizer.Get("Action_Kick"))) return;
        await PlayerCommandAsync($"kick {name}");
    }

    [RelayCommand]
    private async Task BanPlayer(string? name)
    {
        if ((name = ValidPlayerNameOrWarn(name)) is null || !EnsureRunning(Localizer.Get("Action_Ban"))) return;
        await PlayerCommandAsync($"ban {name}");
    }

    [RelayCommand]
    private async Task PardonPlayer(string? name)
    {
        if ((name = ValidPlayerNameOrWarn(name)) is null) return;
        if (_process.IsRunning)
        {
            await PlayerCommandAsync($"pardon {name}");
        }
        else
        {
            _players.Unban(Config.FolderPath, name);
            RefreshPlayers();
        }
    }

    private void UpdateStats()
    {
        var sample = _stats.Sample(_process.CurrentProcess);
        if (sample is null)
        {
            CpuText = RamText = UptimeText = "—";
            return;
        }

        CpuText = $"{sample.CpuPercent:0.#} %";
        RamText = $"{sample.RamMb} MB";
        UptimeText = FormatUptime(sample.Uptime);

        // Feed the mini charts; snapshots because the Sparkline control re-renders per assignment.
        _cpuHistory.Add(sample.CpuPercent);
        _ramHistory.Add(sample.RamMb);
        if (_cpuHistory.Count > MaxStatSamples) _cpuHistory.RemoveAt(0);
        if (_ramHistory.Count > MaxStatSamples) _ramHistory.RemoveAt(0);
        CpuSeries = _cpuHistory.ToArray();
        RamSeries = _ramHistory.ToArray();
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s"
            : $"{t.Minutes}m {t.Seconds}s";

    /// <summary>Stops the server when the app closes. Does NOT touch the Playit service (keeps running in the background).</summary>
    public async Task ShutdownAsync()
    {
        _statsTimer.Stop();
        _idleCountdownTimer.Stop();
        _playitTimer.Stop();
        _playit.StateChanged -= _onPlayitStateChanged; // the manager is shared and outlives us
        _agent.StateChanged -= _onAgentStateChanged;   // the agent runner is shared too
        Mods.Shutdown();                               // cancels anything the store was fetching
        _wake.Stop();                                  // frees the port we answer on while asleep
        if (_process.IsRunning)
            await _process.StopAsync(TimeSpan.FromSeconds(15));
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
