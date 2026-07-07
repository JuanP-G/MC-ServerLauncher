using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// App settings, grouped in one place (language, notifications, and room for more later). Works on
/// a copy of the notification settings so Cancel discards changes; the chosen language and edited
/// notifications are read back by the caller when the dialog returns true.
/// </summary>
public partial class SettingsDialog : Window
{
    public IReadOnlyList<MainViewModel.LanguageOption> Languages { get; }

    /// <summary>The language chosen in the dropdown (read back on Save).</summary>
    public MainViewModel.LanguageOption? SelectedLanguage { get; set; }

    /// <summary>The edited notification settings (a copy; applied by the caller on Save).</summary>
    public NotificationSettings Notifications { get; }

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public SettingsDialog() : this(new List<MainViewModel.LanguageOption>(), null, new NotificationSettings()) { }

    public SettingsDialog(IReadOnlyList<MainViewModel.LanguageOption> languages,
        MainViewModel.LanguageOption? currentLanguage, NotificationSettings notifications)
    {
        InitializeComponent();
        Languages = languages;
        SelectedLanguage = currentLanguage;
        Notifications = notifications.Clone();
        DataContext = this;
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
