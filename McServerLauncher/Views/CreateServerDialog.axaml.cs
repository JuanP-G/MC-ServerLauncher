using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

public partial class CreateServerDialog : Window
{
    private readonly MinecraftVersionService _versions = new();
    private readonly ModLoaderService _mods = new();
    private readonly PaperService _paper = new();
    private readonly ServerCreationService _creation = new();
    private readonly PortService _ports = new();
    private readonly JavaService _java = new();
    private List<MinecraftVersion> _allVersions = new();
    private string _latestRelease = string.Empty;

    /// <summary>Configuration of the created server (valid if the dialog returned true).</summary>
    public ServerConfig? ResultConfig { get; private set; }

    /// <summary>Whether to start the server at the end to generate the world.</summary>
    public bool AutoStart { get; private set; }

    /// <summary>Whether to create the Playit tunnel for this server.</summary>
    public bool CreateTunnel { get; private set; }

    /// <summary>Ports already used by other registered servers (to avoid conflicts).</summary>
    private readonly HashSet<int> _usedPorts;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public CreateServerDialog() : this(null) { }

    // Buffered progress log (see LogBatcher: the Forge installer prints thousands of lines).
    private readonly LogBatcher _log;

    public CreateServerDialog(IEnumerable<int>? usedPorts = null)
    {
        InitializeComponent();
        _usedPorts = new HashSet<int>(usedPorts ?? Enumerable.Empty<int>());

        _log = new LogBatcher(ProgressLog);

        ParentFolderBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        // Suggest a free port that doesn't clash with existing servers.
        PortBox.Value = SuggestFreePort();
        if (_usedPorts.Count > 0)
            PortStatus.Text = string.Format(Localizer.Get("Msg_PortsInUseByServers"), string.Join(", ", _usedPorts.OrderBy(p => p)));

        NameBox.TextChanged += (_, _) => UpdateFinalPath();
        ParentFolderBox.TextChanged += (_, _) => UpdateFinalPath();
        TypeCombo.SelectionChanged += (_, _) => UpdateCrossplayAvailability();
        UpdateCrossplayAvailability();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// First free port from 25565 that is not used by another registered server NOR any other
    /// application on the system.
    /// </summary>
    private int SuggestFreePort() =>
        // null = every port is taken (absurd in practice): suggest the default anyway; the
        // Create button's own validation will refuse a busy port before anything is written.
        _ports.FindFreePort(25565, _usedPorts) ?? 25565;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
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

    private void Snapshots_Changed(object? sender, RoutedEventArgs e) => PopulateVersions();

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
        FinalPathText.Text = string.IsNullOrWhiteSpace(folder) ? string.Empty : "→ " + folder;
    }

