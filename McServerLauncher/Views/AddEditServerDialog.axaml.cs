using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Views;

public partial class AddEditServerDialog : Window
{
    private readonly ServerConfig _config;
    private string _snapshot;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public AddEditServerDialog() : this(new ServerConfig()) { }

    public AddEditServerDialog(ServerConfig config)
    {
        InitializeComponent();
        _config = config;
        // Keep a copy to restore if the user cancels.
        _snapshot = JsonSerializer.Serialize(config);
        DataContext = _config;
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
            _snapshot = JsonSerializer.Serialize(_config);
            RefreshDataContext();
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
        _config.BackupsEnabled = original.BackupsEnabled;
        _config.BackupRetention = original.BackupRetention;
    }
}
