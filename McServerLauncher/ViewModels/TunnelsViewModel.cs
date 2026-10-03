using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
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
/// The tunnels screen: the Playit account, every tunnel on it, and what to do about them.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the account used to be visible only one server at a time, from inside each
/// server, and connecting to it lived at the bottom of Settings. Nobody could see that a tunnel
/// pointed at nothing, or that two servers were fighting for one, because no screen had both lists
/// in front of it. The rules for reading them are <see cref="TunnelInventory"/>; this class only
/// asks the account, shows the answer, and carries out what the person clicks.
/// </para>
/// <para>
/// Nothing destructive happens by itself. The one thing done without asking is renaming a tunnel
/// that still carries the exact name the app gave it, after its server was renamed — and then only
/// that: a name somebody chose is left alone.
/// </para>
/// </remarks>
public partial class TunnelsViewModel : ObservableObject
{
    private static readonly IBrush Green = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Grey = new ImmutableSolidColorBrush(Color.Parse("#8B949E"));
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Color.Parse("#F85149"));
    private static readonly IBrush Amber = new ImmutableSolidColorBrush(Color.Parse("#E3A82B"));

    private readonly ObservableCollection<ServerViewModel> _servers;
    private readonly AppSettings _appSettings;
    private readonly AppSettingsService _settingsService;
    private readonly Func<Window?> _owner;
    private readonly Func<ServerViewModel, Task> _configure;
    private readonly PlayitApiService _api = new();
    private readonly ServerPropertiesService _props = new();
    private bool _busy;

    public TunnelsViewModel(ObservableCollection<ServerViewModel> servers, AppSettings appSettings,
        AppSettingsService settingsService, Func<Window?> owner, Func<ServerViewModel, Task> configure)
    {
        _servers = servers;
        _appSettings = appSettings;
        _settingsService = settingsService;
        _owner = owner;
        _configure = configure;

        PlayitAgentRunner.Shared.StateChanged += _ => Dispatcher.UIThread.Post(RaiseAccount);
        PlayitManager.Shared.StateChanged += _ => Dispatcher.UIThread.Post(RaiseAccount);
    }

    public ObservableCollection<TunnelRowViewModel> Rows { get; } = new();
    public ObservableCollection<TunnelSuggestionViewModel> Suggestions { get; } = new();

    // ---------------------------------------------------------------- the account

    // Every way this machine has into the account. Read each time rather than kept: the installed
    // agent can appear or go while the app runs, and the key it holds is cached for a few seconds
    // by the service that reads it, so asking again is cheap.
    private IReadOnlyList<PlayitConnection.Source> Sources() => PlayitConnection.Sources(_appSettings);

    // What the last reading found out about each way in.
    private readonly HashSet<PlayitConnection.SourceKind> _refused = new();
    private readonly Dictionary<string, string> _keyFor = new();
    private List<string> _workingKeys = new();

    // Which of the keys that answered may also change tunnels. Asked without changing anything; see
    // PlayitApiService.CanManageTunnelsAsync. A key not asked yet is given the benefit of the doubt.
    private readonly Dictionary<string, bool> _writable = new();

    private bool CanWrite(string? key) => key is null || !_writable.TryGetValue(key, out var w) || w;

    /// <summary>Whether this tunnel can be changed from here, with the key that read it.</summary>
    private bool CanChange(string? tunnelId) => CanWrite(KeyFor(tunnelId));

    /// <summary>A key that may create tunnels, or null when every key that answered can only read.</summary>
    private string? WritableKey() => _workingKeys.FirstOrDefault(k => CanWrite(k));

    /// <summary>Changes Playit will not let this app make go through its website, where the account owner can.</summary>
    internal void OpenWebForChange() => BrowserLauncher.Open(AppLinks.PlayitTunnels);

    private bool IsReadOnly(PlayitConnection.Source source) =>
        _writable.TryGetValue(source.Key, out var w) && !w;

    /// <summary>True when there is any way into the account: the app's own agent, the installed one, or a saved key.</summary>
    public bool IsConnected => Sources().Count > 0;
    public bool IsNotConnected => !IsConnected;

    /// <summary>True when the connect flow has saved something — which is what Disconnect forgets.</summary>
    public bool HasStoredConnection => PlayitConnection.IsConnected(_appSettings);

    public string ConnectText => Localizer.Get(HasStoredConnection ? "Pk_Reconnect" : "Pk_Connect");

    private bool UsesAgent => !string.IsNullOrWhiteSpace(_appSettings.PlayitAgentSecretKey);

    public bool ShowAgent => UsesAgent;

    public string AgentText => PlayitAgentRunner.Shared.State switch
    {
        AgentRunState.Downloading => Localizer.Get("Pk_Agent_Downloading"),
        AgentRunState.Starting => Localizer.Get("Pk_Agent_Starting"),
        AgentRunState.Running => Localizer.Get("Pk_Agent_Running"),
        AgentRunState.Unsupported => Localizer.Get("Pk_Agent_Unsupported"),
        AgentRunState.Failed => string.Format(Localizer.Get("Pk_Agent_Failed"), PlayitAgentRunner.Shared.LastError ?? ""),
        _ => Localizer.Get("Pk_Agent_Stopped"),
    };

    public IBrush AgentBrush => PlayitAgentRunner.Shared.State switch
    {
        AgentRunState.Running => Green,
        AgentRunState.Failed => Red,
        AgentRunState.Downloading or AgentRunState.Starting => Amber,
        _ => Grey,
    };

    /// <summary>
    /// The account line's dot: grey with no way in, red when Playit refused every one, green otherwise
    /// — except that the app's own agent being down is shown, since it is the one carrying the traffic.
    /// </summary>
    public IBrush AccountBrush
    {
        get
        {
            var sources = Sources();
            if (sources.Count == 0) return Grey;
            if (_refused.Count == sources.Count) return Red;
            return UsesAgent && PlayitAgentRunner.Shared.State != AgentRunState.Running ? AgentBrush : Green;
        }
    }

    /// <summary>One line for each way in, so it is clear which one the tunnels below were read with.</summary>
    public string AccountText
    {
        get
        {
            var sources = Sources();
            if (sources.Count == 0) return Localizer.Get("Pk_NotConnected");

            return string.Join("\n", sources.Select(source =>
            {
                var line = source.Kind switch
                {
                    PlayitConnection.SourceKind.AppAgent => AgentText,
                    PlayitConnection.SourceKind.InstalledAgent => Localizer.Get(
                        IsReadOnly(source) ? "Tun_Src_InstalledReadOnly" : "Tun_Src_Installed"),
                    _ => Localizer.Get("Tun_Src_Saved"),
                };
                return _refused.Contains(source.Kind) ? line + Localizer.Get("Tun_Src_Refused") : line;
            }));
        }
    }

    public bool CanRetryAgent => UsesAgent && PlayitAgentRunner.Shared.State is AgentRunState.Failed or AgentRunState.Stopped;

    /// <summary>The system service of an agent the user installed, to start and stop it from here.</summary>
    public bool ShowService => IsConnected && !UsesAgent && PlayitManager.Shared.IsInstalled;

    public string ServiceText => string.Format(Localizer.Get("Tun_ServiceFmt"),
        Localizer.Get(PlayitManager.Shared.IsRunning ? "Playit_Active" : "Playit_Stopped"));

    private void RaiseAccount()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsNotConnected));
        OnPropertyChanged(nameof(HasStoredConnection));
        OnPropertyChanged(nameof(ConnectText));
        OnPropertyChanged(nameof(ShowAgent));
        OnPropertyChanged(nameof(AgentText));
        OnPropertyChanged(nameof(AgentBrush));
        OnPropertyChanged(nameof(AccountBrush));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(CanRetryAgent));
        OnPropertyChanged(nameof(ShowService));
        OnPropertyChanged(nameof(ServiceText));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (_owner() is not { } owner) return;
        await PlayitConnection.ConnectAsync(owner, _appSettings, _settingsService);
        RaiseAccount();
        await RefreshAsync();
    }

    [RelayCommand]
    private void Disconnect()
    {
        PlayitConnection.Disconnect(_appSettings, _settingsService);
        Rows.Clear();
        Suggestions.Clear();
        RaiseSummary();
        RaiseAccount();
    }

    [RelayCommand]
    private void RetryAgent()
    {
        if (!string.IsNullOrWhiteSpace(_appSettings.PlayitAgentSecretKey))
            _ = PlayitAgentRunner.Shared.StartAsync(_appSettings.PlayitAgentSecretKey);
    }

    [RelayCommand]
    private async Task ToggleService()
    {
        try
        {
            if (PlayitManager.Shared.IsRunning) await PlayitManager.Shared.StopServiceAsync();
            else await PlayitManager.Shared.StartServiceAsync();
        }
        catch (Exception ex)
        {
            ErrorText = string.Format(Localizer.Get("Msg_PlayitServiceChangeFail"), ex.Message);
        }
        RaiseAccount();
    }

    [RelayCommand]
    private void OpenWeb() => BrowserLauncher.Open(AppLinks.PlayitTunnels);

    // ---------------------------------------------------------------- what the account says

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(LastReadText))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastReadText), nameof(ShowEmpty))]
    private DateTime? _lastRead;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>The table's empty state, shown only once the account has actually answered.</summary>
    public bool ShowEmpty => IsConnected && LastRead is not null && !IsLoading && Rows.Count == 0 && !HasError;

    public string LastReadText => IsLoading
        ? Localizer.Get("Tun_Loading")
        : LastRead is { } at ? string.Format(Localizer.Get("Tun_LastReadFmt"), at.ToString("t")) : "";

    public bool AllGood => Rows.Count > 0 && Rows.All(r => !r.IsProblem) && !Suggestions.Any(s => s.IsProblem);

    public string SummaryText => Rows.Count == 0
        ? ""
        : Suggestions.Count(s => s.IsProblem) is var n and > 0
            ? string.Format(Localizer.Get("Tun_Summary_ProblemsFmt"), n)
            : Localizer.Get("Tun_Summary_Ok");

    public IBrush SummaryBrush => AllGood ? Green : Amber;

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(AllGood));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SummaryBrush));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>Everything the screen needs to know about each server, taken fresh each time.</summary>
    /// <remarks>
    /// Read live rather than kept: servers are added, renamed and have their ports changed while the
    /// app runs, and a snapshot taken at startup would go stale the first time any of that happened.
    /// </remarks>
    private List<ServerPorts> Snapshot() => _servers.Select(s => new ServerPorts(
        s.Config.Id,
        s.Name,
        _props.GetServerPort(s.Config.PropertiesPath),
        CrossplayService.EffectiveBedrockPort(s.Config),
        s.Config.PlayitEnabled)).ToList();

    /// <summary>Puts a reading of the account on screen: the table, the suggestions and the summary.</summary>
    /// <remarks>Public so what the screen looks like can be checked without an account to ask.</remarks>
    public void ShowReport(TunnelReport report)
    {
        Rows.Clear();
        // Trouble first: the rows that need a decision should not be the ones scrolled out of sight.
        foreach (var row in report.Rows.OrderBy(r => r.Health == TunnelHealth.Ok).ThenBy(r => r.Tunnel.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new TunnelRowViewModel(this, row, CanChange(row.Tunnel.Id)));
        Suggestions.Clear();
        foreach (var s in report.Suggestions)
            Suggestions.Add(new TunnelSuggestionViewModel(this, s, CanFixHere(s, report)));

        LastRead = DateTime.Now;
        RaiseSummary();
    }

    /// <summary>What reading the account with every way in came back with.</summary>
    private sealed record Reading(
        List<PlayitApiService.PlayitTunnel> Tunnels, Dictionary<string, string> KeyFor,
        List<string> WorkingKeys, HashSet<PlayitConnection.SourceKind> Refused, Exception? Error);

    /// <summary>
    /// Asks the account with <em>each</em> way in and puts the answers together.
    /// </summary>
    /// <remarks>
    /// Not just the first that works: the app's agent and one the user installed are different agents
    /// of the same account, and each is told only about its own tunnels. A key that Playit rejects is
    /// noted and the others carry on — one stale key must not hide tunnels another can see. Each
    /// tunnel remembers which key read it, because that is the one to change it with.
    /// </remarks>
    private async Task<Reading> ReadAsync()
    {
        var tunnels = new List<PlayitApiService.PlayitTunnel>();
        var keyFor = new Dictionary<string, string>();
        var working = new List<string>();
        var refused = new HashSet<PlayitConnection.SourceKind>();
        Exception? last = null;

        foreach (var source in Sources())
        {
            try
            {
                var (_, list) = await _api.GetRunDataAsync(source.Key);
                working.Add(source.Key);
                foreach (var tunnel in list)
                {
                    // Seen through two agents is still one tunnel.
                    if (!string.IsNullOrEmpty(tunnel.Id) && keyFor.ContainsKey(tunnel.Id)) continue;
                    tunnels.Add(tunnel);
                    if (!string.IsNullOrEmpty(tunnel.Id)) keyFor[tunnel.Id] = source.Key;
                }
            }
            catch (Exception ex)
            {
                last = ex;
                if (ex is PlayitApiException { IsAuthError: true }) refused.Add(source.Kind);
            }
        }

        return new Reading(tunnels, keyFor, working, refused, working.Count == 0 ? last : null);
    }

    /// <summary>What to tell a person about a failure, rather than what the API said.</summary>
    private static string Describe(Exception? ex, bool write = false) => ex switch
    {
        null => "",
        PlayitApiException { IsReadOnlyRefusal: true } => Localizer.Get("Tun_Err_ReadOnly"),
        PlayitApiException { IsAuthError: true } => Localizer.Get(write ? "Tun_Err_NoPermission" : "Tun_Err_Key"),
        _ => ex.Message,
    };

    /// <summary>Whether a suggestion can be carried out from here, or only on playit.gg.</summary>
    private bool CanFixHere(TunnelSuggestion s, TunnelReport report) => s.Kind switch
    {
        TunnelSuggestionKind.DeleteOrphan or TunnelSuggestionKind.DeleteDuplicate => CanChange(s.TunnelId),
        TunnelSuggestionKind.CreateJava or TunnelSuggestionKind.CreateBedrock => WritableKey() is not null,
        _ => true,   // a shared port is fixed on this machine, not in the account
    };

    [RelayCommand]
    public async Task RefreshAsync()
    {
        RaiseAccount();
        if (!IsConnected || _busy) return;

        _busy = true;
        IsLoading = true;
        ErrorText = null;
        try
        {
            var reading = await ReadAsync();

            _refused.Clear();
            foreach (var kind in reading.Refused) _refused.Add(kind);
            _keyFor.Clear();
            foreach (var (id, key) in reading.KeyFor) _keyFor[id] = key;
            _workingKeys = reading.WorkingKeys;
            foreach (var key in _workingKeys)
                if (await _api.CanManageTunnelsAsync(key) is { } writable)
                    _writable[key] = writable;

            if (reading.Error is not null)
            {
                // Nothing could be read: say why, and leave the table as it was rather than empty,
                // which would read as "you have no tunnels".
                ErrorText = Describe(reading.Error);
                return;
            }

            ShowReport(TunnelInventory.Build(reading.Tunnels, Snapshot()));

            // The one thing synced without asking: each server's remembered address follows what the
            // account says. The servers already do this every 30 s; doing it now means a change made
            // here shows up on the server card straight away instead of up to half a minute later.
            PlayitApiService.InvalidateTunnelCache();
            foreach (var server in _servers) _ = server.RefreshTunnelInfoAsync();
        }
        catch (Exception ex)
        {
            ErrorText = Describe(ex);
        }
        finally
        {
            IsLoading = false;
            _busy = false;
            RaiseAccount();
            RaiseSummary();
        }
    }

    // ---------------------------------------------------------------- what the person asks for

    /// <summary>The key that can change this tunnel: the one that read it, else the first that works.</summary>
    private string? KeyFor(string? tunnelId) =>
        tunnelId is not null && _keyFor.TryGetValue(tunnelId, out var key)
            ? key
            : _workingKeys.FirstOrDefault() ?? Sources().FirstOrDefault()?.Key;

    /// <summary>Runs a change against the account and reads the result back, reporting a failure on screen.</summary>
    private async Task ChangeAsync(Func<string, Task> change, string? tunnelId = null)
    {
        if (KeyFor(tunnelId) is not { } key) return;

        try
        {
            ErrorText = null;
            await change(key);
        }
        catch (Exception ex)
        {
            ErrorText = Describe(ex, write: true);
        }
        await RefreshAsync();
    }

    internal Task RenameAsync(string tunnelId, string name) =>
        ChangeAsync(async key =>
        {
            try { await _api.RenameTunnelAsync(key, tunnelId, name); }
            catch (PlayitApiException ex)
            {
                // Said as what to do next, not as what the API said: renaming is the one change here
                // that a key may be refused for even though it can read, and playit.gg can always do it.
                throw new InvalidOperationException(
                    string.Format(Localizer.Get("Tun_RenameFailFmt"), Describe(ex, write: true)), ex);
            }
        }, tunnelId);

    internal async Task DeleteAsync(string tunnelId, string name)
    {
        var owner = _owner();
        if (!await MessageBox.ConfirmAsync(
                string.Format(Localizer.Get("Tun_DeleteConfirmFmt"), name),
                Localizer.Get("Title_DeleteTunnel"), owner))
            return;

        await ChangeAsync(key => _api.DeleteTunnelAsync(key, tunnelId), tunnelId);
    }

    internal async Task CopyAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || text == "—") return;
        if (_owner()?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    internal async Task ApplyAsync(TunnelSuggestion suggestion)
    {
        var server = suggestion.ServerId is null ? null : _servers.FirstOrDefault(s => s.Config.Id == suggestion.ServerId);

        switch (suggestion.Kind)
        {
            case TunnelSuggestionKind.DeleteOrphan:
            case TunnelSuggestionKind.DeleteDuplicate:
                if (suggestion.TunnelId is { } id) await DeleteAsync(id, suggestion.Subject);
                break;

            case TunnelSuggestionKind.SharedPort when suggestion.Udp:
                // The Bedrock port lives in servers.json and Geyser's config, not in server.properties,
                // and any free port will do: nothing about it is the owner's choice to make here.
                if (server is not null)
                    await ChangeAsync(_ => server.MoveBedrockPortAsync(WritableKey()));
                break;

            case TunnelSuggestionKind.SharedPort:
                // A Java port is the server's own setting, and the owner may care which number it is:
                // open the server's configuration instead of picking one for them.
                if (server is not null)
                {
                    await _configure(server);
                    await RefreshAsync();
                }
                break;

            case TunnelSuggestionKind.CreateJava:
                if (server is not null && WritableKey() is { } javaKey)
                    await ChangeAsync(_ => server.CreateTunnelAsync(javaKey));
                break;

            case TunnelSuggestionKind.CreateBedrock:
                if (server is not null && WritableKey() is { } bedrockKey)
                    await ChangeAsync(_ => server.CreateBedrockTunnelAsync(bedrockKey));
                break;

        }
    }

    // ---------------------------------------------------------------- following a renamed server

    /// <summary>
    /// After a server was renamed, gives its tunnels the matching name — those that still carry the
    /// name the app gave them. Quiet: it reports in that server's console and never opens a dialog.
    /// </summary>
    public async Task RenameTunnelsForServerAsync(ServerViewModel server, string oldName)
    {
        if (string.Equals(oldName, server.Name, StringComparison.Ordinal)) return;
        if (!IsConnected) return;

        try
        {
            var reading = await ReadAsync();
            var all = Snapshot();
            var me = all.FirstOrDefault(s => s.Id == server.Config.Id);
            if (me is null) return;

            var renames = TunnelInventory.RenamesFor(me, oldName, reading.Tunnels, all);
            foreach (var (tunnelId, newName) in renames)
            {
                var before = reading.Tunnels.First(t => t.Id == tunnelId).Name;
                if (await _api.CanManageTunnelsAsync(reading.KeyFor[tunnelId]) == false)
                {
                    server.LogLauncher(string.Format(Localizer.Get("Tun_AutoRenameReadOnlyFmt"), before, newName));
                    continue;
                }
                try
                {
                    await _api.RenameTunnelAsync(reading.KeyFor[tunnelId], tunnelId, newName);
                    server.LogLauncher(string.Format(Localizer.Get("Tun_AutoRenamedFmt"), before, newName));
                }
                catch (Exception ex)
                {
                    server.LogLauncher(string.Format(Localizer.Get("Tun_AutoRenameFailFmt"), before, Describe(ex, write: true)));
                }
            }

            if (renames.Count > 0) await RefreshAsync();
        }
        catch
        {
            // No connection, or the account would not say: the tunnels keep their old names, which
            // still work. Not worth interrupting a rename for.
        }
    }
}
