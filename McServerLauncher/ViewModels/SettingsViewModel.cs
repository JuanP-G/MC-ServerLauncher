using System.IO;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>The pages of the settings screen.</summary>
public enum SettingsPage
{
    General,
    Notifications,
    Colors,
    Players,
}

/// <summary>
/// The settings screen: language, window behaviour, the desktop shortcut, notifications, colours
/// and the player history.
/// </summary>
/// <remarks>
/// <para>
/// It used to be a dialog with Cancel and Save, editing a copy. As a screen of the app it saves as
/// it goes: every change lands in the one <see cref="AppSettings"/> the app runs on and is written
/// to disk straight away, so there is no "did I save that?" and nothing to lose by switching away.
/// </para>
/// <para>
/// The one thing not saved as typed is a colour that is not a colour yet. <c>#E0</c> is what a box
/// holds half way through typing <c>#E05561</c>; written to disk it would come back next launch as
/// a broken value. The box keeps what was typed; the file only ever gets something drawable.
/// </para>
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    private static readonly IBrush Good = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Bad = new ImmutableSolidColorBrush(Color.Parse("#E05561"));

    private readonly MainViewModel? _main;
    private readonly AppSettings _settings;
    private readonly AppSettingsService _service;
    private readonly Action _applyConsoleColours;
    private readonly Action _applyWindowBehavior;

    /// <param name="main">For the language, which the main view model already persists; null in tests.</param>
    /// <param name="settings">The settings the app runs on, edited in place.</param>
    /// <param name="service">Writes them to disk.</param>
    /// <param name="applyConsoleColours">Repaints the consoles after a console colour changes.</param>
    /// <param name="applyWindowBehavior">Applies the tray options after they change.</param>
    public SettingsViewModel(MainViewModel? main, AppSettings settings, AppSettingsService service,
        Action applyConsoleColours, Action applyWindowBehavior)
    {
        _main = main;
        _settings = settings;
        _service = service;
        _applyConsoleColours = applyConsoleColours;
        _applyWindowBehavior = applyWindowBehavior;

        _colorSuccess = settings.Notifications.ColorSuccess;
        _colorInfo = settings.Notifications.ColorInfo;
        _colorWarning = settings.Notifications.ColorWarning;
        _colorError = settings.Notifications.ColorError;
        _consoleChatColor = settings.ConsoleChatColor;
        _consolePlayersColor = settings.ConsolePlayersColor;

        var history = (settings.PlayerHistory ?? new PlayerHistorySettings()).Clamped();
        _historyEnabled = history.Enabled;
        _historyRecordChat = history.RecordChat;
        _historyMaxEvents = history.MaxEventsPerPlayer;
        _historyRetentionDays = history.RetentionDays;
    }

    /// <summary>Where saving happens. A parameter only so a test can watch it instead of touching the disk.</summary>
    internal Action<AppSettings>? SaveOverride { get; set; }

    // ---------------------------------------------------------------- pages

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralPage), nameof(IsNotificationsPage), nameof(IsColorsPage), nameof(IsPlayersPage))]
    private SettingsPage _page = SettingsPage.General;

    public bool IsGeneralPage => Page == SettingsPage.General;
    public bool IsNotificationsPage => Page == SettingsPage.Notifications;
    public bool IsColorsPage => Page == SettingsPage.Colors;
    public bool IsPlayersPage => Page == SettingsPage.Players;

    [RelayCommand] private void ShowGeneral() => Page = SettingsPage.General;
    [RelayCommand] private void ShowNotifications() => Page = SettingsPage.Notifications;
    [RelayCommand] private void ShowColors() => Page = SettingsPage.Colors;

    [RelayCommand]
    private void ShowPlayers()
    {
        Page = SettingsPage.Players;
        OnPropertyChanged(nameof(HistorySizeText));   // it grows while servers run
    }

    // ---------------------------------------------------------------- player history

    [ObservableProperty] private bool _historyEnabled;
    [ObservableProperty] private bool _historyRecordChat;
    [ObservableProperty] private decimal _historyMaxEvents;
    [ObservableProperty] private decimal _historyRetentionDays;

    partial void OnHistoryEnabledChanged(bool value) => ApplyHistory();
    partial void OnHistoryRecordChatChanged(bool value) => ApplyHistory();
    partial void OnHistoryMaxEventsChanged(decimal value) => ApplyHistory();
    partial void OnHistoryRetentionDaysChanged(decimal value) => ApplyHistory();

    /// <summary>How much the history takes on disk, for every server together.</summary>
    public string HistorySizeText => string.Format(Localizer.Get("History_SizeFmt"),
        FormatBytes(PlayerHistoryStore.SizeOnDisk()));

    /// <summary>
    /// Puts the history settings to work straight away: the app-wide preferences, every server's
    /// recorder, and the file.
    /// </summary>
    private void ApplyHistory()
    {
        _settings.PlayerHistory = new PlayerHistorySettings
        {
            Enabled = HistoryEnabled,
            RecordChat = HistoryRecordChat,
            MaxEventsPerPlayer = (int)HistoryMaxEvents,
            RetentionDays = (int)HistoryRetentionDays,
        }.Clamped();
        PlayerHistoryPreferences.Current = _settings.PlayerHistory;
        _main?.OnHistorySettingsChanged();
        Persist();
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.#") + " MB",
    };

    // ---------------------------------------------------------------- general

    public IReadOnlyList<MainViewModel.LanguageOption> Languages =>
        _main?.Languages ?? Array.Empty<MainViewModel.LanguageOption>();

    /// <summary>The main view model already persists the language and offers the restart it needs.</summary>
    public MainViewModel.LanguageOption? SelectedLanguage
    {
        get => _main?.SelectedLanguage;
        set
        {
            if (_main is null || value is null || ReferenceEquals(value, _main.SelectedLanguage)) return;
            _main.SelectedLanguage = value;
            OnPropertyChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value) return;
            _settings.MinimizeToTray = value;
            _applyWindowBehavior();
            OnPropertyChanged();
            Persist();
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            if (_settings.CloseToTray == value) return;
            _settings.CloseToTray = value;
            _applyWindowBehavior();
            OnPropertyChanged();
            Persist();
        }
    }

    /// <summary>Whether the update check offers betas as well as stable versions.</summary>
    /// <remarks>
    /// Asks again straight away, so the answer on screen follows the switch: turning it off takes
    /// back a beta already being offered, and turning it on shows one that is there.
    /// </remarks>
    public bool ReceiveBetas
    {
        get => _settings.ReceiveBetas;
        set
        {
            if (_settings.ReceiveBetas == value) return;
            _settings.ReceiveBetas = value;
            OnPropertyChanged();
            Persist();
            _main?.OnReceiveBetasChanged();
        }
    }

    [ObservableProperty]
    private string? _shortcutStatus;

    [ObservableProperty]
    private IBrush? _shortcutStatusBrush;

    /// <summary>Puts the app on the desktop, and says so next to the button rather than in a dialog.</summary>
    [RelayCommand]
    private void CreateShortcut()
    {
        try
        {
            var path = DesktopShortcutService.Create();
            ShortcutStatus = string.Format(Localizer.Get("Shortcut_DoneFmt"), Path.GetFileName(path));
            ShortcutStatusBrush = Good;
        }
        catch (Exception ex)
        {
            ShortcutStatus = ex.Message;
            ShortcutStatusBrush = Bad;
        }
    }

    // ---------------------------------------------------------------- notifications

    /// <summary>
    /// The live notification settings: the same object <see cref="NotificationPreferences.Global"/>
    /// points at, so a switch flipped here takes effect on the next notification without any copying.
    /// </summary>
    public NotificationSettings Notifications => _settings.Notifications;

    // ---------------------------------------------------------------- colours

    [ObservableProperty] private string _colorSuccess;
    [ObservableProperty] private string _colorInfo;
    [ObservableProperty] private string _colorWarning;
    [ObservableProperty] private string _colorError;
    [ObservableProperty] private string _consoleChatColor;
    [ObservableProperty] private string _consolePlayersColor;

    partial void OnColorSuccessChanged(string value) => SetLevel(NotificationLevel.Success, value);
    partial void OnColorInfoChanged(string value) => SetLevel(NotificationLevel.Info, value);
    partial void OnColorWarningChanged(string value) => SetLevel(NotificationLevel.Warning, value);
    partial void OnColorErrorChanged(string value) => SetLevel(NotificationLevel.Error, value);

    private void SetLevel(NotificationLevel level, string value)
    {
        if (!NotificationPalette.IsValid(value)) return;
        _settings.Notifications.SetColorFor(level, value.Trim());
        Persist();
    }

    partial void OnConsoleChatColorChanged(string value)
    {
        if (!NotificationPalette.IsValid(value)) return;
        _settings.ConsoleChatColor = value.Trim();
        _applyConsoleColours();
        Persist();
    }

    partial void OnConsolePlayersColorChanged(string value)
    {
        if (!NotificationPalette.IsValid(value)) return;
        _settings.ConsolePlayersColor = value.Trim();
        _applyConsoleColours();
        Persist();
    }

    /// <summary>
    /// One sample toast per level, drawn with the colours as they are now, so the four can be judged
    /// side by side. Bypasses the enable and window-in-front checks on purpose: it is a preview.
    /// </summary>
    [RelayCommand]
    private void TestNotification()
    {
        foreach (var level in Enum.GetValues<NotificationLevel>())
        {
            var sample = NotificationCatalog.All.FirstOrDefault(x => x.Level == level);
            ToastService.Shared.Notify(
                Localizer.Get("Notif_Level" + level),
                Localizer.Get("Notif_TestBody"),
                level,
                sample?.Emoji ?? string.Empty,
                _settings.Notifications);
        }
    }

    /// <summary>Puts every colour back to what the app ships with.</summary>
    [RelayCommand]
    private void ResetColors()
    {
        ColorSuccess = NotificationPalette.DefaultSuccess;
        ColorInfo = NotificationPalette.DefaultInfo;
        ColorWarning = NotificationPalette.DefaultWarning;
        ColorError = NotificationPalette.DefaultError;
        ConsoleChatColor = ConsoleColors.DefaultChat;
        ConsolePlayersColor = ConsoleColors.DefaultPlayers;
    }

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// Writes the settings as they stand. Called after every change, including the notification
    /// switches the view binds straight to the model.
    /// </summary>
    public void Persist()
    {
        if (SaveOverride is { } save) { save(_settings); return; }
        _service.Save(_settings);
    }
}