    private string GetTargetFolder()
    {
        var name = SanitizeFolderName(NameBox.Text);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ParentFolderBox.Text))
            return string.Empty;
        return Path.Combine(ParentFolderBox.Text.Trim(), name);
    }

    private static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Trim().Where(c => !invalid.Contains(c)).ToArray());
    }

    private async void BrowseParent_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectFolderCreate"),
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
            ParentFolderBox.Text = path;
    }

    private async void Create_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) { await Warn(Localizer.Get("Msg_NameRequired")); return; }
        if (!Directory.Exists(ParentFolderBox.Text)) { await Warn(Localizer.Get("Msg_FolderNotExistCreate")); return; }
        if (VersionCombo.SelectedItem is not MinecraftVersion version) { await Warn(Localizer.Get("Msg_SelectVersion")); return; }

        var minGb = (int)(MinRamBox.Value ?? 2m);
        var maxGb = (int)(MaxRamBox.Value ?? 4m);
        if (maxGb < minGb) { await Warn(Localizer.Get("Msg_RamMaxMin")); return; }

        var port = (int)(PortBox.Value ?? 25565m);
        if (_usedPorts.Contains(port)) { await Warn(string.Format(Localizer.Get("Msg_PortAssigned"), port)); return; }
        if (_ports.IsPortInUse(port)) { await Warn(string.Format(Localizer.Get("Msg_PortInUseOther"), port)); return; }

        var folder = GetTargetFolder();

        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            var ok = await MessageBox.ConfirmAsync(
                string.Format(Localizer.Get("Msg_FolderExists"), folder),
                Localizer.Get("Title_FolderExists"), this);
            if (!ok) return;
        }

        SetBusy(true);
        var progress = new Progress<string>(AppendLog);

        try
        {
            Directory.CreateDirectory(folder);

            AppendLog(string.Format(Localizer.Get("Msg_Resolving"), version.Id));
            var details = await _versions.GetVersionDetailsAsync(version);

            var serverType = SelectedServerType();
            var loaderVersion = string.Empty;
            var forgeArgs = string.Empty;
            var jarName = serverType == ServerType.Fabric ? "fabric-server.jar" : "server.jar";

            // Install/locate the Java this Minecraft version needs first: the Forge installer also
            // requires a compatible Java to run.
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

            if (serverType == ServerType.Fabric)
            {
                AppendLog(Localizer.Get("Msg_FabricResolving"));
                loaderVersion = await _mods.GetLatestFabricLoaderVersionAsync();
                await _mods.DownloadFabricServerAsync(version.Id, loaderVersion, Path.Combine(folder, jarName), progress);
            }
            else if (serverType == ServerType.Forge)
            {
                AppendLog(Localizer.Get("Msg_ForgeResolving"));
                var forgeVersion = await _mods.GetRecommendedForgeVersionAsync(version.Id);
                if (string.IsNullOrEmpty(forgeVersion))
                    throw new InvalidOperationException(string.Format(Localizer.Get("Msg_ForgeNoVersion"), version.Id));

                loaderVersion = forgeVersion;
                var forge = await _mods.InstallForgeServerAsync(folder, version.Id, forgeVersion, javaPath, progress);
                if (forge.ArgsId is not null)
                {
                    forgeArgs = forge.ArgsId;     // modern Forge: launched via args file, no runnable jar
                    jarName = string.Empty;
                    // Forge ships its own run.bat that reads user_jvm_args.txt; give it our RAM settings.
                    _creation.WriteForgeUserJvmArgs(folder, minGb, maxGb);
                }
                else if (!string.IsNullOrEmpty(forge.JarFile))
                {
                    jarName = forge.JarFile;      // old Forge: a runnable forge-*.jar
                }
                else
                {
                    throw new InvalidOperationException(Localizer.Get("Msg_ForgeInstallNoOutput"));
                }
            }
            else if (serverType == ServerType.NeoForge)
            {
                AppendLog(Localizer.Get("Msg_NeoForgeResolving"));
                var choice = await _mods.GetNeoForgeVersionAsync(version.Id);
                if (choice is null)
                    throw new InvalidOperationException(
                        string.Format(Localizer.Get("Msg_NeoForgeNoVersion"), version.Id));

                // Said before installing, not after: for six Minecraft versions a pre-release is
                // the only NeoForge there has ever been, and that is worth knowing up front.
                if (choice.IsBeta)
                    AppendLog(string.Format(Localizer.Get("Msg_NeoForgeBetaWarning"), choice.Version));

                loaderVersion = choice.Version;
                var neo = await _mods.InstallNeoForgeServerAsync(folder, choice.Version, javaPath, progress);
                if (neo.ArgsId is null)
                    throw new InvalidOperationException(Localizer.Get("Msg_NeoForgeInstallNoOutput"));

                forgeArgs = neo.ArgsId;           // NeoForge always launches via the args file
                jarName = string.Empty;
                // Same as Forge: its run script reads user_jvm_args.txt, so give it our RAM settings.
                _creation.WriteForgeUserJvmArgs(folder, minGb, maxGb);
            }
            else if (serverType == ServerType.Paper)
            {
                AppendLog(Localizer.Get("Msg_PaperResolving"));
                var build = await _paper.GetLatestBuildAsync(version.Id);
                if (build is null)
                    throw new InvalidOperationException(string.Format(Localizer.Get("Msg_PaperNoBuild"), version.Id));

                loaderVersion = build.Build.ToString();
                jarName = "paper-server.jar";
                await _paper.DownloadPaperServerAsync(build, Path.Combine(folder, jarName), progress);
            }
            else
            {
                await _versions.DownloadFileAsync(details.ServerUrl, Path.Combine(folder, jarName), progress, details.Sha1);
            }

            AppendLog(Localizer.Get("Msg_WritingEula"));
            _creation.WriteEula(folder);
            // Modern Forge ships its own run.bat (no single jar); only write ours when there is a jar.
            if (!string.IsNullOrEmpty(jarName))
                _creation.WriteRunBat(folder, minGb, maxGb, jarName, javaPath);
            _creation.WriteInitialProperties(folder, port, $"{name} - MC Server Launcher");

            ResultConfig = new ServerConfig
            {
                Name = name,
                FolderPath = folder,
                JarFile = string.IsNullOrEmpty(jarName) ? "server.jar" : jarName,
                Type = serverType,
                GameVersion = version.Id,
                ModLoaderVersion = loaderVersion,
                ForgeArgs = forgeArgs,
                JavaPath = javaPath,
                MinRamGb = minGb,
                MaxRamGb = maxGb,
                PlayitEnabled = PlayitCheck.IsChecked == true,
                CrossplayEnabled = CrossplayCheck.IsChecked == true && CrossplayService.CanEnable(serverType)
            };
            AutoStart = AutoStartCheck.IsChecked == true;
            // The tunnel creation is done by MainViewModel on the already-added server, so the
            // result/errors appear in the server's console (which doesn't disappear).
            CreateTunnel = ResultConfig.PlayitEnabled && CreateTunnelCheck.IsChecked == true;

            AppendLog(Localizer.Get("Msg_ServerCreated"));
            Close(true);
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
            await Warn(string.Format(Localizer.Get("Msg_CreateServerError"), ex.Message));
            SetBusy(false);
        }
    }

    /// <summary>
    /// Greys out crossplay for the server types Geyser has no build for, and says which.
    /// </summary>
    /// <remarks>
    /// Explained rather than merely disabled: a checkbox that is simply grey invites the reader to
    /// assume it is broken. Vanilla gets its own wording because it has a way out — changing the
    /// type to Paper keeps the world — while Forge simply has no Geyser at all.
    /// </remarks>
    private void UpdateCrossplayAvailability()
    {
        var type = SelectedServerType();
        var supported = CrossplayService.CanEnable(type);

        CrossplayCheck.IsEnabled = supported;
        CrossplayHint.IsVisible = supported;
        CrossplayWhyNot.IsVisible = !supported;
        CrossplayModdedNote.IsVisible = supported && CrossplayService.ModsCanLockOutBedrock(type);

        if (!supported)
        {
            CrossplayCheck.IsChecked = false;
            CrossplayWhyNot.Text = Localizer.Get(type == ServerType.Vanilla
                ? "Crossplay_UnsupportedVanilla"
                : "Crossplay_Unsupported");
        }
    }

    /// <summary>Reads the picked server type from the combo's Tag.</summary>
    /// <remarks>
    /// By Tag rather than by SelectedIndex, which is what this used to do. An index switch means
    /// reordering the list, or inserting an entry anywhere but the end, silently changes what every
    /// option below it creates — and the two dialogs that offer this list did not even use the same
    /// order. Falls back to Vanilla, the one type that needs no loader and cannot be wrong.
    /// </remarks>
    private ServerType SelectedServerType() =>
        (TypeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag &&
        Enum.TryParse<ServerType>(tag, out var type)
            ? type
            : ServerType.Vanilla;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void SetBusy(bool busy)
    {
        FormPanel.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy;
        ProgressBox.IsVisible = busy;
        Spinner.IsIndeterminate = busy;
    }

    private void AppendLog(string line) => _log.Append(line);

    protected override void OnClosed(EventArgs e)
    {
        _log.Stop();
        base.OnClosed(e);
    }

    private Task Warn(string message) =>
        MessageBox.ShowAsync(message, Localizer.Get("CreateServer"), this);
}
