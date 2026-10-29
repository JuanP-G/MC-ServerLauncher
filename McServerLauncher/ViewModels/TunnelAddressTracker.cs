using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>What <see cref="TunnelAddressTracker"/> needs to know about, and to be able to do to, its server.</summary>
/// <param name="State">How far along the public address is.</param>
/// <param name="SetState">Shows how far along the public address is.</param>
/// <param name="SetAddress">Shows the public address.</param>
/// <param name="Say">Writes a line in the server's console.</param>
/// <param name="RunOnUi">Runs something on the UI thread; lookups finish on another.</param>
public sealed record TunnelHost(
    Func<TunnelAddressState> State,
    Action<TunnelAddressState> SetState,
    Action<string?> SetAddress,
    Action<string> Say,
    Action<Action> RunOnUi);

/// <summary>
/// Finds out, and keeps up to date, the public address a server's Playit tunnel has — and makes the
/// tunnel in the first place.
/// </summary>
/// <remarks>
/// Every path out of <see cref="RefreshAsync"/> sets a state. It used to return empty-handed in three
/// different situations and say nothing about any of them, which is the whole of the "the address
/// takes for ever to appear" report: most of the time it had appeared on Playit's side and the app
/// was simply not going to ask again for another half minute.
/// </remarks>
public sealed class TunnelAddressTracker
{
    private readonly ServerConfig _config;
    private readonly ServerPropertiesService _properties;
    private readonly PlayitApiService _api;
    private readonly TunnelHost _host;

    public TunnelAddressTracker(ServerConfig config, ServerPropertiesService properties,
        PlayitApiService api, TunnelHost host)
    {
        _config = config;
        _properties = properties;
        _api = api;
        _host = host;
    }

    /// <summary>Gets the tunnel address from the Playit API, matching by port and protocol.</summary>
    /// <param name="fresh">Skips the shared tunnel cache. Used by the burst after a tunnel is made.</param>
    public async Task RefreshAsync(bool fresh = false)
    {
        try
        {
            var port = _properties.GetServerPort(_config.PropertiesPath);
            if (!port.HasValue)
            {
                // No server.properties yet, so no port to match a tunnel against. There is nothing
                // to wait for, which is what the user needs told — not another blank box.
                _host.RunOnUi(() => _host.SetState(TunnelAddressState.NoTunnel));
                return;
            }
            if (!_config.PlayitEnabled) return;

            // Asked as a list, not for one tunnel, so "could not ask" and "the account has no tunnel
            // on this port" stay apart. The second clears the address: kept on screen after a port
            // move, the old one was as often as not another server's tunnel.
            var tunnels = await _api.TryGetTunnelsAsync(fresh);
            if (TunnelAddressSync.Java(tunnels, port.Value) is not { } found)
            {
                _host.RunOnUi(() => _host.SetState(TunnelAddressState.Failed));
                return;
            }

            _host.RunOnUi(() =>
            {
                // A tunnel still waiting for its address keeps nothing stale either: it is new.
                _host.SetAddress(found.Address);
                _host.SetState(found.State);
            });
        }
        catch
        {
            // Best-effort: if the API fails, the saved address is kept — but no longer in silence.
            _host.RunOnUi(() => _host.SetState(TunnelAddressState.Failed));
        }
    }

    /// <summary>
    /// Looks the address up a few times with a growing wait, then leaves it to the ordinary refresh.
    /// </summary>
    /// <remarks>
    /// The Bedrock side has had this since crossplay was written; Java never did, and waited for the
    /// 30-second timer instead. With an arbitrary phase that is anything up to a minute of empty
    /// box — five with the window in the tray — starting from the moment the console says the
    /// address is seconds away.
    /// </remarks>
    public async Task PollAsync()
    {
        foreach (var seconds in AddressRetry.DelaysSeconds)
        {
            if (_host.State() == TunnelAddressState.Ready) return;
            await Task.Delay(TimeSpan.FromSeconds(seconds));

            // fresh: the tunnel list is shared behind a 25-second cache, and without this most of
            // these would be answered by the empty list the first one stored.
            await RefreshAsync(fresh: true);
        }
    }

    /// <summary>
    /// Creates (if it doesn't exist) the Playit tunnel for this server's port, using the write
    /// key. Messages are shown in the server's console.
    /// </summary>
    /// <param name="writeKey">The account's write key.</param>
    /// <param name="serverName">The name the tunnel is given on the account.</param>
    public async Task CreateAsync(string writeKey, string serverName)
    {
        var port = _properties.GetServerPort(_config.PropertiesPath);
        if (!port.HasValue)
        {
            _host.Say(Localizer.Get("Msg_PortUnknown"));
            return;
        }

        try
        {
            _host.Say(string.Format(Localizer.Get("Msg_CreatingTunnel"), port));
            var created = await _api.EnsureMinecraftTunnelAsync(writeKey, serverName, port.Value);
            _host.Say(created
                ? Localizer.Get("Msg_TunnelCreated")
                : string.Format(Localizer.Get("Msg_TunnelExists"), port));

            // The tunnel was made seconds ago, so this first lookup almost always comes back with
            // no address — which is now shown as "waiting" rather than as nothing at all — and the
            // burst picks it up as soon as playit publishes it.
            await RefreshAsync(fresh: true);
            _ = PollAsync();
        }
        catch (Exception ex)
        {
            _host.Say(string.Format(Localizer.Get("Msg_TunnelCreateError"), ErrorMessages.Describe(ex)));
        }
    }
}
