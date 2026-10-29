using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>What <see cref="WakeOnDemandController"/> needs to know about, and to be able to do to, its server.</summary>
/// <param name="IsStarting">Whether the server is already coming up (changes what a client is told).</param>
/// <param name="Motd">The server's own message of the day.</param>
/// <param name="MaxPlayers">The server's player limit.</param>
/// <param name="CanStart">Whether a start would be accepted right now.</param>
/// <param name="Say">Writes a line in the server's console.</param>
/// <param name="Notify">Raises a notification of the given kind.</param>
/// <param name="StartServer">Starts the server, unattended.</param>
/// <param name="RunOnUi">Runs something on the UI thread; the listener calls from its own.</param>
public sealed record WakeHost(
    Func<bool> IsStarting,
    Func<string> Motd,
    Func<int> MaxPlayers,
    Func<bool> CanStart,
    Action<string> Say,
    Action<NotificationKind, string> Notify,
    Action StartServer,
    Action<Action> RunOnUi);

/// <summary>
/// Answers on a stopped server's port and starts the server when somebody tries to join.
/// </summary>
/// <remarks>
/// <para>
/// While the server sleeps, this holds its port, shows the server's own MOTD plus what is going on,
/// and treats a Join as a request to wake it — unless the whitelist says the player may not
/// (<see cref="WakePolicy"/>). The listener itself is <see cref="WakeOnDemandListener"/>; this is
/// what connects it to one server.
/// </para>
/// <para>
/// <see cref="WokeAtUtc"/> is read by <see cref="IdleShutdownController"/>, which leaves a freshly
/// woken server alone for <see cref="IdlePolicy.WakeGrace"/>.
/// </para>
/// </remarks>
public sealed class WakeOnDemandController : IDisposable
{
    private readonly ServerConfig _config;
    private readonly WakeHost _host;
    private readonly ServerPropertiesService _properties;
    private readonly WhitelistService _whitelist;
    private readonly PlayersService _players;
    private readonly WakeOnDemandListener _listener = new();

    public WakeOnDemandController(ServerConfig config, WakeHost host, ServerPropertiesService properties,
        WhitelistService whitelist, PlayersService players)
    {
        _config = config;
        _host = host;
        _properties = properties;
        _whitelist = whitelist;
        _players = players;
    }

    /// <summary>When the server was last started by someone trying to join, if ever.</summary>
    public DateTime? WokeAtUtc { get; private set; }

    /// <summary>Answers on the server's port while it is stopped, if the owner asked for it.</summary>
    public void Start()
    {
        if (!_config.WakeOnDemand) { _listener.Stop(); return; }

        var port = _properties.GetServerPort(_config.PropertiesPath);
        if (port is null) return;

        if (!_listener.Start(port.Value, BuildStatus, OnJoinAttempt))
            _host.Say(Localizer.Format("Msg_WakePortBusyFmt", port.Value));
    }

    /// <summary>Frees the port: the server is starting, the world is being swapped, or the app is closing.</summary>
    public void Stop() => _listener.Stop();

    /// <summary>Same as <see cref="Stop"/>; lets the owner treat the controller as disposable.</summary>
    public void Dispose() => _listener.Dispose();

    /// <summary>What a client sees while the server sleeps: its own MOTD plus what is going on.</summary>
    private WakeStatus BuildStatus()
    {
        var starting = _host.IsStarting();
        var icon = Path.Combine(_config.FolderPath, ServerIconService.FileName);

        return new WakeStatus(
            Description: WakeSign.Compose(_host.Motd(), WakeSign.Notice(starting)),
            VersionName: string.IsNullOrWhiteSpace(_config.GameVersion) ? "?" : _config.GameVersion,
            MaxPlayers: _host.MaxPlayers(),
            IconPath: File.Exists(icon) ? icon : null,
            DisconnectMessage: WakeSign.KickStyle + Localizer.Get(starting ? "Wake_KickStarting"
                : "Wake_KickWaking"),
            RefusedMessage: WakeSign.KickStyle + Localizer.Get("Wake_KickNotWhitelisted"));
    }

    /// <summary>
    /// Somebody pressed Join on a sleeping server. Returns whether it is being woken for them.
    /// </summary>
    /// <remarks>
    /// Runs on the listener's thread, so reading the server's files here never holds up the UI;
    /// only the start itself goes to the UI thread.
    /// </remarks>
    private bool OnJoinAttempt(string? player)
    {
        if (!MayWake(player)) return false;

        _host.RunOnUi(() =>
        {
            if (!_host.CanStart()) return;   // already coming up from an earlier knock, or being restored

            WokeAtUtc = DateTime.UtcNow;
            _host.Say(Localizer.Get("Msg_WakeStarting"));
            _host.Notify(NotificationKind.WokeOnDemand, Localizer.Get("Notif_Woke"));

            // Unattended: nobody is sitting in front of the app to answer a dialog, which is
            // exactly what the auto-restart flag already means everywhere else.
            _host.StartServer();
        });
        return true;
    }

    /// <summary>Whether <paramref name="player"/> may wake this server.</summary>
    /// <remarks>
    /// Nothing is said in the console about a refusal: the ones worth refusing are scanners, and
    /// they come by the hundred.
    /// </remarks>
    private bool MayWake(string? player)
    {
        try
        {
            var props = _properties.Read(_config.PropertiesPath);
            var whitelistOn = ServerPropertiesService.GetBool(props, "white-list", fallback: false);

            // The two lists are only read when they can change the answer.
            if (!whitelistOn || !_config.WakeOnlyForWhitelist) return true;

            return WakePolicy.Allows(player, whitelistOn, _config.WakeOnlyForWhitelist,
                _whitelist.ReadNames(_config.FolderPath), _players.ReadOps(_config.FolderPath));
        }
        catch
        {
            // The files could not be read this instant: behave as before this rule existed rather
            // than lock everybody out of their own server.
            return true;
        }
    }
}
