using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
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
public partial class ServerViewModel : ObservableObject, IBackupHost, IPlayerAdminHost
{

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
    private int _playitTickCounter;
    private readonly DispatcherTimer _statsTimer;

    /// <summary>Stops the server once it has been empty long enough, and shows the countdown.</summary>
    private readonly IdleShutdownController _idle;

    /// <summary>Answers on the port while the server sleeps and wakes it when somebody knocks.</summary>
    private readonly WakeOnDemandController _wake;

    /// <summary>The checks, and the offers to fix them, that come before the JVM is launched.</summary>
    private readonly StartPreflight _preflight;

    /// <summary>What an owner can do to the players: op, ban, kick, whitelist.</summary>
    private readonly PlayerAdmin _admin;

    /// <summary>Finds the public address of this server's Playit tunnel, and makes the tunnel.</summary>
    private readonly TunnelAddressTracker _tunnel;
    private readonly DispatcherTimer _playitTimer;
    private readonly DispatcherTimer _autoBackupTimer;

    /// <summary>Backups, restores, and the moments the JVM starts and stops writing the world.</summary>
    internal BackupCoordinator BackupCoordinator { get; }

    /// <summary>
    /// Counts the times this server was shut down. A start that was still getting ready when the app
    /// began closing finds it changed and does not launch a JVM nobody would then be watching.
    /// </summary>
    private int _shutdowns;

    /// <summary>When the clock will next consider an automatic backup; see the coordinator.</summary>
    /// <remarks>
    /// Only once the server is up: the clock starts then. While it starts or stops — minutes, for a
    /// big modpack — the last time it was set is from an earlier run, or never, and the tab
    /// announced a "next backup" that was already in the past.
    /// </remarks>
    internal DateTime? NextAutoBackupUtc => BackupCoordinator.NextAutoBackupUtc(State == ServerState.Running);

    /// <summary>
    /// How long a server gets to save and exit after "stop" before it is killed.
    /// </summary>
    /// <remarks>
    /// One value for every way a server is stopped. Closing the app (and updating it, and deleting a
    /// server) used to allow fifteen seconds where the Stop button allowed thirty, so the most common
    /// way of stopping a server was the one most likely to kill it halfway through writing its world —
    /// and a large modpack can easily take longer than either to save. Killing is the last resort,
    /// so the wait errs long: a server that has finished saving exits on its own long before this.
    /// </remarks>
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    // --- Auto-restart on crash ---
    // If the server exits on its own (not via the Stop button), it's relaunched automatically, up
    // to a limited number of consecutive attempts so a persistently-crashing server doesn't loop
    // forever. The streak resets whenever a run has been stable (Running) for a while, or the user
    // starts the server manually.
    private const int MaxAutoRestarts = 3;
    private static readonly TimeSpan StabilityWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AutoRestartDelay = TimeSpan.FromSeconds(5);
    private int _consecutiveCrashes;

    /// <summary>
    /// Whether this run has already explained a Bedrock player being kicked for having no mods.
    /// </summary>
    private bool _moddedKickWarned;

    /// <summary>Whether this run has already explained the server refusing to start from its path.</summary>
    private bool _pathRejectionWarned;

    /// <summary>Set when the server failed for a reason that repeating the start cannot change.</summary>
    private bool _startFailureIsFinal;

    /// <summary>
    /// Somebody asked for a stop that is still going: waiting for a backup to finish reading the
    /// world, or for the server to exit. A crash meanwhile is not restarted — they asked for it off.
    /// </summary>
    private bool _stopRequested;
    private DateTime? _lastRunningAtUtc;

    public ServerConfig Config { get; }

    /// <summary>The lines of the console, what is shown after the filters, and how they arrive.</summary>
    private readonly ConsoleBuffer _console = new();

    /// <summary>Lines of the embedded console (server stdout/stderr + the launcher's own).</summary>
    public BulkObservableCollection<ConsoleLine> ConsoleLines => _console.Lines;

    /// <summary>
    /// The lines currently shown in the console UI: all of <see cref="ConsoleLines"/> when nothing
    /// is filtered, or the matching subset (kept in order, updated incrementally as new lines
    /// arrive) while a search is typed or a category is switched off.
    /// </summary>
    public BulkObservableCollection<ConsoleLine> VisibleConsoleLines => _console.Visible;

    [ObservableProperty]
    private string _consoleFilter = string.Empty;

    partial void OnConsoleFilterChanged(string value) => _console.Filter = value;

    /// <summary>One switch per kind of line, in the order they appear along the top of the console.</summary>
    public IReadOnlyList<ConsoleKindFilter> ConsoleKinds => _console.Kinds;

    /// <summary>True while anything at all is being hidden, so the UI can offer to undo it.</summary>
    public bool IsConsoleFiltered => _console.IsFiltered;

    /// <summary>Switches every category back on and clears the search.</summary>
    [RelayCommand]
    private void ShowAllConsoleKinds()
    {
        _console.ShowAllKinds();
        ConsoleFilter = string.Empty;
    }

    /// <summary>Repaints the console and its switches after the colours change in the settings.</summary>
    public void RefreshConsoleColours() => _console.RefreshColours();

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

    /// <summary>How far along the public address is. Drives the line under the Playit row.</summary>
    [ObservableProperty]
    private TunnelAddressState _tunnelState = TunnelAddressState.Waiting;

    partial void OnTunnelStateChanged(TunnelAddressState value) =>
        OnPropertyChanged(nameof(TunnelStateText));

    /// <summary>One line saying what is happening, so the box is never blank without a reason.</summary>
    public string TunnelStateText => Localizer.Get(TunnelAddressStates.KeyFor(TunnelState));

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

    /// <summary>The connection test on the network page, kept with its last result.</summary>
    public ConnectionTestViewModel ConnectionTest => _connectionTest ??= new ConnectionTestViewModel(this);

    private ConnectionTestViewModel? _connectionTest;

    /// <summary>Everyone who has been on this server, and each one's profile.</summary>
    public PlayerHistoryViewModel History { get; }

    public bool IsModded => Config.Type != ServerType.Vanilla;

    /// <summary>Server type shown as a badge (Vanilla/Fabric/Forge, and any future type).</summary>
    public string ServerTypeText => Config.Type.ToString();

    /// <summary>Minecraft version (empty until known).</summary>
    public string GameVersionText => Config.GameVersion;

    /// <summary>Badge color per type (shared palette; unknown/future types fall back to gray).</summary>
    public IBrush ServerTypeBrush => ServerTypeBrushes.For(Config.Type);

    // --- State properties ---
    [ObservableProperty]
    private string _motdText = ServerPropertiesService.DefaultMotd;

    [ObservableProperty]
    private string _playerCountText = "0/20";

