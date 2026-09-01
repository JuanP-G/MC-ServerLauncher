using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The crossplay half of <see cref="ServerViewModel"/>: Geyser, the Bedrock tunnel and the address.
/// </summary>
/// <remarks>
/// Split out because the file had reached two thousand lines carrying the process, the console, the
/// players, the ports, the tunnels, crossplay, the backups, Java and the notifications. This is the
/// part that has grown most and is likeliest to grow again, and it is a closed subject: everything
/// here is about one question, and nothing outside it needs any of these members.
///
/// A move, not a rewrite. The class was already partial, so not one instruction changed — which is
/// what makes it safe to do at all, and why the whole test suite must give the identical result.
/// </remarks>
public partial class ServerViewModel
{
    private readonly MultiVersionService _multiVersion = new();
    private readonly HydraulicService _hydraulic = new();
    private readonly CrossplayService _crossplay = new();

    /// <summary>The Bedrock host and port, shown separately. Null when there is no Bedrock tunnel.</summary>
    [ObservableProperty]
    private string? _bedrockHost;

    [ObservableProperty]
    private string? _bedrockPortText;

    /// <summary>True once there is a public Bedrock address worth showing.</summary>
    public bool HasBedrockAddress => !string.IsNullOrEmpty(BedrockHost);

    partial void OnBedrockHostChanged(string? value) => OnPropertyChanged(nameof(HasBedrockAddress));

    /// <summary>How far along the Bedrock address is. Drives the explanation under the panel.</summary>
    [ObservableProperty]
    private BedrockAddressState _bedrockState = BedrockAddressState.Waiting;

    partial void OnBedrockStateChanged(BedrockAddressState value) =>
        OnPropertyChanged(nameof(BedrockStateText));

    /// <summary>
    /// Whether to show the Bedrock panel at all: the user asked for crossplay.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> "we successfully looked a tunnel up", which is what the panel used
    /// to be tied to. Everything the user needs in the first minute — the local port, and the fact
    /// that the app is still waiting on playit — is knowable before any lookup succeeds, and hiding
    /// the whole block until one did is what made a working server look like a broken one.
    /// </remarks>
    public bool IsCrossplayOn => Config.CrossplayEnabled;

    /// <summary>The local UDP port Geyser listens on. Known from the moment crossplay is set up.</summary>
    public string BedrockLocalPortText =>
        Config.BedrockPort > 0 ? Config.BedrockPort.ToString() : "—";

    /// <summary>One line saying what is happening, so the panel is never blank without a reason.</summary>
    public string BedrockStateText => Localizer.Get(BedrockAddressStates.KeyFor(BedrockState));

    /// <summary>Re-reads everything about the Bedrock panel that comes from the config.</summary>
    private void RefreshBedrockPanel()
    {
        OnPropertyChanged(nameof(IsCrossplayOn));
        OnPropertyChanged(nameof(BedrockLocalPortText));
    }

    /// <summary>Installs Hydraulic and Fabric API, so Bedrock players see what the mods add.</summary>
    /// <remarks>
    /// The flag is only set once the install has actually succeeded. A server remembering it has
    /// this when the download failed would leave Bedrock players staring at invisible blocks with
    /// the app insisting it was handled.
    /// </remarks>
    public async Task SetUpBedrockModContentAsync()
    {
        if (!HydraulicService.CanEnable(Config.Type))
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_HydraulicUnsupportedFmt"), Config.Type));
            return;
        }

