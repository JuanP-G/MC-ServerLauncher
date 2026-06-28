using System.IO;
using System.Windows;
using McServerLauncher.Models;
using McServerLauncher.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace McServerLauncher.Views;

public partial class CreateServerDialog : FluentWindow
{
    private readonly MinecraftVersionService _versions = new();
    private readonly ServerCreationService _creation = new();
    private readonly PortService _ports = new();
    private readonly JavaService _java = new();
    private List<MinecraftVersion> _allVersions = new();
    private string _latestRelease = string.Empty;

    /// <summary>Configuración del servidor creado (válida si DialogResult == true).</summary>
    public ServerConfig? ResultConfig { get; private set; }

    /// <summary>Si hay que iniciar el servidor al terminar para generar el mundo.</summary>
    public bool AutoStart { get; private set; }

    /// <summary>Si hay que crear el túnel de Playit para este servidor.</summary>
    public bool CreateTunnel { get; private set; }

    /// <summary>Puertos ya ocupados por otros servidores registrados (para evitar conflictos).</summary>
    private readonly HashSet<int> _usedPorts;

    public CreateServerDialog(IEnumerable<int>? usedPorts = null)
    {
        InitializeComponent();
        _usedPorts = new HashSet<int>(usedPorts ?? Enumerable.Empty<int>());

        ParentFolderBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        // Sugerir un puerto libre que no choque con los servidores ya existentes.
        PortBox.Value = SuggestFreePort();
        if (_usedPorts.Count > 0)
            PortStatus.Text = $"Puertos ya en uso por otros servidores: {string.Join(", ", _usedPorts.OrderBy(p => p))}";

        NameBox.TextChanged += (_, _) => UpdateFinalPath();
        ParentFolderBox.TextChanged += (_, _) => UpdateFinalPath();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Primer puerto libre desde 25565 que no use otro servidor registrado NI ninguna otra
    /// aplicación del sistema.
    /// </summary>
    private int SuggestFreePort() => _ports.FindFreePort(25565, _usedPorts);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateFinalPath();
        try
        {
            var (latest, list) = await _versions.GetVersionsAsync();
            _latestRelease = latest;
            _allVersions = list;
            PopulateVersions();
            VersionStatus.Text = $"Última versión release: {latest}";
        }
        catch (Exception ex)
        {
            VersionStatus.Text = $"No se pudieron cargar las versiones: {ex.Message}";
        }
    }

    private void Snapshots_Changed(object sender, RoutedEventArgs e) => PopulateVersions();

    private void PopulateVersions()
    {
        if (_allVersions.Count == 0)
            return;

        var includeSnapshots = SnapshotsCheck.IsChecked == true;
        var filtered = includeSnapshots
            ? _allVersions
            : _allVersions.Where(v => v.IsRelease).ToList();

        VersionCombo.ItemsSource = filtered;
        var preferred = filtered.FirstOrDefault(v => v.Id == _latestRelease) ?? filtered.FirstOrDefault();
        VersionCombo.SelectedItem = preferred;
    }

    private void UpdateFinalPath()
    {
        var folder = GetTargetFolder();
        FinalPathText.Text = string.IsNullOrWhiteSpace(folder)
            ? string.Empty
            : $"Se creará en: {folder}";
    }

