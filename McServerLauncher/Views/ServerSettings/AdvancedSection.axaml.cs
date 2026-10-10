using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>
/// Where the server lives, what is run, the JVM's extra arguments, and forgetting its players.
/// </summary>
public partial class AdvancedSection : UserControl, IServerSettingsSection
{
    private readonly ServerSettingsDraft _draft;

    /// <summary>Raised once this server's player history has been forgotten.</summary>
    /// <remarks>
    /// That happens the moment it is confirmed, not on Save — it is not part of what Save writes —
    /// so the Players tab has to be told whether or not the page is then saved.
    /// </remarks>
    public event Action? HistoryCleared;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public AdvancedSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public AdvancedSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        DataContext = draft.Config;
        FolderBox.TextChanged += (_, _) => ShowError(null);
        ShowHistorySize();
    }

    /// <inheritdoc />
    public void Reload() => ShowHistorySize();

    /// <inheritdoc />
    public void ShowError(string? message)
    {
        Error.Text = message;
        Error.IsVisible = message is not null;
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectServerFolder"),
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        var config = _draft.Config;
        config.FolderPath = path;

        // If the name is still the default, suggest the folder's name.
        // ("Nuevo servidor" is the legacy hardcoded default of configs saved by old versions.)
        if (string.IsNullOrWhiteSpace(config.Name)
            || config.Name == Localizer.Get("Name_NewServer")
            || config.Name == "Nuevo servidor")
        {
            config.Name = new DirectoryInfo(path).Name;
        }
    }

    // ---------------------------------------------------------------- player history

    private void ShowHistorySize() =>
        HistorySizeText.Text = string.Format(Localizer.Get("Cfg_HistorySizeFmt"),
            SettingsViewModel.FormatBytes(PlayerHistoryStore.SizeOf(_draft.Live.Id)));

    /// <remarks>
    /// Here rather than in the app's settings because that is where somebody goes looking for it:
    /// the history belongs to a server, and clearing "all of it" from a screen that has no server
    /// on it was a blunt instrument for what people actually wanted, which is to forget one.
    /// </remarks>
    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        ClearHistoryQuestion.Text = string.Format(Localizer.Get("Cfg_HistoryClearConfirmFmt"), _draft.Live.Name);
        ClearHistoryConfirm.IsVisible = true;
        ClearHistoryButton.IsVisible = false;
    }

    private void ClearHistoryCancel_Click(object? sender, RoutedEventArgs e) => CloseConfirm();

    private void ClearHistoryYes_Click(object? sender, RoutedEventArgs e)
    {
        PlayerHistoryStore.For(_draft.Live.Id).ClearAll();
        CloseConfirm();
        ShowHistorySize();
        HistoryCleared?.Invoke();
    }

    private void CloseConfirm()
    {
        ClearHistoryConfirm.IsVisible = false;
        ClearHistoryButton.IsVisible = true;
    }
}
