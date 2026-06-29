using System.IO;
using System.Windows;
using McServerLauncher.Localization;
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

    /// <summary>Configuration of the created server (valid if DialogResult == true).</summary>
    public ServerConfig? ResultConfig { get; private set; }

    /// <summary>Whether to start the server at the end to generate the world.</summary>
    public bool AutoStart { get; private set; }

    /// <summary>Whether to create the Playit tunnel for this server.</summary>
    public bool CreateTunnel { get; private set; }

    /// <summary>Ports already used by other registered servers (to avoid conflicts).</summary>
    private readonly HashSet<int> _usedPorts;

    public CreateServerDialog(IEnumerable<int>? usedPorts = null)
    {
        InitializeComponent();
        _usedPorts = new HashSet<int>(usedPorts ?? Enumerable.Empty<int>());

        ParentFolderBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        // Suggest a free port that doesn't clash with existing servers.
        PortBox.Value = SuggestFreePort();
        if (_usedPorts.Count > 0)
            PortStatus.Text = string.Format(Localizer.Get("Msg_PortsInUseByServers"), string.Join(", ", _usedPorts.OrderBy(p => p)));

        NameBox.TextChanged += (_, _) => UpdateFinalPath();
        ParentFolderBox.TextChanged += (_, _) => UpdateFinalPath();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// First free port from 25565 that is not used by another registered server NOR any other
    /// application on the system.
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
            VersionStatus.Text = string.Format(Localizer.Get("Msg_LatestRelease"), latest);
        }
        catch (Exception ex)
        {
            VersionStatus.Text = string.Format(Localizer.Get("Msg_VersionsLoadError"), ex.Message);
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
        var dialog = new OpenFolderDialog { Title = Localizer.Get("Title_SelectFolderCreate") };
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
            Warn(Localizer.Get("Msg_NameRequired"));
            return;
        }
        if (!Directory.Exists(ParentFolderBox.Text))
        {
            Warn(Localizer.Get("Msg_FolderNotExistCreate"));
            return;
        }
        if (VersionCombo.SelectedItem is not MinecraftVersion version)
        {
            Warn(Localizer.Get("Msg_SelectVersion"));
            return;
        }

        var minGb = (int)(MinRamBox.Value ?? 2);
        var maxGb = (int)(MaxRamBox.Value ?? 4);
        if (maxGb < minGb)
        {
            Warn(Localizer.Get("Msg_RamMaxMin"));
            return;
        }

        var port = (int)(PortBox.Value ?? 25565);
        if (_usedPorts.Contains(port))
        {
            Warn(string.Format(Localizer.Get("Msg_PortAssigned"), port));
            return;
        }
        if (_ports.IsPortInUse(port))
        {
            Warn(string.Format(Localizer.Get("Msg_PortInUseOther"), port));
            return;
        }

        var folder = GetTargetFolder();

        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            var ok = System.Windows.MessageBox.Show(
                string.Format(Localizer.Get("Msg_FolderExists"), folder),
                Localizer.Get("Title_FolderExists"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (ok != System.Windows.MessageBoxResult.Yes)
                return;
        }

        SetBusy(true);
        var progress = new Progress<string>(AppendLog);

        try
        {
            Directory.CreateDirectory(folder);

            AppendLog(string.Format(Localizer.Get("Msg_Resolving"), version.Id));
            var details = await _versions.GetVersionDetailsAsync(version);

            var jarPath = Path.Combine(folder, "server.jar");
            await _versions.DownloadFileAsync(details.ServerUrl, jarPath, progress);

            // Check/install the Java this Minecraft version needs.
            AppendLog(string.Format(Localizer.Get("Msg_CheckingJava"), version.Id, details.JavaMajor));
            var javaPath = "java";
            try
            {
                javaPath = await _java.EnsureJavaAsync(details.JavaMajor, progress);
            }
            catch (Exception jex)
            {
                AppendLog(string.Format(Localizer.Get("Msg_JavaPrepareFail"), details.JavaMajor, jex.Message));
                AppendLog(Localizer.Get("Msg_UseSystemJava"));
            }

            AppendLog(Localizer.Get("Msg_WritingEula"));
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
            // The tunnel creation is done by MainViewModel on the already-added server, so the
            // result/errors appear in the server's console (which doesn't disappear).
            CreateTunnel = ResultConfig.PlayitEnabled && CreateTunnelCheck.IsChecked == true;

            AppendLog(Localizer.Get("Msg_ServerCreated"));
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
            Warn(string.Format(Localizer.Get("Msg_CreateServerError"), ex.Message));
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
        System.Windows.MessageBox.Show(message, Localizer.Get("CreateServer"),
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
}
