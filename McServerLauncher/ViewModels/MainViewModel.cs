using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// ViewModel principal: gestiona la lista de servidores, el seleccionado y la persistencia.
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

    private string? _releaseUrl;

    public MainViewModel()
    {
        Load();
        _ = CheckForUpdatesAsync();
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
                UpdateText = $"Hay una actualización disponible: versión {info.Version}.";
                UpdateAvailable = true;
            }
        }
        catch
        {
            // Sin conexión o GitHub no disponible: no pasa nada.
        }
    }

    [RelayCommand]
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
        // La app arranca sin servidores; el usuario crea uno nuevo o añade una carpeta existente.
        foreach (var cfg in _storage.Load())
            Register(cfg);

        SelectedServer = Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(ServerViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>
    /// Devuelve la clave de escritura de Playit; si no está guardada, la pide al usuario y la guarda.
    /// Devuelve null si el usuario cancela.
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

    /// <summary>Crea el túnel de Playit del servidor seleccionado (botón "Crear túnel").</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CreateTunnelForSelected()
    {
        if (SelectedServer is null) return;
        var key = EnsurePlayitApiKey();
        if (key is null) return;
        await SelectedServer.CreateTunnelAsync(key);
    }

    /// <summary>Crea el ViewModel de un servidor, lo añade a la lista y persiste sus cambios.</summary>
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

            // Crear el túnel de Playit (errores visibles en la consola del servidor).
            if (dialog.CreateTunnel)
            {
                var key = EnsurePlayitApiKey();
                if (key is not null)
                    await vm.CreateTunnelAsync(key);
            }

            // Primer arranque para generar mundo y archivos.
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
            Title = "Elige una imagen para el icono del servidor",
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos los archivos (*.*)|*.*"
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
                $"No se pudo crear el icono:\n\n{ex.Message}",
                "Cambiar icono", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
        // Leemos el puerto ANTES de borrar nada (lo necesitamos para localizar el túnel).
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
                    // El usuario no aportó clave; no se borra el túnel.
                }
                else if (!deleted)
                    System.Windows.MessageBox.Show(
                        $"No se encontró ningún túnel de Playit para el puerto {port}. " +
                        "No se borró ningún túnel.",
                        "Eliminar túnel", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"No se pudo eliminar el túnel de Playit:\n\n{ex.Message}",
                    "Eliminar túnel", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
                    $"El servidor se quitó de la lista, pero no se pudieron borrar todos los archivos:\n\n{ex.Message}",
                    "Eliminar archivos",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
    }

    [RelayCommand]
    private void Save() => _storage.Save(Servers.Select(s => s.Config));

    /// <summary>True si algún servidor está encendido (para avisar al cerrar).</summary>
    public bool AnyServerRunning => Servers.Any(s => s.IsRunning);

    /// <summary>Detiene todos los servidores EN PARALELO y guarda al cerrar la app.</summary>
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