    private int _maxPlayers = 20;

    private readonly PlayersService _players = new();

    /// <summary>Players connected right now (live, read from the console).</summary>
    public BulkObservableCollection<string> ConnectedPlayers { get; } = new();

    /// <summary>Operators (ops.json).</summary>
    public BulkObservableCollection<string> OpPlayers { get; } = new();

    /// <summary>Banned players (banned-players.json).</summary>
    public BulkObservableCollection<string> BannedPlayers { get; } = new();

    /// <summary>Players who have ever joined (usercache.json).</summary>
    public BulkObservableCollection<string> KnownPlayers { get; } = new();

    // --- Whitelist ---

    private readonly WhitelistService _whitelist = new();

    /// <summary>Players currently in the whitelist (names).</summary>
    public BulkObservableCollection<string> WhitelistPlayers { get; } = new();

    [ObservableProperty]
    private bool _whitelistEnabled;

    [ObservableProperty]
    private string _newWhitelistName = string.Empty;

    // Colors per state (status text/dot and the card's signal bars).
    private static readonly IBrush BrushGreen = Frozen(NotificationPalette.DefaultSuccess);
    private static readonly IBrush BrushSignalGreen = Frozen("#55FF55");
    private static readonly IBrush BrushAmber = Frozen(NotificationPalette.DefaultWarning);
    private static readonly IBrush BrushRed = Frozen(NotificationPalette.DefaultError);
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

    /// <summary>
    /// The Bedrock ports the other servers have already been given.
    /// </summary>
    /// <remarks>
    /// Supplied by <c>MainViewModel</c>, which is the only thing that knows the other servers exist.
    /// It matters because the free-port search asks the operating system which UDP ports are
    /// <em>bound right now</em>: set crossplay up on two stopped servers and both are handed 19132,
    /// and the collision only shows up later as the second Geyser failing to bind.
    /// </remarks>
    public Func<IEnumerable<int>>? BedrockPortsInUse { get; set; }

    /// <summary>
    /// Assembles the view model. It does not start anything — see <see cref="Activate"/>.
    /// </summary>
    /// <remarks>
    /// The split is the difference between a server that <em>exists</em> and one that is being
    /// <em>watched</em>. Everything here reads: the config, the console palette, the server's own
    /// files. Nothing here polls, opens a socket, subscribes to a process-wide singleton or goes to
    /// the network, so building one costs a few file reads and leaves nothing running behind it.
    /// <para>
    /// That is what makes the class reachable from a test at all. It is also the right shape on its
    /// own: a constructor that started three timers and two background requests had already begun
    /// doing its job before its caller had the chance to decide whether it wanted it to.
    /// </para>
    /// </remarks>
    public ServerViewModel(ServerConfig config)
    {
        Config = config;
        _name = config.Name;
        _tunnelAddress = config.TunnelAddress;

        WireConsoleAndProcess();
        // Keep a reference to the handler: the manager is shared, so it must be unsubscribed
        // in ShutdownAsync or replaced view models would leak.
        // Both the legacy system service (PlayitManager) and our embedded agent (PlayitAgentRunner)
        // can drive the tunnel; the panel reflects whichever is in play (see PlayitStatus.Effective).
        _onPlayitStateChanged = _ => RunOnUi(RefreshPlayit);
        _onAgentStateChanged = _ => RunOnUi(RefreshPlayit);

        _wake = CreateWakeOnDemand(config);
        _admin = new PlayerAdmin(this, _properties, _whitelist, _players);
        _tunnel = CreateTunnelTracker(config);
        _idle = CreateIdleShutdown();

        _statsTimer = NewTimer(TimeSpan.FromSeconds(2), (_, _) => OnStatsTimerTick());
        // The Playit service runs in the background; we poll its state periodically, and the
        // tunnel address (via the playit API) less often.
        _playitTimer = NewTimer(TimeSpan.FromSeconds(3), OnPlayitTimerTick);
        // Ticks every minute and asks the clock whether the interval has run out, rather than being
        // rescheduled whenever the interval is changed: one less thing to keep in step with the
        // config, and changing the interval takes effect without restarting anything.
        _autoBackupTimer = NewTimer(TimeSpan.FromMinutes(1), (_, _) => OnAutoBackupTick());

        BackupCoordinator = new BackupCoordinator(this, new WorldBackupService());
        BackupCoordinator.RestoringChanged += () => RunOnUi(OnRestoringChanged);
        InitPerformance();

        // Before RefreshInfo, which refreshes the player lists and with them this.
        History = new PlayerHistoryViewModel(this);

        RefreshPort();
        RefreshInfo();
        Mods = new ServerModsViewModel(config);

        _preflight = CreatePreflight(config);
        Backups = new ServerBackupsViewModel(this);

        // The dialogs write straight into this instance. Listening is what makes every derived
        // property correct without anyone having to remember to ask.
        Config.PropertyChanged += OnConfigPropertyChanged;
    }

    /// <summary>Hooks the console buffer and the server process to this view model.</summary>
    private void WireConsoleAndProcess()
    {
        _console.Rebuilt += () => OnPropertyChanged(nameof(IsConsoleFiltered));
        _console.Flushed += OnConsoleFlushed;

        _process.OutputReceived += OnServerLine;
        _process.StateChanged += OnServerStateChanged;
        _process.UnexpectedExit += OnUnexpectedExit;
    }

    private WakeOnDemandController CreateWakeOnDemand(ServerConfig config) =>
        new(config, new WakeHost(
            IsStarting: () => State != ServerState.Stopped,
            Motd: () => MotdText,
            MaxPlayers: () => _maxPlayers,
            CanStart: () => CanStart,
            Say: line => OnConsoleLine(line),
            Notify: NotifyIf,
            // isAutoRestart: nobody is sitting in front of the app to answer a dialog.
            StartServer: () => _ = StartInternalAsync(isAutoRestart: true),
            RunOnUi: RunOnUi), _properties, _whitelist, _players);

    private TunnelAddressTracker CreateTunnelTracker(ServerConfig config) =>
        new(config, _properties, _playitApi, new TunnelHost(
            State: () => TunnelState,
            SetState: state => TunnelState = state,
            SetAddress: address => TunnelAddress = address,
            Say: line => OnConsoleLine(line),
            RunOnUi: RunOnUi));

    private IdleShutdownController CreateIdleShutdown() =>
        new(new IdleHost(
            Minutes: () => Config.IdleShutdownMinutes,
            IsRunning: () => State == ServerState.Running,
            PlayerCount: () => ConnectedPlayers.Count,
            WokeAtUtc: () => _wake.WokeAtUtc,
            ShowCountdown: text => IdleCountdownText = text,
            AnnounceStop: minutes =>
            {
                OnConsoleLine(string.Format(Localizer.Get("Msg_IdleShutdownFmt"), minutes));
                NotifyIf(NotificationKind.IdleShutdown,
                    string.Format(Localizer.Get("Notif_IdleStopped"), minutes));
            },
            StopAsync: Stop,
            ReportError: line => OnConsoleLine(line)));

