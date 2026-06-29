using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Representa un servidor en la interfaz: su configuración, estado en vivo, consola
/// integrada, envío de comandos e integración con Playit.
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
    private readonly object _consoleLock = new();

    public ServerConfig Config { get; }

    /// <summary>Líneas de la consola integrada (stdout/stderr del servidor + Playit).</summary>
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

    /// <summary>Comandos habituales con su explicación (para la ayuda de la consola).</summary>
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

    // --- Vista estilo lista de Minecraft ---

    [ObservableProperty]
    private ImageSource? _serverIcon;

    public bool HasIcon => ServerIcon is not null;

    [ObservableProperty]
    private string _motdText = "A Minecraft Server";

    [ObservableProperty]
    private string _playerCountText = "0/20";

    private int _maxPlayers = 20;

    private readonly PlayersService _players = new();

    /// <summary>Jugadores conectados ahora (en vivo, leído de la consola).</summary>
    public ObservableCollection<string> ConnectedPlayers { get; } = new();

    /// <summary>Operadores (ops.json).</summary>
    public ObservableCollection<string> OpPlayers { get; } = new();

    /// <summary>Baneados (banned-players.json).</summary>
    public ObservableCollection<string> BannedPlayers { get; } = new();

    /// <summary>Jugadores que han entrado alguna vez (usercache.json).</summary>
    public ObservableCollection<string> KnownPlayers { get; } = new();

    // --- Lista blanca (whitelist) ---

    private readonly WhitelistService _whitelist = new();

    /// <summary>Jugadores actualmente en la whitelist (nombres).</summary>
    public ObservableCollection<string> WhitelistPlayers { get; } = new();

    [ObservableProperty]
    private bool _whitelistEnabled;

    [ObservableProperty]
    private string _newWhitelistName = string.Empty;

    // Colores según el estado (texto/punto de estado y barras de señal de la tarjeta).
    private static readonly Brush BrushGreen = Frozen("#3FB950");
    private static readonly Brush BrushSignalGreen = Frozen("#55FF55");
    private static readonly Brush BrushAmber = Frozen("#E3A82B");
    private static readonly Brush BrushRed = Frozen("#E05561");
    private static readonly Brush BrushGray = Frozen("#6E7681");

    [ObservableProperty]
    private Brush _statusBrush = BrushRed;

    [ObservableProperty]
    private Brush _signalBrush = BrushGray;

    [ObservableProperty]
    private string _signalHint = Localizer.Get("Signal_Off");

    [ObservableProperty]
    private bool _showTunnelWarning;

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Se dispara cuando cambia algo persistible del servidor (para guardar).</summary>
    public event Action? ConfigChanged;

    public ServerViewModel(ServerConfig config)
    {
        Config = config;
        _name = config.Name;
        _tunnelAddress = config.TunnelAddress;

        // Permite actualizar ConsoleLines desde hilos de fondo de forma segura.
        BindingOperations.EnableCollectionSynchronization(ConsoleLines, _consoleLock);

        _process.OutputReceived += OnConsoleLine;
        _process.StateChanged += OnServerStateChanged;
        _playit.StateChanged += s => RunOnUi(() => { PlayitState = s; UpdatePlayitStatusText(); UpdateSignal(); });

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) => UpdateStats();

        // El servicio de Playit corre de fondo; consultamos su estado periódicamente, y la
        // dirección del túnel (vía API de playit) con menos frecuencia.
        _playitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _playitTimer.Tick += OnPlayitTimerTick;
        _playitTimer.Start();
        _playit.RefreshState();
        UpdatePlayitStatusText();
        UpdateSignal();

        RefreshPort();
        RefreshInfo();
        _ = RefreshTunnelAddressAsync();
    }

    private void OnPlayitTimerTick(object? sender, EventArgs e)
    {
        _playit.RefreshState();
        // Cada ~30 s (10 ticks de 3 s) refrescamos la dirección del túnel desde la API.
        if (++_playitTickCounter % 10 == 0)
            _ = RefreshTunnelAddressAsync();
    }

    /// <summary>Obtiene la dirección del túnel de la API de playit emparejando por puerto.</summary>
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
            // Best-effort: si la API falla, se mantiene la dirección guardada/manual.
        }
    }

    private void UpdatePlayitStatusText() => PlayitStatusText = PlayitState switch
    {
        PlayitState.Running => Localizer.Get(_playit.IsInstalled ? "Playit_ActiveBg" : "Playit_Active"),
        PlayitState.Starting => Localizer.Get("Status_Starting"),
        _ => Localizer.Get(_playit.IsInstalled ? "Playit_Stopped" : "Playit_NotInstalled")
    };

    /// <summary>
    /// Calcula la "señal" (accesibilidad real): verde solo si el servidor está encendido y, cuando
    /// usa Playit, el túnel está activo. Si está encendido sin túnel, rojo + aviso.
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

    // Jugadores conectados en vivo leyendo los mensajes de entrada/salida de la consola.
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

    /// <summary>Extrae el nombre justo antes de un marcador (p. ej. " joined the game").</summary>
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

            // Si el puerto está ocupado, ofrecemos cerrar el proceso que lo tiene.
            var port = _properties.GetServerPort(Config.PropertiesPath);
            if (port.HasValue && _ports.IsPortInUse(port.Value) && !await TryFreePortAsync(port.Value))
                return;

            // Asegurar que el Java configurado es compatible con la versión de este servidor.
            await EnsureCompatibleJavaAsync();

            _process.Start(Config);
            // Playit ya corre como servicio en segundo plano: no lanzamos otro agente.
        }
        catch (Exception ex)
        {
            OnConsoleLine($"[Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Comprueba que el Java configurado sirve para la versión de este servidor (leída del jar).
    /// Si no, instala/usa el Java correcto y lo guarda en la config.
    /// </summary>
    private async Task EnsureCompatibleJavaAsync()
    {
        var required = _java.GetRequiredJavaFromJar(Config.JarFullPath);
        if (required is null) return; // no se puede saber (jar antiguo): no bloqueamos

        var current = _java.GetMajorVersion(Config.JavaPath);
        if (current > 0 && JavaService.IsCompatible(current, required.Value))
            return;

        OnConsoleLine($"[Launcher] Este servidor necesita Java {required}" +
                      (current > 0 ? $" (el configurado es Java {current})." : ".") + " Preparando Java compatible...");
        try
        {
            var path = await _java.EnsureJavaAsync(required.Value, new Progress<string>(OnConsoleLine));
            if (!string.Equals(path, Config.JavaPath, StringComparison.OrdinalIgnoreCase))
            {
                Config.JavaPath = path;
                ConfigChanged?.Invoke();
                OnConsoleLine($"[Launcher] Java configurado para este servidor: {path}");
            }
        }
        catch (Exception ex)
        {
            OnConsoleLine($"[Aviso] No se pudo preparar Java {required}: {ex.Message}. Se intentará iniciar igualmente.");
        }
    }

    /// <summary>
    /// El puerto está ocupado: identifica el proceso y ofrece cerrarlo. Devuelve true si quedó libre.
    /// </summary>
    private async Task<bool> TryFreePortAsync(int port)
    {
        var pid = _ports.GetListeningPid(port);
        string procDesc = "otra aplicación";
        if (pid.HasValue)
        {
            try { procDesc = $"\"{System.Diagnostics.Process.GetProcessById(pid.Value).ProcessName}\" (PID {pid})"; }
            catch { procDesc = $"PID {pid}"; }
        }

        var answer = System.Windows.MessageBox.Show(
            $"El puerto {port} ya está en uso por {procDesc}.\n\n" +
            "Esto suele pasar si un servidor anterior se quedó colgado.\n" +
            "¿Quieres cerrar ese proceso e iniciar el servidor?",
            "Puerto ocupado",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            OnConsoleLine($"[Error] El puerto {port} está ocupado por {procDesc}. No se inició.");
            return false;
        }

        try
        {
            if (pid.HasValue)
            {
                System.Diagnostics.Process.GetProcessById(pid.Value).Kill(entireProcessTree: true);
                OnConsoleLine($"[Launcher] Cerrado el proceso que ocupaba el puerto {port} ({procDesc}).");
            }
        }
        catch (Exception ex)
        {
            OnConsoleLine($"[Error] No se pudo cerrar el proceso: {ex.Message}");
            return false;
        }

        // Esperar a que el puerto quede libre.
        for (var i = 0; i < 12 && _ports.IsPortInUse(port); i++)
            await Task.Delay(300);

        if (_ports.IsPortInUse(port))
        {
            OnConsoleLine($"[Error] El puerto {port} sigue ocupado. Inténtalo de nuevo en unos segundos.");
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
            OnConsoleLine($"[Error] {ex.Message}");
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

    /// <summary>Pone el comando elegido de la ayuda en la caja (listo para completar y enviar).</summary>
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
            OnConsoleLine("[Playit] El servicio de Playit no está instalado en este equipo.");
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
            OnConsoleLine("[Playit] No se pudo cambiar el servicio (suele requerir " +
                          $"ejecutar la app como administrador, o usar el icono de la bandeja): {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasTunnelAddress))]
    private void CopyTunnelAddress()
    {
        if (!string.IsNullOrEmpty(TunnelAddress))
            Clipboard.SetText(TunnelAddress);
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
    /// Crea (si no existe) el túnel de Playit para el puerto de este servidor, usando la clave de
    /// escritura. Los mensajes salen en la consola del servidor.
    /// </summary>
    public async Task CreateTunnelAsync(string writeKey)
    {
        var port = _properties.GetServerPort(Config.PropertiesPath);
        if (!port.HasValue)
        {
            OnConsoleLine("[Playit] No se pudo determinar el puerto del servidor (¿falta server.properties?).");
            return;
        }

        try
        {
            OnConsoleLine($"[Playit] Creando túnel Minecraft Java para el puerto {port}...");
            var created = await _playitApi.EnsureMinecraftTunnelAsync(writeKey, Name, port.Value);
            OnConsoleLine(created
                ? "[Playit] Túnel creado correctamente. La dirección aparecerá en unos segundos."
                : $"[Playit] Ya existía un túnel para el puerto {port}; se reutiliza.");
            await RefreshTunnelAddressAsync();
        }
        catch (Exception ex)
        {
            OnConsoleLine($"[Playit] Error al crear el túnel: {ex.Message}");
        }
    }

    /// <summary>Abre el panel de túneles de Playit.gg en el navegador (para crear/ver túneles).</summary>
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
            OnConsoleLine($"[Error] No se pudo abrir el navegador: {ex.Message}");
        }
    }

    private void RefreshPort()
    {
        var port = _properties.GetServerPort(Config.PropertiesPath);
        PortText = port?.ToString() ?? "—";
    }

    partial void OnServerIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));

    /// <summary>Vuelve a leer datos del disco (tras editar server.properties).</summary>
    public void RefreshFromDisk()
    {
        RefreshPort();
        RefreshInfo();
    }

    /// <summary>Lee MOTD, máximo de jugadores e icono del servidor (vista estilo Minecraft).</summary>
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
            OnConsoleLine($"[Whitelist] {ex.Message}");
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
            OnConsoleLine($"[Whitelist] {ex.Message}");
        }
    }

    private void LoadIcon()
    {
        // El icono que ven los jugadores en la lista de servidores es server-icon.png (raíz, 64x64).
        var path = Path.Combine(Config.FolderPath, "server-icon.png");
        if (!File.Exists(path))
        {
            ServerIcon = null;
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            ServerIcon = bmp;
        }
        catch
        {
            ServerIcon = null;
        }
    }

    private void UpdatePlayerCount() => PlayerCountText = $"{ConnectedPlayers.Count}/{_maxPlayers}";

    /// <summary>Lee ops.json, banned-players.json, usercache.json y la whitelist.</summary>
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
        OnConsoleLine($"[Jugadores] El servidor debe estar encendido para {action}.");
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
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning("dar OP")) return;
        await PlayerCommandAsync($"op {name}");
    }

    [RelayCommand]
    private async Task DeopPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning("quitar OP")) return;
        await PlayerCommandAsync($"deop {name}");
    }

    [RelayCommand]
    private async Task KickPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning("expulsar")) return;
        await PlayerCommandAsync($"kick {name}");
    }

    [RelayCommand]
    private async Task BanPlayer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnsureRunning("banear")) return;
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

    /// <summary>Detiene el servidor al cerrar la app. NO toca el servicio de Playit (sigue de fondo).</summary>
    public async Task ShutdownAsync()
    {
        _statsTimer.Stop();
        _playitTimer.Stop();
        if (_process.IsRunning)
            await _process.StopAsync(TimeSpan.FromSeconds(15));
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher.CheckAccess() ?? true)
            action();
        else
            app.Dispatcher.Invoke(action);
    }
}