    private string GetTargetFolder()
    {
        var name = SanitizeFolderName(NameBox.Text);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ParentFolderBox.Text))
            return string.Empty;
        return Path.Combine(ParentFolderBox.Text.Trim(), name);
    }

    private static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Trim().Where(c => !invalid.Contains(c)).ToArray());
    }

    private void BrowseParent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Carpeta donde crear el servidor" };
        if (Directory.Exists(ParentFolderBox.Text))
            dialog.InitialDirectory = ParentFolderBox.Text;
        if (dialog.ShowDialog() == true)
            ParentFolderBox.Text = dialog.FolderName;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            Warn("Indica un nombre para el servidor.");
            return;
        }
        if (!Directory.Exists(ParentFolderBox.Text))
        {
            Warn("La carpeta donde crear el servidor no existe.");
            return;
        }
        if (VersionCombo.SelectedItem is not MinecraftVersion version)
        {
            Warn("Selecciona una versión de Minecraft.");
            return;
        }

        var minGb = (int)(MinRamBox.Value ?? 2);
        var maxGb = (int)(MaxRamBox.Value ?? 4);
        if (maxGb < minGb)
        {
            Warn("La RAM máxima debe ser mayor o igual que la mínima.");
            return;
        }

        var port = (int)(PortBox.Value ?? 25565);
        if (_usedPorts.Contains(port))
        {
            Warn($"El puerto {port} ya está asignado a otro servidor registrado.\n\n" +
                 "Elige un puerto distinto (el botón te sugiere uno libre).");
            return;
        }
        if (_ports.IsPortInUse(port))
        {
            Warn($"El puerto {port} ya está en uso por otra aplicación del sistema.\n\n" +
                 "Elige un puerto distinto.");
            return;
        }

        var folder = GetTargetFolder();

        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            var ok = System.Windows.MessageBox.Show(
                $"La carpeta ya existe y no está vacía:\n{folder}\n\n¿Usarla de todas formas?",
                "Carpeta existente", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (ok != System.Windows.MessageBoxResult.Yes)
                return;
        }

        SetBusy(true);
        var progress = new Progress<string>(AppendLog);

        try
        {
            Directory.CreateDirectory(folder);

            AppendLog($"Resolviendo la descarga de Minecraft {version.Id}...");
            var details = await _versions.GetVersionDetailsAsync(version);

            var jarPath = Path.Combine(folder, "server.jar");
            await _versions.DownloadFileAsync(details.ServerUrl, jarPath, progress);

            // Comprobar/instalar el Java que necesita esta versión de Minecraft.
            AppendLog($"Comprobando Java (Minecraft {version.Id} necesita Java {details.JavaMajor})...");
            var javaPath = "java";
            try
            {
                javaPath = await _java.EnsureJavaAsync(details.JavaMajor, progress);
            }
            catch (Exception jex)
            {
                AppendLog($"[Aviso] No se pudo preparar Java {details.JavaMajor}: {jex.Message}");
                AppendLog("Se usará el 'java' del sistema; si no es compatible, instálalo a mano.");
            }

            AppendLog("Aceptando el EULA y generando run.bat...");
            _creation.WriteEula(folder);
            _creation.WriteRunBat(folder, minGb, maxGb, "server.jar", javaPath);
            _creation.WriteInitialProperties(folder, port, $"{name} - creado con MC Server Launcher");

            ResultConfig = new ServerConfig
            {
                Name = name,
                FolderPath = folder,
                JarFile = "server.jar",
                JavaPath = javaPath,
                MinRamGb = minGb,
                MaxRamGb = maxGb,
                PlayitEnabled = PlayitCheck.IsChecked == true
            };
            AutoStart = AutoStartCheck.IsChecked == true;
            // La creación del túnel la hace MainViewModel sobre el servidor ya añadido, para que
            // el resultado/errores salgan en la consola del servidor (que no desaparece).
            CreateTunnel = ResultConfig.PlayitEnabled && CreateTunnelCheck.IsChecked == true;

            AppendLog("¡Servidor creado correctamente!");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] {ex.Message}");
            Warn($"No se pudo crear el servidor:\n\n{ex.Message}");
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SetBusy(bool busy)
    {
        FormPanel.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy;
        ProgressBox.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Spinner.IsIndeterminate = busy;
    }

    private void AppendLog(string line)
    {
        ProgressLog.AppendText(line + Environment.NewLine);
        ProgressLog.ScrollToEnd();
    }

    private static void Warn(string message) =>
        System.Windows.MessageBox.Show(message, "Crear servidor",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
}
