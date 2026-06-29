using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Main ViewModel: manages the server list, the selected server and persistence.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ServerStorageService _storage = new();
    private readonly AppSettingsService _settings = new();

    public ObservableCollection<ServerViewModel> Servers { get; } = new();

    [ObservableProperty]
    private ServerViewModel? _selectedServer;

    public bool HasSelection => SelectedServer is not null;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateText = string.Empty;

    [ObservableProperty]
    private bool _isUpdating;

    private string? _releaseUrl;
    private string? _installerUrl;

    public record LanguageOption(string Code, string Name);

    public IReadOnlyList<LanguageOption> Languages { get; } = new List<LanguageOption>
    {
        new("es", "Español"),
        new("en", "English"),
        new("pt", "Português"),
        new("fr", "Français"),
        new("de", "Deutsch"),
    };

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    private bool _languageReady;

    public MainViewModel()
    {
        Load();

        var saved = _settings.Load().Language;
        var code = !string.IsNullOrWhiteSpace(saved) ? saved : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        _languageReady = true;

        _ = CheckForUpdatesAsync();
    }

    partial void OnIsUpdatingChanged(bool value) => UpdateNowCommand.NotifyCanExecuteChanged();

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (!_languageReady || value is null) return;

        var settings = _settings.Load();
        if (settings.Language == value.Code) return;

        settings.Language = value.Code;
        _settings.Save(settings);

        var answer = System.Windows.MessageBox.Show(
            Localizer.Get("RestartNeeded"), Localizer.Get("Language"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer == System.Windows.MessageBoxResult.Yes)
            _ = RestartAppAsync();
    }

    private async Task RestartAppAsync()
    {
        await ShutdownAllAsync();
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exe))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
            catch { /* si no se puede relanzar, al menos cerramos */ }
        }
        Environment.Exit(0);
    }

    /// <summary>
    /// If the current version differs from the last one seen by the user (i.e. it was just
    /// updated), shows the what's-new window. Saves the seen version so it isn't repeated.
    /// </summary>
    public void ShowWhatsNewIfUpdated(System.Windows.Window owner)
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (current is null) return;
        var version = $"{current.Major}.{current.Minor}.{Math.Max(0, current.Build)}";

        var settings = _settings.Load();
        if (settings.LastVersionSeen == version) return; // ya la ha visto en esta versión

        settings.LastVersionSeen = version;
        _settings.Save(settings);

        try
        {
            var dialog = new WhatsNewDialog(version) { Owner = owner };
            dialog.ShowDialog();
        }
        catch { /* si algo falla, no bloquear el arranque */ }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            var info = await new UpdateService().CheckAsync(current);
            if (info is not null)
            {
                _releaseUrl = info.Url;
                _installerUrl = info.InstallerUrl;
                UpdateText = string.Format(Localizer.Get("Msg_UpdateAvailableFmt"), info.Version);
                UpdateAvailable = true;
            }
        }
        catch
        {
            // No connection or GitHub unavailable: it's fine.
        }
    }

    private bool CanUpdateNow => !IsUpdating;

    /// <summary>
    /// Downloads the new version's installer and runs it to update the app without going through
    /// GitHub. If the release has no installer, opens the page as a fallback.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNow()
    {
        // No downloadable installer: open the release page (fallback).
        if (string.IsNullOrEmpty(_installerUrl))
        {
            OpenRelease();
            return;
        }

        IsUpdating = true;
        UpdateText = Localizer.Get("Update_Downloading");
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), "MC-ServerLauncher-Setup.exe");
            await new UpdateService().DownloadInstallerAsync(_installerUrl, dest);

            // Stop servers and launch the installer silently; it relaunches the app when done.
            await ShutdownAllAsync();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dest,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true   // permite la elevación (UAC) del instalador
            });
            Environment.Exit(0);
        }
        catch
        {
            // If the download/install fails, let the user open the page manually.
            IsUpdating = false;
            UpdateText = string.Empty;
            OpenRelease();
        }
    }

    private void OpenRelease()
    {
        if (string.IsNullOrEmpty(_releaseUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _releaseUrl,
                UseShellExecute = true
            });
        }
        catch { /* sin navegador */ }
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;

    private void Load()
    {
        // The app starts with no servers; the user creates a new one or adds an existing folder.
        foreach (var cfg in _storage.Load())
            Register(cfg);

        SelectedServer = Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(ServerViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>
    /// Returns the Playit write key; if it's not saved, asks the user for it and saves it.
    /// Returns null if the user cancels.
    /// </summary>
    private string? EnsurePlayitApiKey()
    {
        var settings = _settings.Load();
        if (!string.IsNullOrWhiteSpace(settings.PlayitApiKey))
            return settings.PlayitApiKey;

        var dialog = new PlayitApiKeyDialog { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
            return null;

        settings.PlayitApiKey = dialog.ApiKey;
        _settings.Save(settings);
        return dialog.ApiKey;
    }

    /// <summary>Creates the Playit tunnel for the selected server (the "Create tunnel" button).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CreateTunnelForSelected()
    {
        if (SelectedServer is null) return;
        var key = EnsurePlayitApiKey();
        if (key is null) return;
        await SelectedServer.CreateTunnelAsync(key);
    }

    /// <summary>Creates a server's ViewModel, adds it to the list and persists its changes.</summary>
    private ServerViewModel Register(ServerConfig config)
    {
        var vm = new ServerViewModel(config);
        vm.ConfigChanged += Save;
        Servers.Add(vm);
        return vm;
    }

    [RelayCommand]
    private void AddServer()
    {
        var config = new ServerConfig();
        var dialog = new AddEditServerDialog(config) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            SelectedServer = Register(config);
            Save();
        }
    }

    [RelayCommand]
    private async Task CreateServer()
    {
        var propertiesService = new ServerPropertiesService();
        var usedPorts = Servers
            .Select(s => propertiesService.GetServerPort(s.Config.PropertiesPath))
            .Where(p => p.HasValue)
            .Select(p => p!.Value);

        var dialog = new CreateServerDialog(usedPorts) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() == true && dialog.ResultConfig is not null)
        {
            var vm = Register(dialog.ResultConfig);
            SelectedServer = vm;
            Save();

            // Create the Playit tunnel (errors are visible in the server's console).
            if (dialog.CreateTunnel)
            {
                var key = EnsurePlayitApiKey();
                if (key is not null)
                    await vm.CreateTunnelAsync(key);
            }

            // First launch to generate the world and files.
            if (dialog.AutoStart)
                vm.StartCommand.Execute(null);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditServer()
    {
        if (SelectedServer is null) return;
        var dialog = new AddEditServerDialog(SelectedServer.Config) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            SelectedServer.Name = SelectedServer.Config.Name;
            Save();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ChangeIconForSelected()
    {
        if (SelectedServer is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.Get("Title_SelectImage"),
            Filter = Localizer.Get("Filter_Images")
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            new ServerIconService().SetIconFromImage(SelectedServer.Config.FolderPath, dialog.FileName);
            SelectedServer.RefreshFromDisk();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                string.Format(Localizer.Get("Msg_IconCreateError"), ex.Message),
                Localizer.Get("Title_ChangeIcon"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ConfigureServer()
    {
        if (SelectedServer is null) return;
        var dialog = new ServerConfigDialog(SelectedServer.Config)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
            SelectedServer.RefreshFromDisk();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveServer()
    {
        if (SelectedServer is null) return;

        var folder = SelectedServer.Config.FolderPath;
        // Read the port BEFORE deleting anything (we need it to locate the tunnel).
        var port = new ServerPropertiesService().GetServerPort(SelectedServer.Config.PropertiesPath);

        var dialog = new DeleteServerDialog(SelectedServer.Name, folder)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        await SelectedServer.ShutdownAsync();
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.FirstOrDefault();
        Save();

        if (dialog.DeleteTunnel && port.HasValue)
        {
            var key = EnsurePlayitApiKey();
            try
            {
                var deleted = key is not null && await new PlayitApiService().DeleteTunnelForPortAsync(key, port.Value);
                if (key is null)
                {
                    // The user didn't provide a key; the tunnel is not deleted.
                }
                else if (!deleted)
                    System.Windows.MessageBox.Show(
                        string.Format(Localizer.Get("Msg_NoTunnelForPort"), port),
                        Localizer.Get("Title_DeleteTunnel"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(Localizer.Get("Msg_TunnelDeleteError"), ex.Message),
                    Localizer.Get("Title_DeleteTunnel"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        if (dialog.DeleteFiles && Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(Localizer.Get("Msg_FilesDeleteError"), ex.Message),
                    Localizer.Get("Title_DeleteFiles"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
    }

    [RelayCommand]
    private void Save() => _storage.Save(Servers.Select(s => s.Config));

    /// <summary>True if any server is running (to warn on close).</summary>
    public bool AnyServerRunning => Servers.Any(s => s.IsRunning);

    /// <summary>Stops all servers IN PARALLEL and saves when the app closes.</summary>
    public async Task ShutdownAllAsync()
    {
        await Task.WhenAll(Servers.Select(s => s.ShutdownAsync()));
        Save();
    }

    partial void OnSelectedServerChanged(ServerViewModel? oldValue, ServerViewModel? newValue)
    {
        EditServerCommand.NotifyCanExecuteChanged();
        RemoveServerCommand.NotifyCanExecuteChanged();
        CreateTunnelForSelectedCommand.NotifyCanExecuteChanged();
        ConfigureServerCommand.NotifyCanExecuteChanged();
        ChangeIconForSelectedCommand.NotifyCanExecuteChanged();
    }
}
