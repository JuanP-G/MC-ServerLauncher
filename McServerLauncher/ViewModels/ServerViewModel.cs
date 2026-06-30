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

    private readonly ServerProcessManager _process = new();
    private readonly PlayitManager _playit = new();
    private readonly ProcessStatsService _stats = new();
    private readonly ServerPropertiesService _properties = new();
    private readonly PortService _ports = new();
    private readonly JavaService _java = new();
    private readonly PlayitApiService _playitApi = new();
    private int _playitTickCounter;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _playitTimer;

    public ServerConfig Config { get; }

    /// <summary>Lines of the embedded console (server stdout/stderr + Playit).</summary>
    public ObservableCollection<string> ConsoleLines { get; } = new();

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

    private static readonly IReadOnlyList<CommandHelp> SharedCommandHelp = new List<CommandHelp>
    {
        new("say ", "say <mensaje>", "Envía un mensaje en el chat a todos los jugadores."),
        new("list", "list", "Muestra los jugadores conectados ahora mismo."),
        new("op ", "op <jugador>", "Da permisos de operador (administrador) a un jugador."),
        new("deop ", "deop <jugador>", "Quita los permisos de operador a un jugador."),
        new("kick ", "kick <jugador> [razón]", "Expulsa a un jugador (puede volver a entrar)."),
        new("ban ", "ban <jugador> [razón]", "Banea a un jugador para que no pueda entrar."),
        new("pardon ", "pardon <jugador>", "Quita el baneo a un jugador."),
        new("whitelist add ", "whitelist add <jugador>", "Añade un jugador a la lista blanca."),
        new("whitelist remove ", "whitelist remove <jugador>", "Quita un jugador de la lista blanca."),
        new("gamemode ", "gamemode <modo> [jugador]", "Cambia el modo de juego (survival, creative, adventure, spectator)."),
        new("tp ", "tp <jugador> <destino>", "Teletransporta a un jugador hasta otro jugador o coordenadas."),
        new("give ", "give <jugador> <objeto> [cantidad]", "Da objetos a un jugador."),
        new("time set ", "time set <day|night|valor>", "Cambia la hora del mundo."),
        new("weather ", "weather <clear|rain|thunder>", "Cambia el clima."),
        new("difficulty ", "difficulty <dificultad>", "Cambia la dificultad (peaceful, easy, normal, hard)."),
        new("gamerule ", "gamerule <regla> <valor>", "Cambia una regla del juego (p. ej. keepInventory true)."),
        new("seed", "seed", "Muestra la semilla del mundo."),
        new("save-all", "save-all", "Guarda el mundo en disco de inmediato."),
        new("stop", "stop", "Apaga el servidor de forma segura (guardando el mundo)."),
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

    // --- Minecraft server-list style view ---

    [ObservableProperty]
    private Bitmap? _serverIcon;

    public bool HasIcon => ServerIcon is not null;

    public ServerModsViewModel Mods { get; }
    
    public bool IsModded => Config.Type != ServerType.Vanilla;

    // --- State properties ---
    [ObservableProperty]
    private string _motdText = "A Minecraft Server";

    [ObservableProperty]
    private string _playerCountText = "0/20";

    private int _maxPlayers = 20;

    private readonly PlayersService _players = new();

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
        _playit.StateChanged += s => RunOnUi(() => { PlayitState = s; UpdatePlayitStatusText(); UpdateSignal(); });

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) => UpdateStats();

        // The Playit service runs in the background; we poll its state periodically, and the
        // tunnel address (via the playit API) less often.
        _playitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _playitTimer.Tick += OnPlayitTimerTick;
        _playitTimer.Start();
        _playit.RefreshState();
        UpdatePlayitStatusText();
        UpdateSignal();

        RefreshPort();
        RefreshInfo();
        Mods = new ServerModsViewModel(config);
        _ = RefreshTunnelAddressAsync();
    }

    private void OnPlayitTimerTick(object? sender, EventArgs e)
    {
        _playit.RefreshState();
        // Every ~30 s (10 ticks of 3 s) we refresh the tunnel address from the API.
        if (++_playitTickCounter % 10 == 0)
            _ = RefreshTunnelAddressAsync();
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

    private void UpdatePlayitStatusText() => PlayitStatusText = PlayitState switch
    {
        PlayitState.Running => Localizer.Get(_playit.IsInstalled ? "Playit_ActiveBg" : "Playit_Active"),
        PlayitState.Starting => Localizer.Get("Status_Starting"),
        _ => Localizer.Get(_playit.IsInstalled ? "Playit_Stopped" : "Playit_NotInstalled")
    };

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
        }
        else if (state == ServerState.Stopped)
        {
            _statsTimer.Stop();
            CpuText = RamText = UptimeText = "—";
            ConnectedPlayers.Clear();
            UpdatePlayerCount();
            RefreshPlayers(); // los archivos (ops/banned/whitelist) pueden haber cambiado
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

    private void OnConsoleLine(string line) => RunOnUi(() =>
    {
        ConsoleLines.Add(line);
        if (ConsoleLines.Count > MaxConsoleLines)
            ConsoleLines.RemoveAt(0);

        TrackPlayers(line);
    });

    // Live connected players, read from the join/leave messages in the console.
    private void TrackPlayers(string line)
    {
        var joined = NameBefore(line, " joined the game");
        if (joined is not null)
        {
            if (!ConnectedPlayers.Contains(joined)) ConnectedPlayers.Add(joined);
            UpdatePlayerCount();
            return;
        }

        var left = NameBefore(line, " left the game");
        if (left is not null)
        {
            ConnectedPlayers.Remove(left);
            UpdatePlayerCount();
        }
    }

    /// <summary>Extracts the name right before a marker (e.g. " joined the game").</summary>
    private static string? NameBefore(string line, string marker)
    {
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx <= 0) return null;
        var head = line[..idx];
        var sp = head.LastIndexOf(' ');
        var name = sp >= 0 ? head[(sp + 1)..] : head;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        try
        {
            RefreshPort();
            RefreshInfo();

            // If the port is busy, offer to close the process holding it.
            var port = _properties.GetServerPort(Config.PropertiesPath);
            if (port.HasValue && _ports.IsPortInUse(port.Value) && !await TryFreePortAsync(port.Value))
                return;

            // Make sure the configured Java is compatible with this server's version.
            await EnsureCompatibleJavaAsync();

            _process.Start(Config);
            // Playit already runs as a background service: we don't launch another agent.
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
        if (required is null) return; // no se puede saber (jar antiguo): no bloqueamos

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
                System.Diagnostics.Process.GetProcessById(pid.Value).Kill(entireProcessTree: true);
                OnConsoleLine(string.Format(Localizer.Get("Msg_ClosedPortProcess"), port, procDesc));
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
        await Start();
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
    private void ClearConsole() => ConsoleLines.Clear();

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
        var name = NewWhitelistName?.Trim();
        if (string.IsNullOrEmpty(name)) return;

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
        if (string.IsNullOrWhiteSpace(name)) return;

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

    [RelayCommand]
    private async Task OpPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning(Localizer.Get("Action_Op"))) return;
        await PlayerCommandAsync($"op {name}");
    }

    [RelayCommand]
    private async Task DeopPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning(Localizer.Get("Action_Deop"))) return;
        await PlayerCommandAsync($"deop {name}");
    }

    [RelayCommand]
    private async Task KickPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning(Localizer.Get("Action_Kick"))) return;
        await PlayerCommandAsync($"kick {name}");
    }

    [RelayCommand]
    private async Task BanPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning(Localizer.Get("Action_Ban"))) return;
        await PlayerCommandAsync($"ban {name}");
    }

    [RelayCommand]
    private async Task PardonPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
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
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s"
            : $"{t.Minutes}m {t.Seconds}s";

    /// <summary>Stops the server when the app closes. Does NOT touch the Playit service (keeps running in the background).</summary>
    public async Task ShutdownAsync()
    {
        _statsTimer.Stop();
        _playitTimer.Stop();
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