        try
        {
            await _hydraulic.InstallAsync(Config, new Progress<string>(OnConsoleLine));
            RunOnUi(Mods.ReloadInstalled);

            Config.BedrockModContentEnabled = true;
            ConfigChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Config.BedrockModContentEnabled = false;
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
        }
    }

    /// <summary>Installs ViaVersion and ViaBackwards, so other Minecraft versions can join.</summary>
    /// <remarks>
    /// Nothing to configure and no tunnel involved: both plugins work as installed. The flag is
    /// only set once the install has actually succeeded — a server remembering it has them when
    /// the download failed would turn players away and show no reason for it.
    /// </remarks>
    public async Task SetUpMultiVersionAsync()
    {
        if (!MultiVersionService.CanEnable(Config.Type))
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_MultiVersionUnsupportedFmt"), Config.Type));
            return;
        }

        try
        {
            await _multiVersion.InstallAsync(Config, new Progress<string>(OnConsoleLine));

            // Same reason as crossplay: the panel read the folder before these jars existed.
            RunOnUi(Mods.ReloadInstalled);

            Config.MultiVersionEnabled = true;
            ConfigChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Config.MultiVersionEnabled = false;   // it did not happen; do not claim that it did
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
        }
    }

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

        var log = new Progress<string>(OnConsoleLine);

        // Whether this server already had a port written down before today. If it did, a tunnel
        // sitting on it is ours no matter what it is called — which is what keeps renaming the
        // server from making the app treat its own tunnel as a stranger's.
        var portWasAlreadyOurs = Config.BedrockPort > 0;

        try
        {
            if (Config.BedrockPort <= 0)
                Config.BedrockPort = await PickBedrockPortAsync(log);

            // Saved here, not at the end. Everything below can throw — a download, playit's API —
            // and a port that was chosen but never written down is worse than one never chosen: a
            // tunnel may already exist on it while servers.json still says 0, so the next crossplay
            // server is handed the same port and adopts this one's tunnel.
            RunOnUi(RefreshBedrockPanel);
            ConfigChanged?.Invoke();

            await _crossplay.InstallAsync(Config, log);

            // Geyser and Floodgate are mods like any other, and the Mods tab listed the folder
            // before they existed. Left alone it would keep claiming the server has none.
            RunOnUi(Mods.ReloadInstalled);

            // Set as soon as Geyser is installed, before the tunnel — deliberately unlike
            // BedrockModContentEnabled and MultiVersionEnabled, which are only set once everything
            // succeeded. There the flag means "this is done"; here it means "the user wants this,
            // so keep repairing and refreshing it at every start". Leaving it false when the tunnel
            // step fails made RefreshBedrockAddressAsync return on its first line for the rest of
            // that run and every run afterwards, with no way back except switching crossplay off
            // and on again — which is exactly the "it never appears" report.
            Config.CrossplayEnabled = true;
            RunOnUi(RefreshBedrockPanel);
            ConfigChanged?.Invoke();

            int? publicPort = null;
            if (Config.PlayitEnabled && !string.IsNullOrEmpty(playitKey))
                publicPort = await EnsureBedrockTunnelAsync(playitKey!, portWasAlreadyOurs);
            else
                OnConsoleLine(Localizer.Get("Msg_CrossplayNoTunnel"));

            _crossplay.WriteConfig(Config, publicPort);

            OnConsoleLine(Localizer.Get("Msg_CrossplayReady"));
            ConfigChanged?.Invoke();

            // The tunnel was created seconds ago and playit usually has not published its domain
            // and public port yet, so one lookup here almost always comes back empty. Waiting for
            // the ordinary refresh would mean up to 30 seconds of blank panel — five minutes with
            // the window in the tray — during the one minute the user is actually watching.
            _ = PollForBedrockAddressAsync();
        }
        catch (Exception ex)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
        }
    }

    /// <summary>A local UDP port for Geyser, avoiding this app's servers and the playit account.</summary>
    private async Task<int> PickBedrockPortAsync(IProgress<string> log)
    {
        IReadOnlyCollection<int>? accountPorts = null;
        if (Config.PlayitEnabled)
        {
            try { accountPorts = await _playitApi.GetUdpTunnelPortsAsync(); }
            catch { /* reported below, the same as not being able to ask */ }

            if (accountPorts is null) OnConsoleLine(Localizer.Get("Msg_TunnelPortsUnknown"));
        }

        return _crossplay.PickBedrockPort(
            BedrockPortsInUse?.Invoke() ?? Array.Empty<int>(),
            accountPorts ?? Array.Empty<int>(),
            log);
    }

    /// <summary>
    /// Looks the address up a few times with a growing wait, then leaves it to the ordinary refresh.
    /// </summary>
    private async Task PollForBedrockAddressAsync()
    {
        foreach (var seconds in BedrockAddressRetryDelays)
        {
            if (BedrockState == BedrockAddressState.Ready) return;
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await RefreshBedrockAddressAsync();
        }
    }

    /// <summary>
    /// How long to wait between the first few address lookups, in seconds.
    /// </summary>
    /// <remarks>
    /// Growing rather than fixed, and stopping rather than going forever: playit normally publishes
    /// the address within a few seconds, and if it has not after about half a minute the reason is
    /// not one more request. The ordinary 30-second refresh takes over from there.
    /// </remarks>
    private static readonly int[] BedrockAddressRetryDelays = { 2, 3, 5, 8, 13 };

    /// <summary>The name this server's Bedrock tunnel is created under, and recognised by.</summary>
    private string BedrockTunnelName => Name + " (Bedrock)";

    /// <summary>
    /// Creates the Bedrock (UDP) tunnel if it isn't there, and returns its public port.
    /// </summary>
    /// <remarks>
    /// The result of the create matters, which is why it is no longer discarded. Playit's API does
    /// not fail when a tunnel already occupies the port — it reports that one already exists, and
    /// the old code took that as success and went on to read <em>that</em> tunnel's public port and
    /// advertise it as this server's. When the tunnel belonged to another server, both ended up
    /// pointing players at one address, and the second one to be set up appeared to work until
    /// somebody tried to join.
    /// </remarks>
    private async Task<int?> EnsureBedrockTunnelAsync(string playitKey, bool portWasAlreadyOurs)
    {
        var log = new Progress<string>(OnConsoleLine);

        // Two attempts, not a loop: if the port picked to get away from a foreign tunnel is also
        // taken, something is wrong that another round will not fix.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            OnConsoleLine(string.Format(Localizer.Get("Msg_CrossplayTunnelFmt"), Config.BedrockPort));

            var created = await _playitApi.EnsureMinecraftTunnelAsync(
                playitKey, BedrockTunnelName, Config.BedrockPort,
                PlayitApiService.TunnelEdition.Bedrock);

            if (created)
            {
                OnConsoleLine(Localizer.Get("Msg_TunnelCreated"));
                break;
            }

            var existing = await FindBedrockTunnelAsync();
            if (existing is null || IsOurBedrockTunnel(existing, portWasAlreadyOurs && attempt == 0))
            {
                OnConsoleLine(string.Format(Localizer.Get("Msg_TunnelExists"), Config.BedrockPort));
                break;
            }

            if (attempt > 0)
            {
                OnConsoleLine(string.Format(Localizer.Get("Msg_TunnelForeignFmt"),
                    Config.BedrockPort, existing.Name));
                break;
            }

            OnConsoleLine(string.Format(Localizer.Get("Msg_TunnelForeignMovingFmt"),
                Config.BedrockPort, existing.Name));
            Config.BedrockPort = await PickBedrockPortAsync(log);
            RunOnUi(RefreshBedrockPanel);
            ConfigChanged?.Invoke();
        }

        var tunnel = await FindBedrockTunnelAsync();
        return tunnel?.PublicPort > 0 ? tunnel.PublicPort : null;
    }

    /// <summary>
    /// Whether a tunnel on this server's Bedrock port is the one this server created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways to say yes, and the first is the reliable one. <paramref name="portIsOnRecord"/>
    /// means this server already had that port written in servers.json before today, so whatever is
    /// on it now got there for this server — no matter what it is called. That covers the case the
    /// name check gets wrong: rename the server and its own tunnel, created under the old name,
    /// would otherwise look like a stranger's.
    /// </para>
    /// <para>
    /// The name is the fallback, because playit's API offers nothing better: a tunnel carries no
    /// reference to what made it, and the name is the one thing this app writes deterministically.
    /// Renaming the tunnel on playit's own site does defeat it, and the cost of that is one spare
    /// tunnel on a different port rather than two servers advertising one address — the safer of
    /// the two ways to be wrong.
    /// </para>
    /// </remarks>
    private bool IsOurBedrockTunnel(PlayitApiService.PlayitTunnel tunnel, bool portIsOnRecord) =>
        portIsOnRecord || string.Equals(tunnel.Name, BedrockTunnelName, StringComparison.Ordinal);

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

        // Without a tunnel there is nothing to look up, and no amount of polling will change that.
        // Said once, as a state, instead of returning empty-handed every 30 seconds for ever.
        if (!Config.PlayitEnabled)
        {
            RunOnUi(() => BedrockState = BedrockAddressState.LocalOnly);
            return;
        }

        try
        {
            var tunnel = await FindBedrockTunnelAsync();
            if (tunnel?.Address is not { } host || tunnel.PublicPort <= 0)
            {
                // Two different things, and the panel now says which. A tunnel that exists but has
                // no address yet is a few seconds away; no tunnel at all needs the user to act.
                RunOnUi(() => BedrockState = tunnel is null
                    ? BedrockAddressState.LocalOnly
                    : BedrockAddressState.Waiting);
                return;
            }

            RunOnUi(() =>
            {
                BedrockHost = host;
                BedrockPortText = tunnel.PublicPort.ToString();
                BedrockState = BedrockAddressState.Ready;
            });

            _crossplay.WriteConfig(Config, tunnel.PublicPort);
        }
        catch
        {
            // Best-effort, like the Java address: a failed lookup keeps whatever was shown — but it
            // no longer keeps it silently. An empty panel that never explains itself is the bug.
            RunOnUi(() => BedrockState = BedrockAddressState.Failed);
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

    /// <summary>
    /// Copies the Bedrock address and its port together, ready to send to somebody.
    /// </summary>
    /// <remarks>
    /// Two buttons and not one because they are two different jobs. A Bedrock client asks for the
    /// address and the port in separate boxes and refuses "host:port", so pasting into it needs the
    /// host alone — but what people actually do first is send both to a friend, and copying them
    /// one at a time for that is silly. The port here is the PUBLIC one; the local port beside it
    /// is what Geyser listens on and would not work for anybody outside this machine.
    /// </remarks>
    [RelayCommand]
    private async Task CopyBedrockForSharing()
    {
        if (string.IsNullOrEmpty(BedrockHost)) return;
        var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top?.Clipboard is { } cb)
            await cb.SetTextAsync(string.Format(
                Localizer.Get("Addr_ShareFmt"), BedrockHost, BedrockPortText));
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
}
