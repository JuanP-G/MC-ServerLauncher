using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>What <see cref="PlayerAdmin"/> needs to know about, and to be able to do to, its server.</summary>
internal interface IPlayerAdminHost
{
    /// <summary>The server's configuration.</summary>
    ServerConfig Config { get; }

    /// <summary>Whether the server process is up, i.e. commands can be sent to it.</summary>
    bool IsProcessRunning { get; }

    /// <summary>Whether the whitelist switch is shown as on.</summary>
    bool WhitelistEnabled { get; }

    /// <summary>Types a command into the server's console.</summary>
    void SendToConsole(string command);

    /// <summary>Echoes a command the user caused into the console, as the user's own.</summary>
    void Echo(string command);

    /// <summary>Writes a line of the launcher's own in the console.</summary>
    void Say(string line);

    /// <summary>Re-reads the whitelist and its switch from disk into what is on screen.</summary>
    void RefreshWhitelist();

    /// <summary>Re-reads the player lists (ops, bans, known players, whitelist) from disk.</summary>
    void RefreshPlayers();

    /// <summary>Shows the whitelist switch as on or off without reading the file.</summary>
    void ShowWhitelistEnabled(bool on);

    /// <summary>Puts the whitelist switch back on the real state after it flipped itself.</summary>
    void ResyncWhitelistSwitch();
}

/// <summary>
/// What an owner can do to a server's players — op, ban, kick, whitelist — through the server's
/// console while it runs and straight into its files while it does not.
/// </summary>
/// <remarks>
/// <para>
/// The two paths are the point: with the server up, the change must go through its own command or the
/// server overwrites it from memory on the next save; with it down, the file is edited directly.
/// Every method here takes the name as typed and validates it first (see
/// <see cref="ValidNameOrWarn"/>), because the name ends up inside a command sent over stdin.
/// </para>
/// <para>
/// The lists on screen belong to the view model, which hands this class a host to refresh them
/// through. This class only knows how to change the truth underneath.
/// </para>
/// </remarks>
internal sealed class PlayerAdmin
{
    private readonly IPlayerAdminHost _host;
    private readonly ServerPropertiesService _properties;
    private readonly WhitelistService _whitelist;
    private readonly PlayersService _players;

    /// <summary>What the settings page saved into server.properties while the server ran.</summary>
    private readonly PropertiesSavedWhileRunning _savedWhileRunning = new();

    public PlayerAdmin(IPlayerAdminHost host, ServerPropertiesService properties,
        WhitelistService whitelist, PlayersService players)
    {
        _host = host;
        _properties = properties;
        _whitelist = whitelist;
        _players = players;
    }

    private ServerConfig Config => _host.Config;

    /// <summary>Forgets what was saved while running: the next start reads the file as it is.</summary>
    public void ServerStopped() => _savedWhileRunning.Clear();

    // ---------------------------------------------------------------- names

    /// <summary>
    /// Validates a player name before it's embedded in a console command sent over stdin or
    /// written to the whitelist/ban files. Only real Minecraft names pass
    /// (letters/digits/underscore, 1-16 chars — the same rule <see cref="PlayerNames"/>
    /// holds for join/leave detection): this both blocks command injection (a "\n" would add a
    /// second console command) and confusing extra arguments (a name with spaces would turn
    /// "ban a b" into banning "a" with reason "b"). Returns the trimmed name, or null after
    /// logging a clear message when the input isn't a valid name.
    /// </summary>
    public string? ValidNameOrWarn(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (PlayerNames.IsValid(name)) return name;

        // Show what was rejected, defanged for display (no line breaks, capped length).
        var shown = name.Replace("\r", "").Replace("\n", " ");
        if (shown.Length > 32) shown = shown[..32] + "…";
        _host.Say(string.Format(Localizer.Get("Msg_InvalidPlayerNameFmt"), shown));
        return null;
    }

    // ---------------------------------------------------------------- whitelist

    /// <summary>Adds a player to the whitelist and switches the whitelist on if it was off.</summary>
    /// <returns>Whether the name was valid and went in.</returns>
    public async Task<bool> AddToWhitelistAsync(string? typed)
    {
        var name = ValidNameOrWarn(typed);
        if (name is null) return false;

        try
        {
            if (_host.IsProcessRunning)
            {
                _host.Echo($"whitelist add {name}");
                _host.SendToConsole($"whitelist add {name}");
                await Task.Delay(500);
            }
            else
            {
                var props = _properties.Read(Config.PropertiesPath);
                var online = ServerPropertiesService.GetBool(props, "online-mode", fallback: true);
                await _whitelist.AddAsync(Config.FolderPath, name, online);
            }
            _host.RefreshWhitelist();

            // Somebody adding a name means they want a list that counts. Before, the name went in
            // and nothing changed until they also found the switch in Configurar.
            if (!_host.WhitelistEnabled) await SetWhitelistAsync(true);
            return true;
        }
        catch (Exception ex)
        {
            _host.Say(string.Format(Localizer.Get("Msg_WhitelistError"), ErrorMessages.Describe(ex)));
            return false;
        }
    }

