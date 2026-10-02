using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.IO;
using Avalonia.Media;
using Avalonia.Threading;
using System.Linq;
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

    /// <summary>Minimizing sends the window to the tray instead of the taskbar (read back on Save).</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>The X button sends the window to the tray instead of quitting (read back on Save).</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Colour for player chat in the console (read back on Save).</summary>
    public string ConsoleChatColor { get; set; } = ConsoleColors.DefaultChat;

    /// <summary>Colour for joins, leaves and deaths in the console (read back on Save).</summary>
    public string ConsolePlayersColor { get; set; } = ConsoleColors.DefaultPlayers;

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
        MinimizeToTray = appSettings?.MinimizeToTray ?? true;
        CloseToTray = appSettings?.CloseToTray ?? false;
        ConsoleChatColor = appSettings?.ConsoleChatColor ?? ConsoleColors.DefaultChat;
        ConsolePlayersColor = appSettings?.ConsolePlayersColor ?? ConsoleColors.DefaultPlayers;
        DataContext = this;
    }

    /// <summary>
    /// Shows one sample toast per level, so the four colours can be judged side by side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four rather than one, because the question the button now answers is not only "do
    /// notifications reach me" but "can I tell these apart" — and a single toast cannot answer the
    /// second. Four is also exactly what fits: the toast stack holds four before it starts pushing
    /// the oldest off.
    /// </para>
    /// <para>
    /// Drawn with <see cref="Notifications"/>, the copy this dialog is editing, not the saved
    /// settings. Previewing the colours the user has just changed is the entire point, and reading
    /// the global ones would show them the colours they are trying to replace.
    /// </para>
    /// <para>
    /// Bypasses the enable/inactive gating on purpose — it is a preview.
    /// </para>
    /// </remarks>
    private void TestNotification_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var level in Enum.GetValues<NotificationLevel>())
        {
            // A kind that is actually shown at this level, so the preview carries the same mark the
            // real notification will. Info has several; the first is as good as any.
            var sample = NotificationCatalog.All.FirstOrDefault(x => x.Level == level);

            ToastService.Shared.Notify(
                Localizer.Get("Notif_Level" + level),
                Localizer.Get("Notif_TestBody"),
                level,
                sample?.Emoji ?? string.Empty,
                Notifications);
        }
    }

    /// <summary>Puts the four colours back to the palette the app ships with.</summary>
    /// <remarks>
    /// Written through the boxes rather than into <see cref="Notifications"/> directly, because the
    /// settings object is a plain serialized model with no change notification: assigning to it
    /// would update what gets saved while the four boxes carried on showing the old values, and the
    /// user would be looking at a dialog that disagreed with itself. The two-way bindings carry the
    /// new text back the moment the boxes change.
    /// </remarks>
    private void ResetColors_Click(object? sender, RoutedEventArgs e)
    {
        ColorSuccessBox.Text = NotificationPalette.DefaultSuccess;
        ColorInfoBox.Text = NotificationPalette.DefaultInfo;
        ColorWarningBox.Text = NotificationPalette.DefaultWarning;
        ColorErrorBox.Text = NotificationPalette.DefaultError;
        ColorChatBox.Text = ConsoleColors.DefaultChat;
        ColorPlayersBox.Text = ConsoleColors.DefaultPlayers;
    }

    /// <summary>
    /// Accepts the dialog, dropping any colour the user left in an unusable state.
    /// </summary>
    /// <remarks>
    /// A half-typed <c>#E0</c> is what a box looks like the moment somebody clicks Save while still
    /// editing, and storing it would mean that level silently falling back to its default on every
    /// notification from then on, with the settings still displaying the broken value. Cleaning it
    /// here means what is saved is always what will actually be drawn.
    /// </remarks>
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var level in Enum.GetValues<NotificationLevel>())
            Notifications.SetColorFor(level,
                NotificationPalette.Sanitize(Notifications.ColorFor(level), level));

        // Same for the console's own two. Sanitize needs a level to fall back to, and these are not
        // levels — so an unusable value goes back to the console default rather than to a level's.
        if (!NotificationPalette.IsValid(ConsoleChatColor)) ConsoleChatColor = ConsoleColors.DefaultChat;
        if (!NotificationPalette.IsValid(ConsolePlayersColor)) ConsolePlayersColor = ConsoleColors.DefaultPlayers;

        Close(true);
    }

    /// <summary>
    /// Puts the app on the desktop. The result is reported next to the button rather than in a
    /// dialog: it is a one-click action and a modal for "done" would be more interruption than
    /// information.
    /// </summary>
    private void CreateShortcut_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = DesktopShortcutService.Create();
            ShortcutStatus.Text = string.Format(Localizer.Get("Shortcut_DoneFmt"), Path.GetFileName(path));
            ShortcutStatus.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
        }
        catch (Exception ex)
        {
            ShortcutStatus.Text = ex.Message;
            ShortcutStatus.Foreground = Avalonia.Media.Brushes.IndianRed;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
