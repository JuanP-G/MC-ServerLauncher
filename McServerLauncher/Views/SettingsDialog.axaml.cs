using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// App settings, grouped in one place (language, notifications, Playit connection, and room for more
/// later). Language and notifications are edited on a copy and applied by the caller on Save; the
/// Playit connection is an action that persists immediately (connect/disconnect).
/// </summary>
public partial class SettingsDialog : Window
{
    public IReadOnlyList<MainViewModel.LanguageOption> Languages { get; }

    /// <summary>The language chosen in the dropdown (read back on Save).</summary>
    public MainViewModel.LanguageOption? SelectedLanguage { get; set; }

    /// <summary>The edited notification settings (a copy; applied by the caller on Save).</summary>
    public NotificationSettings Notifications { get; }

    private readonly AppSettings? _appSettings;
    private readonly AppSettingsService? _settingsService;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public SettingsDialog() : this(new List<MainViewModel.LanguageOption>(), null, new NotificationSettings(), null, null) { }

    public SettingsDialog(IReadOnlyList<MainViewModel.LanguageOption> languages,
        MainViewModel.LanguageOption? currentLanguage, NotificationSettings notifications,
        AppSettings? appSettings, AppSettingsService? settingsService)
    {
        InitializeComponent();
        Languages = languages;
        SelectedLanguage = currentLanguage;
        Notifications = notifications.Clone();
        _appSettings = appSettings;
        _settingsService = settingsService;
        DataContext = this;
        UpdatePlayitStatus();

        // Reflect the embedded agent's live state (downloading / running / failed) so the user can see
        // the app is actually bringing their tunnels online — and retry if the download failed.
        PlayitAgentRunner.Shared.StateChanged += OnAgentStateChanged;
        Closed += (_, _) => PlayitAgentRunner.Shared.StateChanged -= OnAgentStateChanged;
        UpdateAgentStatus(PlayitAgentRunner.Shared.State);
    }

    /// <summary>Reflects the current Playit connection state in the status dot/text and buttons.</summary>
    private void UpdatePlayitStatus()
    {
        var connected = _appSettings is not null && PlayitConnection.IsConnected(_appSettings);
        PlayitDot.Fill = new SolidColorBrush(Color.Parse(connected ? "#3FB950" : "#8B949E"));
        PlayitStatus.Text = Localizer.Get(connected ? "Pk_Connected" : "Pk_NotConnected");
        ConnectBtn.Content = Localizer.Get(connected ? "Pk_Reconnect" : "Pk_Connect");
        DisconnectBtn.IsVisible = connected;
        UpdateAgentStatus(PlayitAgentRunner.Shared.State);
    }

    private void OnAgentStateChanged(AgentRunState state)
        => Dispatcher.UIThread.Post(() => UpdateAgentStatus(state));

    /// <summary>Shows what the embedded Playit agent is doing (only relevant once connected).</summary>
    private void UpdateAgentStatus(AgentRunState state)
    {
        var connected = _appSettings is not null && PlayitConnection.IsConnected(_appSettings);
        // Only meaningful for the partner agent key (the legacy write-key model uses the user's own agent).
        var usesAgent = connected && !string.IsNullOrWhiteSpace(_appSettings?.PlayitAgentSecretKey);
        AgentRow.IsVisible = usesAgent;
        if (!usesAgent) return;

        AgentStatus.Text = state switch
        {
            AgentRunState.Downloading => Localizer.Get("Pk_Agent_Downloading"),
            AgentRunState.Starting => Localizer.Get("Pk_Agent_Starting"),
            AgentRunState.Running => Localizer.Get("Pk_Agent_Running"),
            AgentRunState.Unsupported => Localizer.Get("Pk_Agent_Unsupported"),
            AgentRunState.Failed => string.Format(Localizer.Get("Pk_Agent_Failed"),
                PlayitAgentRunner.Shared.LastError ?? ""),
            _ => Localizer.Get("Pk_Agent_Stopped"),
        };
        AgentStatus.Foreground = new SolidColorBrush(Color.Parse(state switch
        {
            AgentRunState.Running => "#3FB950",
            AgentRunState.Failed => "#F85149",
            _ => "#8B949E",
        }));
        // Offer a manual retry when it isn't up (failed, or stopped with a key present).
        AgentRetryBtn.IsVisible = state is AgentRunState.Failed or AgentRunState.Stopped;
    }

    private void RetryAgent_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_appSettings?.PlayitAgentSecretKey))
            _ = PlayitAgentRunner.Shared.StartAsync(_appSettings.PlayitAgentSecretKey);
    }

    private async void ConnectPlayit_Click(object? sender, RoutedEventArgs e)
    {
        if (_appSettings is null || _settingsService is null) return;
        await PlayitConnection.ConnectAsync(this, _appSettings, _settingsService);
        UpdatePlayitStatus();
    }

    private void DisconnectPlayit_Click(object? sender, RoutedEventArgs e)
    {
        if (_appSettings is null || _settingsService is null) return;
        PlayitConnection.Disconnect(_appSettings, _settingsService);
        UpdatePlayitStatus();
    }

    /// <summary>
    /// Shows a sample toast so the user can see what notifications look like and confirm they appear
    /// on their system. Bypasses the enable/inactive gating on purpose — it's a preview.
    /// </summary>
    private void TestNotification_Click(object? sender, RoutedEventArgs e)
        => ToastService.Shared.Notify("MC Server Launcher", Localizer.Get("Notif_TestBody"));

    private void Save_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
