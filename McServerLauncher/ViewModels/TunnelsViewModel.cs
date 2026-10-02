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

    public bool IsConnected => PlayitConnection.IsConnected(_appSettings);
    public bool IsNotConnected => !IsConnected;
    public string ConnectText => Localizer.Get(IsConnected ? "Pk_Reconnect" : "Pk_Connect");

    private bool UsesAgent => IsConnected && !string.IsNullOrWhiteSpace(_appSettings.PlayitAgentSecretKey);

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

    /// <summary>The account line's dot: green only when connected <em>and</em> the agent is carrying traffic.</summary>
    public IBrush AccountBrush => !IsConnected ? Grey : UsesAgent ? AgentBrush : Green;

    public string AccountText => !IsConnected
        ? Localizer.Get("Pk_NotConnected")
        : UsesAgent ? AgentText : Localizer.Get("Pk_Connected");

    public bool CanRetryAgent => UsesAgent && PlayitAgentRunner.Shared.State is AgentRunState.Failed or AgentRunState.Stopped;

    /// <summary>The old system service, for people who connected with a key instead of the agent.</summary>
    public bool ShowService => IsConnected && !UsesAgent && PlayitManager.Shared.IsInstalled;

    public string ServiceText => string.Format(Localizer.Get("Tun_ServiceFmt"),
        Localizer.Get(PlayitManager.Shared.IsRunning ? "Playit_Active" : "Playit_Stopped"));

    private void RaiseAccount()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsNotConnected));
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
        s.Config.CrossplayEnabled && s.Config.BedrockPort > 0 ? s.Config.BedrockPort : null,
        s.Config.PlayitEnabled)).ToList();

    /// <summary>Puts a reading of the account on screen: the table, the suggestions and the summary.</summary>
    /// <remarks>Public so what the screen looks like can be checked without an account to ask.</remarks>
    public void ShowReport(TunnelReport report)
    {
        Rows.Clear();
        // Trouble first: the rows that need a decision should not be the ones scrolled out of sight.
        foreach (var row in report.Rows.OrderBy(r => r.Health == TunnelHealth.Ok).ThenBy(r => r.Tunnel.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new TunnelRowViewModel(this, row));
        Suggestions.Clear();
        foreach (var s in report.Suggestions)
            Suggestions.Add(new TunnelSuggestionViewModel(this, s));

        LastRead = DateTime.Now;
        RaiseSummary();
    }

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
            var key = PlayitConnection.Credential(_appSettings)!;
            var (_, tunnels) = await _api.GetRunDataAsync(key);

            ShowReport(TunnelInventory.Build(tunnels, Snapshot()));

            // The one thing synced without asking: each server's remembered address follows what the
            // account says. The servers already do this every 30 s; doing it now means a change made
            // here shows up on the server card straight away instead of up to half a minute later.
            PlayitApiService.InvalidateTunnelCache();
            foreach (var server in _servers) _ = server.RefreshTunnelInfoAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
            _busy = false;
            RaiseSummary();
        }
    }

    // ---------------------------------------------------------------- what the person asks for

    /// <summary>Runs a change against the account and reads the result back, reporting a failure on screen.</summary>
    private async Task ChangeAsync(Func<string, Task> change)
    {
        if (PlayitConnection.Credential(_appSettings) is not { } key) return;

        try
        {
            ErrorText = null;
            await change(key);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        await RefreshAsync();
    }

    internal Task RenameAsync(string tunnelId, string name) =>
        ChangeAsync(async key =>
        {
            try { await _api.RenameTunnelAsync(key, tunnelId, name); }
            catch (PlayitApiException ex)
            {
                // The one change this app makes that Playit's own client documents but this key was
                // never promised: say what to do instead of leaving a bare API error.
                throw new InvalidOperationException(string.Format(Localizer.Get("Tun_RenameFailFmt"), ex.Message), ex);
            }
        });

    internal async Task DeleteAsync(string tunnelId, string name)
    {
        var owner = _owner();
        if (!await MessageBox.ConfirmAsync(
                string.Format(Localizer.Get("Tun_DeleteConfirmFmt"), name),
                Localizer.Get("Title_DeleteTunnel"), owner))
            return;

        await ChangeAsync(key => _api.DeleteTunnelAsync(key, tunnelId));
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

            case TunnelSuggestionKind.SharedPort:
                // Changing a port is the one fix that is not ours to make: it is the server's own
                // setting, and only its owner knows what to change it to.
                if (server is not null)
                {
                    await _configure(server);
                    await RefreshAsync();
                }
                break;

            case TunnelSuggestionKind.CreateJava:
                if (server is not null)
                    await ChangeAsync(key => server.CreateTunnelAsync(key));
                break;

            case TunnelSuggestionKind.CreateBedrock:
                if (server is not null)
                    await ChangeAsync(key => server.CreateBedrockTunnelAsync(key));
                break;

            case TunnelSuggestionKind.RenameAll:
                var renames = Rows.Where(r => r.SuggestedName is not null && !r.IsProblem).ToList();
                await ChangeAsync(async key =>
                {
                    foreach (var row in renames)
                        await _api.RenameTunnelAsync(key, row.Id, row.SuggestedName!);
                });
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
        if (PlayitConnection.Credential(_appSettings) is not { } key) return;

        try
        {
            var (_, tunnels) = await _api.GetRunDataAsync(key);
            var all = Snapshot();
            var me = all.FirstOrDefault(s => s.Id == server.Config.Id);
            if (me is null) return;

            var renames = TunnelInventory.RenamesFor(me, oldName, tunnels, all);
            foreach (var (tunnelId, newName) in renames)
            {
                var before = tunnels.First(t => t.Id == tunnelId).Name;
                try
                {
                    await _api.RenameTunnelAsync(key, tunnelId, newName);
                    server.LogLauncher(string.Format(Localizer.Get("Tun_AutoRenamedFmt"), before, newName));
                }
                catch (Exception ex)
                {
                    server.LogLauncher(string.Format(Localizer.Get("Tun_AutoRenameFailFmt"), before, ex.Message));
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