    /// <summary>Takes a player off the whitelist.</summary>
    public async Task RemoveFromWhitelistAsync(string? typed)
    {
        if (ValidNameOrWarn(typed) is not { } name) return;

        try
        {
            if (_host.IsProcessRunning)
            {
                _host.Echo($"whitelist remove {name}");
                _host.SendToConsole($"whitelist remove {name}");
                await Task.Delay(500);
            }
            else
            {
                _whitelist.Remove(Config.FolderPath, name);
            }
            _host.RefreshWhitelist();
        }
        catch (Exception ex)
        {
            _host.Say(string.Format(Localizer.Get("Msg_WhitelistError"), ErrorMessages.Describe(ex)));
        }
    }

    /// <summary>
    /// The settings page wrote <paramref name="changes"/> into server.properties. With the server
    /// running they are remembered, and a whitelist switch among them goes to the server as a command.
    /// </summary>
    /// <remarks>
    /// Written to the file, a whitelist switch only took effect on the next start, while the Players
    /// tab's switch takes effect at once: the same setting behaved two ways. And the command makes the
    /// server rewrite the whole file from what it loaded, which is why the rest is remembered — see
    /// <see cref="PropertiesSavedWhileRunning"/>.
    /// </remarks>
    public async Task FollowSavedPropertiesAsync(IReadOnlyDictionary<string, string> changes)
    {
        if (!_host.IsProcessRunning || changes.Count == 0) return;

        _savedWhileRunning.Note(changes);
        if (changes.ContainsKey("white-list"))
            await SetWhitelistAsync(ServerPropertiesService.GetBool(changes, "white-list", fallback: false));
    }

    /// <summary>Switches the whitelist on or off, keeping whoever is on it.</summary>
    /// <remarks>
    /// While the server runs, through its own command, which applies it at once and writes
    /// server.properties itself — from the values it loaded at start, so whatever was saved since is
    /// written again after it. While it is stopped, straight into the file. whitelist.json is never
    /// touched, so switching it off and on again gives back the same list.
    /// </remarks>
    public async Task SetWhitelistAsync(bool on)
    {
        try
        {
            if (_host.IsProcessRunning)
            {
                var command = on ? "whitelist on" : "whitelist off";
                _host.Echo(command);
                _host.SendToConsole(command);
                await Task.Delay(500);

                if (_savedWhileRunning.ToRestore() is { Count: > 0 } owed)
                    _properties.Update(Config.PropertiesPath, owed);
            }
            else
            {
                _properties.Update(Config.PropertiesPath,
                    new Dictionary<string, string> { ["white-list"] = on ? "true" : "false" });
            }
            _host.RefreshWhitelist();

            // The server writes the file when it gets to it; what was asked is what it will be.
            if (_host.IsProcessRunning) _host.ShowWhitelistEnabled(on);
        }
        catch (Exception ex)
        {
            _host.Say(string.Format(Localizer.Get("Msg_WhitelistError"), ErrorMessages.Describe(ex)));
        }
        finally
        {
            // The switch flips itself when clicked; this puts it back on the real state if that failed.
            _host.ResyncWhitelistSwitch();
        }
    }

    // ---------------------------------------------------------------- commands on a player

    /// <summary>Makes a player an operator. Needs the server running.</summary>
    public Task OpAsync(string? typed) => RunningCommandAsync(typed, "op", "Action_Op");

    /// <summary>Takes operator rights from a player. Needs the server running.</summary>
    public Task DeopAsync(string? typed) => RunningCommandAsync(typed, "deop", "Action_Deop");

    /// <summary>Kicks a player. Needs the server running.</summary>
    public Task KickAsync(string? typed) => RunningCommandAsync(typed, "kick", "Action_Kick");

    /// <summary>Bans a player. Needs the server running.</summary>
    public Task BanAsync(string? typed) => RunningCommandAsync(typed, "ban", "Action_Ban");

    /// <summary>Lifts a ban: by command while the server runs, by editing the ban list otherwise.</summary>
    public async Task PardonAsync(string? typed)
    {
        if (ValidNameOrWarn(typed) is not { } name) return;
        if (_host.IsProcessRunning)
        {
            await CommandAsync($"pardon {name}");
        }
        else
        {
            _players.Unban(Config.FolderPath, name);
            _host.RefreshPlayers();
        }
    }

    private async Task RunningCommandAsync(string? typed, string verb, string actionKey)
    {
        if (ValidNameOrWarn(typed) is not { } name || !EnsureRunning(Localizer.Get(actionKey))) return;
        await CommandAsync($"{verb} {name}");
    }

    private bool EnsureRunning(string action)
    {
        if (_host.IsProcessRunning) return true;
        _host.Say(string.Format(Localizer.Get("Msg_NeedRunningFor"), action));
        return false;
    }

    private async Task CommandAsync(string command)
    {
        _host.Echo(command);
        _host.SendToConsole(command);
        await Task.Delay(450);
        _host.RefreshPlayers();
    }
}