    private StartPreflight CreatePreflight(ServerConfig config) =>
        new(config, new PreflightHost(
            Name: () => Name,
            Say: line => OnConsoleLine(line),
            ConfigChanged: () => ConfigChanged?.Invoke(),
            InstallMissing: (ids, progress) => Mods.InstallMissingAsync(ids, progress)),
            _java, _ports, _properties);

    private static DispatcherTimer NewTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    /// <summary>Applies the row of <see cref="ServerConfigEffects"/> for the field that changed.</summary>
    /// <remarks>
    /// <para>
    /// Marshalled onto the UI thread, because plenty of these changes do not start there: setting
    /// up crossplay picks a Bedrock port and writes it from a background continuation, and raising
    /// a bound property from off the UI thread is how a working feature turns into a crash.
    /// </para>
    /// <para>
    /// A field with no row is a field nobody declared, which the tests do not allow — but at run
    /// time the safe answer is to do nothing rather than to throw inside a property setter.
    /// </para>
    /// </remarks>
    private void OnConfigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not { } property) return;
        if (ServerConfigEffects.For(property) is { } row) RunOnUi(() => Apply(row));
    }

    /// <summary>
    /// Announces what one row names and does what it asks, here and in the panels below.
    /// </summary>
    private void Apply(ServerConfigEffects.Row row)
    {
        // Mirrors first: these two are bindable properties of their own that hold a copy of the
        // config's value, and the rest of the row may depend on them being current.
        if (row.Effects.HasFlag(ConfigEffect.MirrorName)) Name = Config.Name;
        if (row.Effects.HasFlag(ConfigEffect.MirrorTunnelAddress)) TunnelAddress = Config.TunnelAddress;

        foreach (var name in row.ServerProperties)
            OnPropertyChanged(name);

        if (row.Effects.HasFlag(ConfigEffect.RereadPort)) RefreshPort();
        if (row.Effects.HasFlag(ConfigEffect.RereadInfo)) RefreshInfo();
        if (row.Effects.HasFlag(ConfigEffect.RefreshSignal)) UpdateSignal();
        // The listener holds a real socket on the server's port, so switching it on has to open one
        // now. It used to wait for the next stop, i.e. until the server had been run once.
        if (row.Effects.HasFlag(ConfigEffect.RestartWakeListener)) _wake.Start();
        if (row.Effects.HasFlag(ConfigEffect.ReloadBackups)) Backups.RefreshIfLoaded();

        if (row.ModsProperties.Length > 0 || row.Effects != ConfigEffect.None)
            Mods.ApplyConfigChange(row.ModsProperties, row.Effects);
    }

    /// <summary>True between <see cref="Activate"/> and <see cref="ShutdownAsync"/>.</summary>
    /// <remarks>
    /// Kept so that both are safe to call twice, and so that <see cref="ShutdownAsync"/> never
    /// unsubscribes handlers it did not subscribe.
    /// </remarks>
    private bool _active;

    /// <summary>
    /// Starts watching: the polling timer, the shared Playit state, the tunnel addresses and the
    /// wake-on-demand listener. <see cref="ShutdownAsync"/> is its mirror.
    /// </summary>
    /// <remarks>
    /// Called by <c>MainViewModel</c> once the window is up, rather than by the constructor, so
    /// that building a server and switching it on are two decisions instead of one. Everything in
    /// here reaches outside the object — a shared singleton, a socket, the network — and none of it
    /// is wanted by code that only needs to look at a server.
    /// </remarks>
    public void Activate()
    {
        if (_active) return;
        _active = true;

        _playit.StateChanged += _onPlayitStateChanged;
        _agent.StateChanged += _onAgentStateChanged;

        _playitTimer.Start();
        _playit.RefreshState();
        // Sync directly: the shared manager/agent may already know the state (another view model
        // refreshed it before we subscribed), in which case no change event will fire.
        RefreshPlayit();

        _ = _tunnel.RefreshAsync();

        // Crossplay is a remembered setting, so it gets checked rather than assumed: the tunnel's
        // public port can change, and Geyser has to be re-pointed at it or the server quietly stops
        // being reachable from Bedrock.
        _ = RefreshBedrockAddressAsync();

        // Also here, not only on the transition to Stopped: opening the app finds every server
        // already stopped and fires no state change, so waiting for one would mean wake-on-demand
        // never starting until you had run and stopped a server by hand first.
        _wake.Start();

        // Prunes and, the first time ever, imports the server's old logs — in the background.
        History.Start();
    }

    // --- Tray-aware polling (EFI-2) ---
    // The app is designed to live in the tray with the window hidden; there's no point refreshing
    // stats text, sparklines and Playit status nobody can see at full cadence. When the window is
    // hidden the periodic work runs at 1/10th its usual rate (crash detection and the toasts that
    // matter in the tray are event-driven, not timer-driven, so they're unaffected). Everything
    // returns to full speed on the first tick after the window is shown again.

    /// <summary>True when the main window is hidden, i.e. minimized to the tray (see MainWindow).</summary>
    private static bool MainWindowHidden => AppWindows.Main is not { IsVisible: true };

    private int _hiddenStatsTicks;
    private int _hiddenPlayitTicks;

    private void OnStatsTimerTick()
    {
        // Before the throttle below, on purpose: the whole point of stopping an empty server is to
        // stop it while nobody is watching, which is exactly when the window is in the tray.
        _idle.Check();
        ProbePerformance();

        // Also before the throttle: a PC short of memory is the case where the window is most likely
        // to be in the tray, and a check every hundred seconds instead of ten would come too late.
        AdjustAgent();

        if (MainWindowHidden && ++_hiddenStatsTicks % 10 != 0) return;
        UpdateStats();
    }

    /// <summary>
    /// <c>server-port</c> changed on disk: the wake-on-demand listener moves with it, if the server
    /// is stopped and nothing else has the port.
    /// </summary>
    /// <remarks>
    /// The listener read the port when it opened and kept it. Only a config field restarted it, and
    /// the port is not one — it lives in <c>server.properties</c> — so after a change it went on
    /// answering on the old port until the server was next stopped, and players knocking on the new
    /// one woke nothing.
    /// </remarks>
    internal void FollowPortChange()
    {
        if (_process.IsRunning || IsPreparing || IsRestoring) return;
        _wake.Start();
    }

    private void OnPlayitTimerTick(object? sender, EventArgs e)
    {
        if (MainWindowHidden && ++_hiddenPlayitTicks % 10 != 0) return;

        // The system's own Playit service only matters when the app's agent is not the one in use:
        // asking it otherwise meant a systemctl every three seconds, per server, for an answer the
        // panel then ignored.
        if (!_agent.HasSecret) _playit.RefreshState();
        // Every ~30 s (10 ticks of 3 s; ~5 min while in the tray) refresh the tunnel address.
        if (++_playitTickCounter % 10 == 0)
        {
            _ = _tunnel.RefreshAsync();
            _ = RefreshBedrockAddressAsync();   // no-op unless this server does crossplay
        }
    }

    /// <summary>Recomputes the tunnel status/signal from whichever data-plane is in play.</summary>
    private void RefreshPlayit()
    {
        PlayitState = PlayitStatus.Effective(_agent.HasSecret, _agent.State, _playit.State);
        PlayitStatusText = PlayitStatus.Text(_agent.HasSecret, _agent.State, PlayitState, _playit.IsInstalled);
        UpdateSignal();
    }

    /// <summary>Shows how reachable the server is: see <see cref="ReachabilitySignal"/>.</summary>
    private void UpdateSignal()
    {
        var signal = ReachabilitySignal.For(State, Config.PlayitEnabled, PlayitState);

        SignalBrush = signal.Kind switch
        {
            SignalKind.Transition => BrushAmber,
            SignalKind.Off => BrushGray,
            SignalKind.NoTunnel => BrushRed,
            _ => BrushSignalGreen
        };
        SignalHint = Localizer.Get(signal.HintKey);
        ShowTunnelWarning = signal.ShowTunnelWarning;
    }

    public bool IsRunning => State is ServerState.Running or ServerState.Starting or ServerState.Stopping;
    public bool CanStart => State == ServerState.Stopped && !IsRestoring && !IsPreparing && !IsConverting;

    /// <summary>True while the settings page installs another loader into this server's folder.</summary>
    /// <remarks>
    /// Starting meanwhile would launch a jar that is being replaced. The button follows this, and so
    /// does every start without one — waking on demand, restarting after a crash.
    /// </remarks>
    [ObservableProperty]
    private bool _isConverting;

    partial void OnIsConvertingChanged(bool value) => NotifyCommandStates();

    /// <summary>True from the moment a start begins until the process is up or the start gives up.</summary>
    /// <remarks>
    /// Before the process exists a start spends seconds — minutes, with a big world to back up —
    /// checking the port, the path, the dependencies and the Java, and making the backup, and all
    /// that time the state is still Stopped. Without this, anything that starts a server without
    /// the button (a second knock while waking on demand, an auto-restart, the button itself once
    /// it had finished its own run) began a second start beside the first: a second backup of the
    /// same world, and an error when it found the first one's process already there.
    /// </remarks>
    [ObservableProperty]
    private bool _isPreparing;

    partial void OnIsPreparingChanged(bool value)
    {
        NotifyCommandStates();
        ShowStatus();
    }

    /// <summary>True while a backup is being put back in place of the world.</summary>
    /// <remarks>
    /// Nothing may start the server meanwhile: it would open a world that is being swapped out from
    /// under it. The button follows this, and so does every start that has no button — waking on
    /// demand, restarting after a crash.
    /// </remarks>
    public bool IsRestoring => BackupCoordinator.IsRestoring;

    /// <summary>
    /// The wake listener is closed while the world is swapped, or somebody pressing Join would start
    /// the server on half of it.
    /// </summary>
    private void OnRestoringChanged()
    {
        OnPropertyChanged(nameof(IsRestoring));
        NotifyCommandStates();
        if (IsRestoring) _wake.Stop();
        else if (!_process.IsRunning) _wake.Start();
    }
    public bool CanStop => State is ServerState.Running or ServerState.Starting;

    partial void OnNameChanged(string value) => Config.Name = value;

    /// <summary>Puts the state into words and a colour, for the list and the detail's header.</summary>
    /// <remarks>
    /// A stopped server that is getting ready to start says so, in the same amber as Starting:
    /// otherwise it read "Stopped" in red for as long as the backup took, with the Start button
    /// greyed out and nothing on screen to say why.
    /// </remarks>
    private void ShowStatus()
    {
        var preparing = State == ServerState.Stopped && IsPreparing;

        StatusText = preparing ? Localizer.Get("Status_Preparing") : State switch
        {
            ServerState.Stopped => Localizer.Get("Status_Stopped"),
            ServerState.Starting => Localizer.Get("Status_Starting"),
            ServerState.Running => Localizer.Get("Status_Running"),
            ServerState.Stopping => Localizer.Get("Status_Stopping"),
            _ => "?"
        };

        StatusBrush = preparing ? BrushAmber : State switch
        {
            ServerState.Running => BrushGreen,
            ServerState.Starting or ServerState.Stopping => BrushAmber,
            _ => BrushRed
        };
    }

    private void OnServerStateChanged(ServerState state) => RunOnUi(() =>
    {
        var previous = State;
        State = state;
        ShowStatus();
        UpdateSignal();

        if (state == ServerState.Running)
        {
            _stats.Reset();
            _statsTimer.Start();
            BeginPerformanceWatch();
            _lastRunningAtUtc = DateTime.UtcNow;

            // The interval starts now: the pre-start backup already covers the world as it is.
            BackupCoordinator.ClockStarted(DateTime.UtcNow);
            _autoBackupTimer.Start();
        }
        else if (state == ServerState.Stopped)
        {
            _statsTimer.Stop();
            _autoBackupTimer.Stop();
            EndPerformanceWatch();
            MemoryBudget.Release(Config.Id);

            // From Stopping, the app or the console asked for it; from anything else, it ended by itself.
            _memoryFinish = FinishMemoryRunAsync(previous == ServerState.Stopping ? SessionEnd.Stopped : SessionEnd.Crashed);

            // Explicitly, not just by waiting for the next check: see IdleShutdownController.Reset.
            _idle.Reset();

            CpuText = RamText = UptimeText = "—";
            _cpuHistory.Clear();
            _ramHistory.Clear();
            CpuSeries = null;
            RamSeries = null;
            ConnectedPlayers.Clear();
            SnapshotOnline();
            History.OnServerStopped();
            UpdatePlayerCount();
            RefreshPlayers(); // the files (ops/banned/whitelist) may have changed
            _wake.Start();
            _admin.ServerStopped();   // the next start reads the file as it is

            // However it stopped — a crash, "stop" typed in the console. A stop the app asked for
            // notes the world itself, holding it; this one waits until nothing else is reading it.
            BackupCoordinator.ServerStopped();
        }
        else if (state == ServerState.Starting)
        {
            ConnectedPlayers.Clear();
            SnapshotOnline();
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

    /// <summary>
    /// A line from the launcher itself.
    /// </summary>
    /// <remarks>
    /// Keeps its signature because some fifty places call it, eight of them by handing it over as an
    /// <c>IProgress&lt;string&gt;</c> to a service that knows nothing about consoles. Everything that
    /// reaches it this way is the app talking, which is why the kind can simply be assumed here
    /// instead of being guessed from text that is translated into five languages.
    /// </remarks>
    private void OnConsoleLine(string line) => OnConsoleLine(line, ConsoleLineKind.Launcher);

    /// <summary>A line from the server process, classified by where it came from.</summary>
    private void OnServerLine(string line, ConsoleSource source)
    {
        // First of all: the answer to the speed probe is not for the console, nor the log on disk,
        // nor the player history, so nothing below may see it.
        if (SwallowProbeAnswer(line)) return;

        // Only standard output's own previous line: standard error arrives on another thread, and
        // a JVM warning landing between two lines of the mod list must not decide what they are.
        var kind = ConsoleLineClassifier.Classify(line, source,
            source == ConsoleSource.Stdout ? _lastStdoutKind : null, _onlineNames);
        if (source == ConsoleSource.Stdout) _lastStdoutKind = kind;

        OnConsoleLine(line, kind);

        // Here, on the output's own thread, so writing the history to disk never holds up the UI.
        // Before the chat guard below: chat is something the history records on purpose.
        if (source == ConsoleSource.Stdout) History.OnServerLine(line, _onlineNames);

        // Chat is the one part of the console a player writes, so nothing below may react to it:
        // each detector is anchored on its own, and this keeps one that is not — or a future one —
        // from being driven by whatever somebody types.
        if (kind == ConsoleLineKind.Chat) return;

        WatchForOutOfMemory(line);
        WatchForLag(line);
        WatchForAgentError(line);

        // The server's own answer to "seed": remembered in case the world's files cannot be read.
        if (source == ConsoleSource.Stdout && WorldSeed.FromConsoleLine(line) is { } seed)
            RunOnUi(() =>
            {
                if (Config.LastKnownSeed == seed) return;
                Config.LastKnownSeed = seed;
                ConfigChanged?.Invoke();
            });

        // The server's answer to "save-all flush", which a backup of a running world waits for.
        if (source == ConsoleSource.Stdout && SaveConfirmation.IsSaveFinished(line))
            BackupCoordinator.ServerSaved();

        // Once per run: BlueMap repeats itself on every start, and so would the question.
        if (!_blueMapAsked && BlueMapConsent.IsAskingForConsent(line))
        {
            _blueMapAsked = true;
            Dispatcher.UIThread.Post(() => _ = OfferBlueMapDownloadAsync());
        }
    }

    /// <summary>Whether this run has already asked about BlueMap's download.</summary>
    private bool _blueMapAsked;

    /// <summary>
    /// Asks whether to accept BlueMap's download, and if so accepts it and reloads BlueMap.
    /// </summary>
    /// <remarks>
    /// Asks rather than accepts: it is consent to download Mojang's client files, and that belongs to
    /// the person running the server. Declining is remembered only until the next start, so the
    /// question comes back rather than the map silently never appearing.
    /// </remarks>
    private async Task OfferBlueMapDownloadAsync()
    {
        if (BlueMapConsent.FindConfig(Config.FolderPath) is not { } path) return;

        if (!await Dialogs.Current.ConfirmAsync(Localizer.Get("BlueMap_ConsentText"), "BlueMap"))
        {
            OnConsoleLine(Localizer.Get("Msg_BlueMapDeclined"));
            return;
        }

        try
        {
            if (BlueMapConsent.Accept(await File.ReadAllTextAsync(path)) is not { } accepted)
            {
                OnConsoleLine(string.Format(Localizer.Get("Msg_BlueMapCouldNotAcceptFmt"), path));
                return;
            }

            AtomicTextFile.WriteIfChanged(path, accepted);
            OnConsoleLine(Localizer.Get("Msg_BlueMapAccepted"));

            // BlueMap re-reads its config on reload, so the map starts without restarting the server.
            if (IsRunning) _process.SendCommand("bluemap reload");
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
        }
    }

    /// <summary>
    /// What the last line on standard output was. See <see cref="ConsoleLineClassifier.Classify"/>.
    /// </summary>
    private ConsoleLineKind? _lastStdoutKind;

    private void OnConsoleLine(string text, ConsoleLineKind kind)
    {
        // Off the UI thread on purpose (this is often called from a background thread): file I/O
        // shouldn't block RunOnUi's dispatch of the in-memory console update. The file keeps the
        // plain text — the kind is a way of looking at the console, not part of the record.
        ConsoleLogService.Shared.Log(Name, text);

        _console.Enqueue(new ConsoleLine(text, kind));
    }

    /// <summary>What reacts to the content of the lines that just went on screen.</summary>
    private void OnConsoleFlushed(IReadOnlyList<ConsoleLine> batch)
    {
        foreach (var line in batch)
        {
            // Only what the server says about itself. Chat is typed by players, and the app's own
            // lines and the echoed commands are not events at all: "x: Bob left the game" in chat
            // used to take Bob off the list, and with the list empty the idle timer stopped a
            // server that people were still playing on.
            if (line.Kind is ConsoleLineKind.Chat or ConsoleLineKind.Launcher or ConsoleLineKind.Command)
                continue;

            TrackPlayers(line.Text);
            WarnAboutModdedKick(line.Text);
            WarnAboutRejectedPath(line.Text);
        }
    }

    /// <summary>
    /// Turns Paper's "rename the affected folder" into an explanation of what has to be renamed.
    /// </summary>
    /// <remarks>
    /// The server exits cleanly here, so the launcher reads it as a crash and restarts twice more,
    /// burying the one line that said why under two more attempts. Saying it once, in the user's
    /// language, with the character and the path named, is the difference between a five-second fix
    /// and an afternoon.
    /// </remarks>
    private void WarnAboutRejectedPath(string line)
    {
        // The refusal comes before the server is up — Paperclip prints it before the server even
        // exists. Once it is running the path has been accepted, and a line saying otherwise can
        // only be somebody typing the sentence; believing it would switch auto-restart off.
        if (State == ServerState.Running) return;
        if (_pathRejectionWarned || !BukkitPathRule.IsPathRejection(line)) return;

        _pathRejectionWarned = true;
        // Belt as well as braces: the check before starting should mean this is never reached, but
        // if the server ever refuses the path for a reason that check did not foresee, restarting
        // three more times would only bury the one line that said why.
        _startFailureIsFinal = true;
        var bad = BukkitPathRule.OffendingCharacter(Config.FolderPath);
        OnConsoleLine(bad is { } c
            ? string.Format(Localizer.Get("Msg_BukkitPathFmt"), c)
            : Localizer.Get("Msg_BukkitPathUnknown"));
    }

    /// <summary>
    /// Turns the loader's "install NeoForge" kick into an explanation that fits what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only on a crossplay server, because on a Java-only one the message is already addressed to
    /// somebody who can act on it. Here it is not: the player who was turned away is on a phone or
    /// a console and cannot install a mod loader at all. What actually happened is that Geyser
    /// joins as a client with no mods, and this server now carries mods that the client must have.
    /// </para>
    /// <para>
    /// Once per run. A Bedrock client retries on its own, and repeating the same paragraph down the
    /// console would bury the server's real output under it.
    /// </para>
    /// </remarks>
    private void WarnAboutModdedKick(string line)
    {
        if (_moddedKickWarned || !Config.CrossplayEnabled) return;
        if (!CrossplayDiagnostics.IsModdedClientRejection(line)) return;

        _moddedKickWarned = true;
        OnConsoleLine(Localizer.Get("Msg_CrossplayModdedKick"));
    }

    /// <summary>
    /// Who is connected, as a copy the console classifier can read from the process's thread.
    /// </summary>
    /// <remarks>
    /// The classifier runs where the line arrives, off the UI thread, and <see cref="ConnectedPlayers"/>
    /// belongs to the UI thread. It only needs the names to tell a player's <c>/say</c> from a plugin
    /// logging under its own name, so an immutable copy swapped in on every join and leave is enough.
    /// </remarks>
    private volatile IReadOnlySet<string> _onlineNames = new HashSet<string>();

    private void SnapshotOnline() =>
        _onlineNames = new HashSet<string>(ConnectedPlayers, StringComparer.OrdinalIgnoreCase);

    // Live connected players, read from the join/leave messages in the console.
    private void TrackPlayers(string line)
    {
        var joined = ConsoleLineClassifier.NameBefore(line, " joined the game");
        if (joined is not null)
        {
            if (!ConnectedPlayers.Contains(joined)) ConnectedPlayers.Add(joined);
            BackupCoordinator.NoteWorldTouched();   // even if they leave before the clock next looks
            SnapshotOnline();
            History.RequestRefresh();
            UpdatePlayerCount();
            NotifyIf(NotificationKind.PlayerJoined,
                string.Format(Localizer.Get("Notif_PlayerJoinedFmt"), joined));
            return;
        }

        var left = ConsoleLineClassifier.NameBefore(line, " left the game");
        if (left is not null)
        {
            ConnectedPlayers.Remove(left);
            SnapshotOnline();
            History.RequestRefresh();
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
    /// toast names the server beside the time, so it's always clear which one it came from.
    /// </summary>
    private void NotifyIf(NotificationKind kind, string message)
    {
        if (!ToastService.MainWindowInactive || !NotificationPreferences.ShouldNotify(Config, kind))
            return;

        // The look comes from the kind, through the one table that holds it. Working it out here
        // would put a second opinion about what "crashed" means next to the first.
        var entry = NotificationCatalog.For(kind);
        ToastService.Shared.Notify(Localizer.Get(NotificationCatalog.HeadingKey(kind)), Name, message,
            entry.Icon, entry.Level, NotificationPreferences.EffectiveFor(Config));
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        _consecutiveCrashes = 0; // a deliberate Start gives auto-restart a fresh budget
        _moddedKickWarned = false; // and a fresh chance to explain the kick, in case the mods changed
        _pathRejectionWarned = false;
        _startFailureIsFinal = false;
        await StartInternalAsync(isAutoRestart: false);
    }

    internal async Task StartInternalAsync(bool isAutoRestart)
    {
        if (IsRestoring)
        {
            OnConsoleLine(Localizer.Get("Msg_StartWaitsForRestore"));
            return;
        }
        if (IsConverting)
        {
            OnConsoleLine(Localizer.Get("Msg_StartWaitsForLoader"));
            return;
        }

        // One start at a time (see IsPreparing). Said nowhere: the one already under way is the
        // answer to this one too.
        if (IsPreparing) return;
        IsPreparing = true;
        var shutdowns = _shutdowns;

        // A new run starts a new console story: the first line must not inherit the colour of the
        // last line of a crash, and a question declined last time is asked again.
        _lastStdoutKind = null;
        _blueMapAsked = false;

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

            // If the port is busy, offer to close the process holding it.
            if (!await _preflight.PortIsFreeAsync(isAutoRestart))
                return;

            // Paper and Purpur refuse to run from a path with "+" or "!" in it. Checked here
            // rather than left to fail: the server would exit cleanly on every attempt, which reads
            // as a crash and burns the whole auto-restart budget on something a retry cannot change.
            if (!await _preflight.TryFixRejectedPathAsync(isAutoRestart))
                return;

            // Correct a Geyser config the app wrote before it knew better. Cheap, and it is the
            // only thing that fixes a server already sitting on the wrong authentication.
            if (Config.CrossplayEnabled && _crossplay.RepairConfig(Config) is { } repaired)
                OnConsoleLine(repaired);

            // Everything the mods and plugins need is there — or the user has decided about it.
            if (!await _preflight.CheckContentDependenciesAsync(isAutoRestart))
                return;

            // Make sure the configured Java is compatible with this server's version.
            await _preflight.EnsureCompatibleJavaAsync();

            // The memory this run gets. Decided in the background because it asks the Java what it
            // is, which also leaves the version remembered for the one Start asks when building the
            // command line, instead of starting a JVM on the UI thread.
            await _memoryFinish;
            var (plan, notes) = await Task.Run(PlanMemory);
            foreach (var note in notes) OnConsoleLine(note);
            MemoryPlan = plan;

            // Back up the world right before touching it again: the safety net that matters most,
            // since it covers every start path (manual, Restart, and auto-restart after a crash).
            // Never while another copy is still reading the world, skipped when the world is still
            // the last backup's, and the JVM launched before anything else may read it: see
            // BackupCoordinator.StartAsync.
            await BackupCoordinator.StartAsync(() =>
            {
                if (shutdowns != _shutdowns) return;
                MemoryBudget.Reserve(Config.Id, plan.CeilingMb);
                BeginMemoryRun(plan);
                try
                {
                    _process.Start(Config, plan.Flags);
                }
                catch
                {
                    MemoryBudget.Release(Config.Id);
                    AbandonMemoryRun();
                    throw;
                }
            });
            // Playit already runs as a background service: we don't launch another agent.
            Backups.RefreshIfLoaded();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
        }
        finally
        {
            // The listener was let go of at the top, and only the transition to Stopped took it back
            // up — which never happens when the process never started. So a start abandoned here (a
            // busy port, a path Paper refuses, a dependency check cancelled) left a server with
            // wake-on-demand asleep and deaf until the app was restarted. Starting it twice is
            // harmless: Start lets go of the old socket first.
            if (!_process.IsRunning) _wake.Start();

            IsPreparing = false;
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
            // A start that dies over the memory agent is the agent's doing, not a crash to count.
            if (TryRecoverFromAgentFailure()) return;

            var reason = _crashReports.FindRecentCrashReason(Config.FolderPath, _process.StartedAtUtc);
            var codeText = exitCode?.ToString() ?? "?";
            OnConsoleLine(reason is not null
                ? string.Format(Localizer.Get("Msg_ServerCrashedReasonFmt"), codeText, reason)
                : string.Format(Localizer.Get("Msg_ServerCrashedFmt"), codeText));
            NotifyIf(NotificationKind.ServerCrashed, Localizer.Get("Notif_Crashed"));

            if (_startFailureIsFinal)
            {
                OnConsoleLine(Localizer.Get("Msg_NotRetryingFinal"));
                return;
            }

            // Stop was pressed and is waiting for a backup to finish reading the world: the crash
            // only got there first.
            if (_stopRequested) return;

            var stableRun = _lastRunningAtUtc is { } last && DateTime.UtcNow - last >= StabilityWindow;
            if (stableRun) _consecutiveCrashes = 0;
            _consecutiveCrashes++;

            if (_consecutiveCrashes > MaxAutoRestarts)
            {
                OnConsoleLine(string.Format(Localizer.Get("Msg_AutoRestartGaveUpFmt"), MaxAutoRestarts));
                NotifyIf(NotificationKind.AutoRestartGaveUp, Localizer.Get("Notif_GaveUp"));
                return;
            }

            OnConsoleLine(string.Format(Localizer.Get("Msg_AutoRestartingFmt"), _consecutiveCrashes,
                MaxAutoRestarts));
            await Task.Delay(AutoRestartDelay);

            // Only proceed if nothing else already started it in the meantime (e.g. the user
            // clicked Start manually right after the crash).
            if (CanStart)
                await StartInternalAsync(isAutoRestart: true);
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        _stopRequested = true;
        try
        {
            // With a snapshot of the good state just reached by stopping cleanly. Not for Restart's
            // internal stop or for the app-closing ShutdownAsync: the next Start's own pre-backup
            // (or, when closing, simply not needing one) already covers those. Nor when nobody
            // played: then the world is still the last backup's, and this is how a server going to
            // sleep with nobody on stopped filling the folder with copies of it.
            await BackupCoordinator.StopAsync(() => _process.StopAsync(StopTimeout), backupAfter: true);
            if (Config.BackupsEnabled) Backups.RefreshIfLoaded();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
        }
        finally
        {
            _stopRequested = false;
        }
    }

    /// <summary>Once a minute while the server runs: is it time for an automatic backup?</summary>
    private void OnAutoBackupTick()
    {
        switch (BackupCoordinator.ClockTick(DateTime.UtcNow))
        {
            case BackupDue.Now:
                _ = RunAutoBackupAsync();
                break;

            // Nobody played: no copy of the same world again, and the next one an interval away.
            case BackupDue.Skip:
                Backups.RefreshSummary();
                break;
        }
    }

    private async Task RunAutoBackupAsync()
    {
        try
        {
            await BackupCoordinator.BackupAsync("auto");
            Backups.RefreshIfLoaded();
        }
        catch (OperationCanceledException)
        {
            // Cut short by a stop, whose own backup covers the same world.
        }
        catch (Exception ex)
        {
            // A failed backup must never take the server down with it.
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Restart()
    {
        await BackupCoordinator.StopAsync(() => _process.StopAsync(StopTimeout), backupAfter: false);
        _consecutiveCrashes = 0; // a deliberate Restart gives auto-restart a fresh budget too
        await StartInternalAsync(isAutoRestart: false);
    }

    private bool CanSend => IsRunning && !string.IsNullOrWhiteSpace(CommandText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SendCommand()
    {
        var cmd = CommandText.Trim();
        if (cmd.Length == 0) return;
        DisarmProbe();   // what they asked for must reach them, not the probe
        OnConsoleLine("> " + cmd, ConsoleLineKind.Command);
        _process.SendCommand(cmd);
        if (WorldCommand.Touches(cmd)) BackupCoordinator.NoteWorldTouched();   // a /fill, with nobody on
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

    /// <summary>Writes a line the launcher itself says into this server's console.</summary>
    /// <remarks>
    /// For the tunnels screen, which acts on a server that is not the one on screen: its messages
    /// belong in that server's console, not in a dialog nobody asked for.
    /// </remarks>
    public void LogLauncher(string line) => RunOnUi(() => OnConsoleLine(line));

    /// <summary>Re-reads this server's tunnel addresses now, instead of waiting for the 30 s timer.</summary>
    public async Task RefreshTunnelInfoAsync()
    {
        await _tunnel.RefreshAsync();
        await RefreshBedrockAddressAsync();   // no-op unless this server does crossplay
    }

    [RelayCommand(CanExecute = nameof(HasTunnelAddress))]
    private async Task CopyTunnelAddress()
    {
        if (string.IsNullOrEmpty(TunnelAddress)) return;
        if (AppWindows.Main?.Clipboard is { } cb)
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
    private void ClearConsole() => _console.Clear();

    /// <summary>
    /// Creates (if it doesn't exist) the Playit tunnel for this server's port, using the write
    /// key. Messages are shown in the server's console.
    /// </summary>
    public Task CreateTunnelAsync(string writeKey) => _tunnel.CreateAsync(writeKey, Name);

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
        MotdText = props.TryGetValue("motd", out var m) && !string.IsNullOrWhiteSpace(m)
            ? m
            : ServerPropertiesService.DefaultMotd;
        _maxPlayers = props.TryGetValue("max-players", out var mp) && int.TryParse(mp, out var n)
            ? n
            : ServerPropertiesService.DefaultMaxPlayers;
        UpdatePlayerCount();
        LoadIcon();
        RefreshPlayers();
    }

    private void RefreshWhitelist(IReadOnlyDictionary<string, string>? props = null)
    {
        props ??= _properties.Read(Config.PropertiesPath);
        WhitelistEnabled = ServerPropertiesService.GetBool(props, "white-list", fallback: false);

        WhitelistPlayers.Clear();
        foreach (var name in _whitelist.ReadNames(Config.FolderPath))
            WhitelistPlayers.Add(name);
    }

    [RelayCommand]
    private async Task AddToWhitelist()
    {
        if (await _admin.AddToWhitelistAsync(NewWhitelistName))
            NewWhitelistName = string.Empty;
    }

    /// <summary>The switch on the whitelist card: turns it off without emptying it, or back on.</summary>
    [RelayCommand]
    private Task ToggleWhitelist() => _admin.SetWhitelistAsync(!WhitelistEnabled);

    /// <summary>
    /// The settings page wrote <paramref name="changes"/> into server.properties; see
    /// <see cref="PlayerAdmin.FollowSavedPropertiesAsync"/>.
    /// </summary>
    internal Task FollowSavedPropertiesAsync(IReadOnlyDictionary<string, string> changes) =>
        _admin.FollowSavedPropertiesAsync(changes);

    [RelayCommand]
    private Task RemoveFromWhitelist(string? name) => _admin.RemoveFromWhitelistAsync(name);

    // The icon players see in the server list is server-icon.png (root, 64x64).
    private void LoadIcon() => ServerIcon = ServerIconImage.Load(Config.FolderPath);

    private void UpdatePlayerCount() => PlayerCountText = $"{ConnectedPlayers.Count}/{_maxPlayers}";

    /// <summary>Reads ops.json, banned-players.json, usercache.json and the whitelist.</summary>
    [RelayCommand]
    private void RefreshPlayers()
    {
        ReplaceAll(OpPlayers, _players.ReadOps(Config.FolderPath));
        ReplaceAll(BannedPlayers, _players.ReadBanned(Config.FolderPath));
        ReplaceAll(KnownPlayers, _players.ReadKnown(Config.FolderPath));
        RefreshWhitelist();
        History.RequestRefresh();
    }

    /// <summary>
    /// Refills one of the player lists, announcing the result once instead of once per name.
    /// </summary>
    private static void ReplaceAll(BulkObservableCollection<string> target, IEnumerable<string> items) =>
        target.ReplaceAll(items);

    [RelayCommand]
    private Task OpPlayer(string? name) => _admin.OpAsync(name);

    [RelayCommand]
    private Task DeopPlayer(string? name) => _admin.DeopAsync(name);

    [RelayCommand]
    private Task KickPlayer(string? name) => _admin.KickAsync(name);

    [RelayCommand]
    private Task BanPlayer(string? name) => _admin.BanAsync(name);

    [RelayCommand]
    private Task PardonPlayer(string? name) => _admin.PardonAsync(name);

    private void UpdateStats()
    {
        var sample = _stats.Sample(_process.CurrentProcess);
        if (sample is null)
        {
            CpuText = RamText = UptimeText = "—";
            return;
        }

        // The memory only this server holds, not the working set Task Manager shows (see ProcessMemory).
        var ramMb = MemoryReadout.OwnMb(sample, _memTracker?.Collector ?? GcCollector.Unknown);
        _memTracker?.AddSample(sample.PrivateMb, ConnectedPlayers.Count);
        PumpGcLog();
        FeedPerformance(sample);

        CpuText = $"{sample.CpuPercent:0.#} %";
        RamText = $"{ramMb} MB";
        UptimeText = FormatUptime(sample.Uptime);

        // Feed the mini charts; snapshots because the Sparkline control re-renders per assignment.
        _cpuHistory.Add(sample.CpuPercent);
        _ramHistory.Add(ramMb);
        if (_cpuHistory.Count > MaxStatSamples) _cpuHistory.RemoveAt(0);
        if (_ramHistory.Count > MaxStatSamples) _ramHistory.RemoveAt(0);
        CpuSeries = _cpuHistory.ToArray();
        RamSeries = _ramHistory.ToArray();
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s"
            : $"{t.Minutes}m {t.Seconds}s";

    /// <summary>
    /// The mirror of <see cref="Activate"/>: stops watching, and stops the server if it is running.
    /// Does NOT touch the Playit service, which keeps running in the background.
    /// </summary>
    /// <remarks>
    /// Safe on a view model that was never activated, which is the normal case in a test. Only the
    /// two unsubscriptions would be wrong to run unpaired, and they are guarded; stopping an inert
    /// timer and closing a listener that was never opened cost nothing.
    /// </remarks>
    public async Task ShutdownAsync()
    {
        if (_active)
        {
            _active = false;
            _playit.StateChanged -= _onPlayitStateChanged; // the manager is shared and outlives us
            _agent.StateChanged -= _onAgentStateChanged;   // the agent runner is shared too
        }

        Config.PropertyChanged -= OnConfigPropertyChanged;
        _statsTimer.Stop();
        _idle.Dispose();
        _playitTimer.Stop();
        _autoBackupTimer.Stop();
        Mods.Shutdown();                               // cancels anything the store was fetching
        History.Shutdown();                            // drops a rebuild that was waiting its turn
        _connectionTest?.Cancel();                     // nobody is left to read what it measures
        _wake.Stop();                                  // frees the port we answer on while asleep
        _shutdowns++;                                  // a start still getting ready must not launch

        // Cuts a backup in progress short, and stops the server holding the world: the app is
        // closing, so the Stopped event may never run to note it.
        Func<Task>? stop = _process.IsRunning ? () => _process.StopAsync(StopTimeout) : null;
        await BackupCoordinator.ShutdownAsync(stop);
        await FinishMemoryRunAsync(SessionEnd.AppClosed);  // the Stopped event may never get to run
    }

    ServerConfig IPlayerAdminHost.Config => Config;
    bool IPlayerAdminHost.IsProcessRunning => _process.IsRunning;
    bool IPlayerAdminHost.WhitelistEnabled => WhitelistEnabled;
    void IPlayerAdminHost.SendToConsole(string command) => _process.SendCommand(command);
    void IPlayerAdminHost.Echo(string command) => OnConsoleLine("> " + command, ConsoleLineKind.Command);
    void IPlayerAdminHost.Say(string line) => OnConsoleLine(line);
    void IPlayerAdminHost.RefreshWhitelist() => RefreshWhitelist();
    void IPlayerAdminHost.RefreshPlayers() => RefreshPlayers();
    void IPlayerAdminHost.ShowWhitelistEnabled(bool on) => WhitelistEnabled = on;
    void IPlayerAdminHost.ResyncWhitelistSwitch() => OnPropertyChanged(nameof(WhitelistEnabled));

    ServerConfig IBackupHost.Config => Config;
    bool IBackupHost.IsProcessRunning => _process.IsRunning;
    bool IBackupHost.IsPreparing => IsPreparing;
    int IBackupHost.PlayersOnline => ConnectedPlayers.Count;
    void IBackupHost.SendToConsole(string command) => _process.SendCommand(command);
    void IBackupHost.Say(string line) => OnConsoleLine(line);

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
