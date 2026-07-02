using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
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

    /// <summary>The main window, used as the owner of modal dialogs.</summary>
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

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

        _ = AskRestartAsync();
    }

    private async Task AskRestartAsync()
    {
        if (await MessageBox.ConfirmAsync(Localizer.Get("RestartNeeded"), Localizer.Get("Language")))
            await RestartAppAsync();
    }

    private async Task RestartAppAsync()
    {
        await ShutdownAllAsync();
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exe))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
            catch { /* if it can't be relaunched, at least exit */ }
        }
        Environment.Exit(0);
    }

    /// <summary>
    /// If the current version differs from the last one seen by the user (i.e. it was just
    /// updated), shows the what's-new window. Saves the seen version so it isn't repeated.
    /// </summary>
    public void ShowWhatsNewIfUpdated(Window owner)
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (current is null) return;
        var version = $"{current.Major}.{current.Minor}.{Math.Max(0, current.Build)}";

        var settings = _settings.Load();
        if (settings.LastVersionSeen == version) return; // already seen in this version

        settings.LastVersionSeen = version;
        _settings.Save(settings);

        try
        {
            _ = new WhatsNewDialog(version).ShowDialog(owner);
        }
        catch { /* if something fails, don't block startup */ }
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
        // On non-Windows (or with no installer asset), open the releases page so the user can
        // download the right package (e.g. the Linux AppImage). The silent .exe installer is Windows-only.
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(_installerUrl))
        {
            OpenRelease();
            return;
        }

        IsUpdating = true;
        UpdateText = Localizer.Get("Update_Downloading");
        try
        {
            // Random per-run folder: fixed names in %TEMP% could be pre-planted/replaced by
            // another local process between download and execution.
            var updateDir = Path.Combine(Path.GetTempPath(), "mcsl-" + Path.GetRandomFileName());
            var dest = Path.Combine(updateDir, "MC-ServerLauncher-Setup.exe");
            await new UpdateService().DownloadInstallerAsync(_installerUrl, dest);

            // Run the installer from a helper that first waits for THIS app to fully exit, then
            // launches it. This avoids the UAC-elevation race where the app closed too soon and the
            // silent install never actually applied.
            var helper = Path.Combine(updateDir, "mcsl-update.cmd");
            await File.WriteAllTextAsync(helper,
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"IMAGENAME eq McServerLauncher.exe\" 2>nul | find /I \"McServerLauncher.exe\" >nul\r\n" +
                "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
                $"\"{dest}\" /SILENT /SUPPRESSMSGBOXES /NORESTART\r\n");

            await ShutdownAllAsync();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{helper}\"",
                UseShellExecute = false,
                CreateNoWindow = true
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
        catch { /* no browser available */ }
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;

    private void Load()
    {
        // The app starts with no servers; the user creates a new one or adds an existing folder.
        // For servers saved before Type/GameVersion existed, detect them from the folder so the
        // mods browser works (older Fabric/Forge servers).
        var detector = new ServerDetectionService();
        var changed = false;
        foreach (var cfg in _storage.Load())
        {
            if (detector.DetectAndFill(cfg)) changed = true;
            Register(cfg);
        }
        if (changed) Save();

        SelectedServer = Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(ServerViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>
    /// Returns the Playit write key; if it's not saved, asks the user for it and saves it.
    /// Returns null if the user cancels.
    /// </summary>
    private async Task<string?> EnsurePlayitApiKeyAsync()
    {
        var settings = _settings.Load();
        if (!string.IsNullOrWhiteSpace(settings.PlayitApiKey))
            return settings.PlayitApiKey;

        var dialog = new PlayitApiKeyDialog();
        if (Owner is null || !await dialog.ShowDialog<bool>(Owner))
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
        var key = await EnsurePlayitApiKeyAsync();
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
    private async Task AddServer()
    {
        if (Owner is null) return;
        var config = new ServerConfig();
        var dialog = new AddEditServerDialog(config);
        if (await dialog.ShowDialog<bool>(Owner))
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

        if (Owner is null) return;
        var dialog = new CreateServerDialog(usedPorts);
        if (await dialog.ShowDialog<bool>(Owner) && dialog.ResultConfig is not null)
        {
            var vm = Register(dialog.ResultConfig);
            SelectedServer = vm;
            Save();

            // Create the Playit tunnel (errors are visible in the server's console).
            if (dialog.CreateTunnel)
            {
                var key = await EnsurePlayitApiKeyAsync();
                if (key is not null)
                    await vm.CreateTunnelAsync(key);
            }

            // First launch to generate the world and files.
            if (dialog.AutoStart)
                vm.StartCommand.Execute(null);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditServer()
    {
        if (SelectedServer is null || Owner is null) return;
        var server = SelectedServer;
        var oldType = server.Config.Type;

        var dialog = new AddEditServerDialog(server.Config);
        if (await dialog.ShowDialog<bool>(Owner))
        {
            server.Name = server.Config.Name;
            Save();

            // If the loader type changed (e.g. a vanilla server was converted to Fabric), rebuild the
            // view model so computed state (IsModded, the Mods tab/browser) refreshes.
            if (server.Config.Type != oldType && !server.IsRunning)
                ReplaceServer(server);
        }
    }

    /// <summary>Replaces a server's view model in place (keeping its position) and reselects it.</summary>
    private void ReplaceServer(ServerViewModel old)
    {
        var index = Servers.IndexOf(old);
        if (index < 0) return;

        _ = old.ShutdownAsync(); // stop its timers (it isn't running)
        var vm = new ServerViewModel(old.Config);
        vm.ConfigChanged += Save;
        Servers[index] = vm;
        SelectedServer = vm;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ChangeIconForSelected()
    {
        if (SelectedServer is null || Owner is null) return;

        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectImage"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Localizer.Get("Title_SelectImage"))
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" }
                }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            new ServerIconService().SetIconFromImage(SelectedServer.Config.FolderPath, path);
            SelectedServer.RefreshFromDisk();
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_IconCreateError"), ex.Message),
                Localizer.Get("Title_ChangeIcon"));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ConfigureServer()
    {
        if (SelectedServer is null || Owner is null) return;
        var dialog = new ServerConfigDialog(SelectedServer.Config);
        if (await dialog.ShowDialog<bool>(Owner))
            SelectedServer.RefreshFromDisk();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveServer()
    {
        if (SelectedServer is null) return;

        var folder = SelectedServer.Config.FolderPath;
        // Read the port BEFORE deleting anything (we need it to locate the tunnel).
        var port = new ServerPropertiesService().GetServerPort(SelectedServer.Config.PropertiesPath);

        if (Owner is null) return;
        var dialog = new DeleteServerDialog(SelectedServer.Name, folder);
        if (!await dialog.ShowDialog<bool>(Owner))
            return;

        await SelectedServer.ShutdownAsync();
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.FirstOrDefault();
        Save();

        if (dialog.DeleteTunnel && port.HasValue)
        {
            var key = await EnsurePlayitApiKeyAsync();
            try
            {
                var deleted = key is not null && await new PlayitApiService().DeleteTunnelForPortAsync(key, port.Value);
                if (key is null)
                {
                    // The user didn't provide a key; the tunnel is not deleted.
                }
                else if (!deleted)
                    await MessageBox.ShowAsync(
                        string.Format(Localizer.Get("Msg_NoTunnelForPort"), port),
                        Localizer.Get("Title_DeleteTunnel"));
            }
            catch (Exception ex)
            {
                await MessageBox.ShowAsync(
                    string.Format(Localizer.Get("Msg_TunnelDeleteError"), ex.Message),
                    Localizer.Get("Title_DeleteTunnel"));
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
                await MessageBox.ShowAsync(
                    string.Format(Localizer.Get("Msg_FilesDeleteError"), ex.Message),
                    Localizer.Get("Title_DeleteFiles"));
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
