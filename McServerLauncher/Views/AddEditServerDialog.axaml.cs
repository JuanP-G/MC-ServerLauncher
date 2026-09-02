using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

public partial class AddEditServerDialog : Window
{
    private readonly ServerConfig _config;
    private string _snapshot;

    /// <summary>
    /// True if a mod loader was installed during this dialog session. The install mutates the
    /// config AND the disk immediately (files are already downloaded), so the caller must persist
    /// the config even if the user then closes this dialog with Cancel — Cancel only reverts the
    /// editable fields, it cannot undo the loader change.
    /// </summary>
    public bool LoaderInstalled { get; private set; }

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public AddEditServerDialog() : this(new ServerConfig()) { }

    public AddEditServerDialog(ServerConfig config)
    {
        InitializeComponent();
        _config = config;
        // The per-server notification override must be non-null for the checkboxes to bind; seed it
        // from the current global defaults so a fresh custom config starts sensibly.
        _config.Notifications ??= Services.NotificationPreferences.Global.Clone();
        // Keep a copy to restore if the user cancels.
        _snapshot = JsonSerializer.Serialize(config);
        DataContext = _config;

        UpdateTypeDependentOptions();
    }

    private void RefreshDataContext()
    {
        DataContext = null;
        DataContext = _config;
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectServerFolder"),
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        _config.FolderPath = path;
        RefreshDataContext();

        // If the name is still the default, suggest the folder's name.
        // ("Nuevo servidor" is the legacy hardcoded default of configs saved by old versions.)
        if (string.IsNullOrWhiteSpace(_config.Name)
            || _config.Name == Localizer.Get("Name_NewServer")
            || _config.Name == "Nuevo servidor")
        {
            _config.Name = new DirectoryInfo(path).Name;
            RefreshDataContext();
        }
    }

    private async void BrowseJava_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectJava"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java")
                {
                    Patterns = OperatingSystem.IsWindows() ? new[] { "java.exe" } : new[] { "java" }
                }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        _config.JavaPath = path;
        RefreshDataContext();
    }

    private async void InstallLoader_Click(object? sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_config.FolderPath))
        {
            await MessageBox.ShowAsync(Localizer.Get("Msg_FolderNotExist"), Localizer.Get("Title_Validation"), this);
            return;
        }

        var dialog = new InstallLoaderDialog(_config);
        if (await dialog.ShowDialog<bool>(this))
        {
            // The loader files are already on disk and _config was updated; refresh the snapshot so
            // a later Cancel doesn't revert the new loader/jar/java fields, and re-bind to show them.
            LoaderInstalled = true;
            _snapshot = JsonSerializer.Serialize(_config);
            RefreshDataContext();
            // The type just changed, and everything below depends on it. Without this the crossplay
            // and version-bridging checkboxes keep the previous type's answer — convert a Vanilla
            // server to Paper and they stay greyed out saying Vanilla takes no plugins, which reads
            // as the conversion not having happened at all.
            UpdateTypeDependentOptions();
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.Name))
        {
            await MessageBox.ShowAsync(Localizer.Get("Msg_NameEmpty"), Localizer.Get("Title_Validation"), this);
            return;
        }
        if (!Directory.Exists(_config.FolderPath))
        {
            await MessageBox.ShowAsync(Localizer.Get("Msg_FolderNotExist"), Localizer.Get("Title_Validation"), this);
            return;
        }
        if (_config.MaxRamGb < _config.MinRamGb)
        {
            await MessageBox.ShowAsync(Localizer.Get("Msg_RamMaxMin"), Localizer.Get("Title_Validation"), this);
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        RestoreSnapshot();
        Close(false);
    }

    /// <summary>
    /// Greys out the options the current type cannot do, and says why for each.
    /// </summary>
    /// <remarks>
    /// Runs again after a type conversion, not only once at open: the Install-loader button changes
    /// the type from inside this dialog, and the comment here used to claim it could not.
    /// </remarks>
    private void UpdateTypeDependentOptions()
    {
        var supported = Services.CrossplayService.CanEnable(_config.Type);

        CrossplayCheck.IsEnabled = supported;
        CrossplayHint.IsVisible = supported;
        CrossplayWhyNot.IsVisible = !supported;
        var caveat = Services.CrossplayService.CaveatKey(_config.Type);
        CrossplayModdedNote.IsVisible = supported && caveat is not null;
        if (caveat is not null) CrossplayModdedNote.Text = Localizer.Get(caveat);

        var multiVersion = Services.MultiVersionService.CanEnable(_config.Type);
        MultiVersionCheck.IsEnabled = multiVersion;
        MultiVersionHint.IsVisible = multiVersion;
        MultiVersionWhyNot.IsVisible = !multiVersion;
        if (!multiVersion) _config.MultiVersionEnabled = false;

        var modContent = Services.HydraulicService.CanEnable(_config.Type);
        HydraulicCheck.IsEnabled = modContent;
        HydraulicHint.IsVisible = modContent;
        HydraulicWhyNot.IsVisible = !modContent;
        if (!modContent) _config.BedrockModContentEnabled = false;

        if (!supported)
        {
            _config.CrossplayEnabled = false;
            CrossplayWhyNot.Text = Localizer.Get(_config.Type == Models.ServerType.Vanilla
                ? "Crossplay_UnsupportedVanilla"
                : "Crossplay_Unsupported");
        }
    }

    private void RestoreSnapshot()
    {
        var original = JsonSerializer.Deserialize<ServerConfig>(_snapshot);
        if (original is null) return;
        _config.Name = original.Name;
        _config.FolderPath = original.FolderPath;
        _config.JarFile = original.JarFile;
        _config.JavaPath = original.JavaPath;
        _config.MinRamGb = original.MinRamGb;
        _config.MaxRamGb = original.MaxRamGb;
        _config.ExtraJvmArgs = original.ExtraJvmArgs;
        _config.PlayitEnabled = original.PlayitEnabled;
        _config.IdleShutdownMinutes = original.IdleShutdownMinutes;
        _config.WakeOnDemand = original.WakeOnDemand;
        _config.CrossplayEnabled = original.CrossplayEnabled;
        _config.MultiVersionEnabled = original.MultiVersionEnabled;
        _config.BedrockModContentEnabled = original.BedrockModContentEnabled;
        _config.BedrockPort = original.BedrockPort;
        _config.BackupsEnabled = original.BackupsEnabled;
        _config.BackupRetention = original.BackupRetention;
        _config.UseCustomNotifications = original.UseCustomNotifications;
        _config.Notifications = original.Notifications;
    }

}
