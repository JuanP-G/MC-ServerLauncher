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
    private readonly ServerJarInstaller _installer = new();
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
        TypePicker.SelectionChanged += (_, _) =>
        {
            UpdateTypeDependentOptions();
            UpdatePathWarning();     // the rule only applies to some types, so it moves with the pick
        };
        UpdateTypeDependentOptions();
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
        UpdatePathWarning(folder);
    }

    /// <summary>
    /// Warns while the name is still being typed, for anything that would stop this server working.
    /// </summary>
    /// <remarks>
    /// Here rather than only at the Create button because the folder name comes from the server
    /// name: someone calling a server "Java+Bedrock" should find out now, not after the download and
    /// three restart attempts. It follows the picked type too, since only some server software
    /// refuses characters of its own.
    /// </remarks>
    private void UpdatePathWarning(string? folder = null)
    {
        folder ??= GetTargetFolder();
        var issue = ServerNameRule.Check(folder, SelectedServerType());

        PathWarning.IsVisible = issue is not null;
        if (issue is not null) PathWarning.Text = Describe(issue, folder);
    }

    /// <summary>Turns a rule violation into something worth reading, with the fix in it.</summary>
    private static string Describe(NameIssue issue, string folder) => issue.Kind switch
    {
        NameIssueKind.InvalidCharacter => string.Format(
            Localizer.Get("Msg_NameInvalidCharFmt"), issue.Detail,
            ServerNameRule.Clean(Path.GetFileName(folder))),

        NameIssueKind.ReservedName => string.Format(
            Localizer.Get("Msg_NameReservedFmt"), issue.Detail),

        NameIssueKind.TrailingDotOrSpace => Localizer.Get("Msg_NameTrailingDot"),

        NameIssueKind.ServerRejectsParentCharacter => string.Format(
            Localizer.Get("Msg_BukkitPathParentFmt"), issue.Detail),

        _ => string.Format(Localizer.Get("Msg_BukkitPathFmt"), issue.Detail)
    };

    /// <summary>
    /// The folder the server would get, using the name exactly as typed.
    /// </summary>
    /// <remarks>
    /// No longer stripped behind the user's back. Typing "Mi:Server" used to produce a folder called
    /// "MiServer" with nothing said about it; now the name is shown as it is and refused with a
    /// reason if it cannot be used.
    /// </remarks>
    private string GetTargetFolder()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ParentFolderBox.Text))
            return string.Empty;
        return Path.Combine(ParentFolderBox.Text.Trim(), name);
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

        // Refused rather than warned about: depending on which rule it breaks, the folder either
        // cannot be created at all or produces a server that installs fine and then exits on every
        // start without ever reaching the world.
        if (ServerNameRule.Check(folder, SelectedServerType()) is { } issue)
        {
            await Warn(Describe(issue, folder));
            return;
        }

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

            var installed = await _installer.InstallAsync(
                serverType, folder, version.Id, details, javaPath, minGb, maxGb, progress);

            var jarName = installed.JarFile;
            var loaderVersion = installed.LoaderVersion;
            var forgeArgs = installed.ForgeArgs;

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
                MultiVersionEnabled = MultiVersionCheck.IsChecked == true,
                BedrockModContentEnabled = HydraulicCheck.IsChecked == true,
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
    /// Greys out the options the picked type cannot do, and says why for each.
    /// </summary>
    /// <remarks>
    /// Explained rather than merely disabled: a checkbox that is simply grey invites the reader to
    /// assume it is broken. Vanilla gets its own crossplay wording because it has a way out —
    /// changing the type to Paper keeps the world — while Forge simply has no Geyser at all.
    /// </remarks>
    private void UpdateTypeDependentOptions()
    {
        var type = SelectedServerType();

        var crossplay = CrossplayService.CanEnable(type);
        CrossplayCheck.IsEnabled = crossplay;
        CrossplayHint.IsVisible = crossplay;
        CrossplayWhyNot.IsVisible = !crossplay;
        var caveat = CrossplayService.CaveatKey(type);
        CrossplayModdedNote.IsVisible = crossplay && caveat is not null;
        if (caveat is not null) CrossplayModdedNote.Text = Localizer.Get(caveat);

        if (!crossplay)
        {
            CrossplayCheck.IsChecked = false;
            CrossplayWhyNot.Text = Localizer.Get(type == ServerType.Vanilla
                ? "Crossplay_UnsupportedVanilla"
                : "Crossplay_Unsupported");
        }

        var multiVersion = MultiVersionService.CanEnable(type);
        MultiVersionCheck.IsEnabled = multiVersion;
        MultiVersionHint.IsVisible = multiVersion;
        MultiVersionWhyNot.IsVisible = !multiVersion;
        if (!multiVersion) MultiVersionCheck.IsChecked = false;

        var modContent = HydraulicService.CanEnable(type);
        HydraulicCheck.IsEnabled = modContent;
        HydraulicHint.IsVisible = modContent;
        HydraulicWhyNot.IsVisible = !modContent;
        if (!modContent) HydraulicCheck.IsChecked = false;
    }

    /// <summary>The picked server type.</summary>
    /// <remarks>
    /// The picker carries the enum value itself. This used to parse the string in a ComboBoxItem's
    /// Tag, where a typo produced no error at all: the parse failed, the fallback took over, and
    /// you got a Vanilla server having asked for something else.
    /// </remarks>
    private ServerType SelectedServerType() => TypePicker.SelectedType;

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
