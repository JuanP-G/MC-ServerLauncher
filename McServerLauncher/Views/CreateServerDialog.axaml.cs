using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// Creates a server from nothing — type, version, port and RAM in, a running server out — or takes
/// over a folder that already holds one.
/// </summary>
/// <remarks>
/// <para>
/// This is the dialog that orchestrates the create path — <see cref="ServerJarInstaller"/> for the
/// jar, <see cref="JavaService"/> for the runtime, <see cref="PortService"/> for a free port and
/// <see cref="ServerCreationService"/> for the initial files — rather than owning any of it.
/// </para>
/// <para>
/// The second mode replaced a separate "Add" button whose dialog asked for a jar name by hand and
/// ignored the crossplay, tunnel and start options. Here the folder is read by
/// <see cref="ServerDetectionService.Detect"/>, what it says is filled in and locked, and only what
/// it could not say is asked for. <see cref="ExistingServer"/> builds the result and keeps the
/// folder untouched beyond the port.
/// </para>
/// </remarks>
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

    /// <summary>Folders the app already manages, so the same one cannot be added twice.</summary>
    private readonly List<string> _registeredFolders;

    /// <summary>What the chosen existing folder turned out to be, and which folder that was.</summary>
    private ServerDetection? _detected;
    private string? _detectedFolder;
    private readonly ServerDetectionService _detection = new();

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public CreateServerDialog() : this(null) { }

    // Buffered progress log (see LogBatcher: the Forge installer prints thousands of lines).
    private readonly LogBatcher _log;

    public CreateServerDialog(IEnumerable<int>? usedPorts = null, IEnumerable<string>? registeredFolders = null)
    {
        InitializeComponent();
        _usedPorts = new HashSet<int>(usedPorts ?? Enumerable.Empty<int>());
        _registeredFolders = (registeredFolders ?? Enumerable.Empty<string>()).ToList();

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
        // Typed or pasted rather than browsed to: read it once the box is left.
        ExistingFolderBox.LostFocus += (_, _) => _ = DetectFolderAsync(ExistingFolderBox.Text);
        UpdateTypeDependentOptions();
        Loaded += OnLoaded;
    }

    /// <summary>Whether the dialog is taking over a folder rather than making a server.</summary>
    /// <remarks>
    /// Kept in a field, not read off the radio buttons: a RadioButton announces that it was checked
    /// before its sibling has been cleared, so during that event both say yes — the same trap the
    /// type picker documents. Taken from whichever button just became checked, it cannot be stale.
    /// </remarks>
    private bool IsExistingMode => _existingMode;
    private bool _existingMode;

    private void Mode_Changed(object? sender, RoutedEventArgs e)
    {
        // Both radios raise this; only the one that became checked says which mode it is now.
        if (sender is not RadioButton { IsChecked: true } radio) return;

        var existing = _existingMode = radio == ExistingModeRadio;
        ExistingPanel.IsVisible = existing;
        NewFolderPanel.IsVisible = !existing;
        SeedPanel.IsVisible = !existing;
        CreateButton.Content = Localizer.Get(existing ? "Cs_AddButton" : "Cs_CreateButton");

        if (existing)
        {
            ApplyDetection();
        }
        else
        {
            // Back to making one: nothing a folder said still applies.
            TypePicker.IsEnabled = VersionCombo.IsEnabled = SnapshotsCheck.IsEnabled = true;
            PopulateVersions();
        }
        UpdatePathWarning();
    }

    private async void BrowseExisting_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("Cs_ExistingFolder"),
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        ExistingFolderBox.Text = path;
        await DetectFolderAsync(path);
    }

    /// <summary>
    /// Reads the folder off the UI thread — a big modpack has a lot of jars to open — and shows what
    /// it found.
    /// </summary>
    internal async Task DetectFolderAsync(string? folder)
    {
        folder = folder?.Trim();
        if (string.IsNullOrEmpty(folder) || folder == _detectedFolder) return;

        _detectedFolder = folder;
        var found = await Task.Run(() => _detection.Detect(folder));
        if (folder != _detectedFolder) return;   // another folder was chosen while this one was read

        ShowDetection(folder, found);
    }

    /// <summary>Takes what a folder turned out to be and puts it on screen.</summary>
    /// <remarks>Apart from the reading so that the screen side can be tested without waiting on a thread.</remarks>
    internal void ShowDetection(string folder, ServerDetection found)
    {
        _detectedFolder = folder;
        _detected = found;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ApplyDetection();
        UpdatePathWarning();
    }

    /// <summary>
    /// Puts what the folder said into the form and locks it, and says what is still missing.
    /// </summary>
    /// <remarks>
    /// Locked because it is not a choice: a folder with Fabric's launcher in it is a Fabric server
    /// whatever the picker says, and letting the two disagree would produce a config that launches
    /// the wrong way. Whatever could not be told stays open to pick.
    /// </remarks>
    internal void ApplyDetection()
    {
        var found = _detected;
        if (!IsExistingMode || found is null)
        {
            DetectionSummary.IsVisible = DetectionMissing.IsVisible = JarPanel.IsVisible = false;
            return;
        }

        if (found.Type is { } type)
        {
            TypePicker.SelectedType = type;
            UpdateTypeDependentOptions();
        }
        TypePicker.IsEnabled = found.Type is null;

        if (!string.IsNullOrEmpty(found.GameVersion)) SelectVersion(found.GameVersion);
        VersionCombo.IsEnabled = SnapshotsCheck.IsEnabled = string.IsNullOrEmpty(found.GameVersion);

        if (found.Port is { } port) PortBox.Value = port;
        if (found.MinRamGb is { } min) MinRamBox.Value = min;
        if (found.MaxRamGb is { } max) MaxRamBox.Value = max;

        var needsJar = string.IsNullOrEmpty(found.ForgeArgs) && string.IsNullOrEmpty(found.JarFile);
        JarPanel.IsVisible = needsJar && found.Jars.Count > 0;
        if (JarPanel.IsVisible)
        {
            JarCombo.ItemsSource = found.Jars;
            JarCombo.SelectedIndex = 0;
        }

        DetectionSummary.IsVisible = found.Type is not null;
        DetectionSummary.Text = Summary(found);

        var missing = Missing(found, needsJar);
        DetectionMissing.IsVisible = missing is not null;
        DetectionMissing.Text = missing ?? string.Empty;
    }

    /// <summary>"✔ Fabric 1.21.1 · loader 0.16.2 · port 25565 · 4 GB · with a world".</summary>
    internal static string Summary(ServerDetection found)
    {
        if (found.Type is not { } type) return string.Empty;

        var parts = new List<string> { (ServerTypeCatalog.For(type).DisplayName + " " + found.GameVersion).Trim() };
        if (!string.IsNullOrEmpty(found.LoaderVersion))
            parts.Add(string.Format(Localizer.Get("Cs_DetectedLoaderFmt"), found.LoaderVersion));
        if (found.Port is { } port) parts.Add(string.Format(Localizer.Get("Cs_DetectedPortFmt"), port));
        if (found.MaxRamGb is { } max) parts.Add(max + " GB");
        parts.Add(Localizer.Get(found.HasWorld ? "Cs_DetectedWorld" : "Cs_DetectedNoWorld"));
        return "✔ " + string.Join(" · ", parts);
    }

    private static string? Missing(ServerDetection found, bool needsJar)
    {
        if (found.Type is null && found.Jars.Count == 0) return Localizer.Get("Cs_ExistingNotAServer");
        if (found.Type is null) return Localizer.Get("Cs_ExistingPickType");
        if (string.IsNullOrEmpty(found.GameVersion)) return Localizer.Get("Cs_ExistingPickVersion");
        if (needsJar) return Localizer.Get("Cs_ExistingPickJar");
        return null;
    }

    /// <summary>
    /// Selects a version in the list, adding it if the list does not have it — a snapshot while
    /// snapshots are hidden, a version older than the list reaches, or the list not loaded yet.
    /// </summary>
    private void SelectVersion(string id)
    {
        var known = _allVersions.FirstOrDefault(v => v.Id == id);
        if (known is { IsRelease: false } && SnapshotsCheck.IsChecked != true)
            SnapshotsCheck.IsChecked = true;   // repopulates, and lands back here

        var items = (VersionCombo.ItemsSource as IEnumerable<MinecraftVersion>)?.ToList() ?? new List<MinecraftVersion>();
        var match = items.FirstOrDefault(v => v.Id == id);
        if (match is null)
        {
            match = known ?? new MinecraftVersion { Id = id, Type = "release" };
            items.Insert(0, match);
            VersionCombo.ItemsSource = items;
        }
        VersionCombo.SelectedItem = match;
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

        // The list can arrive after the folder was read; the folder's version still wins.
        if (IsExistingMode && _detected?.GameVersion is { Length: > 0 } detected)
            SelectVersion(detected);
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
        if (IsExistingMode) return ExistingFolderBox.Text?.Trim() ?? string.Empty;

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
        if (IsExistingMode)
        {
            await AddExistingAsync();
            return;
        }

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
            var seed = SeedBox.Text?.Trim();
            if (!string.IsNullOrEmpty(seed) && ServerCreationService.WorldExists(folder))
                AppendLog(Localizer.Get("Msg_SeedIgnoredWorldExists"));
            _creation.WriteInitialProperties(folder, port, $"{name} - MC Server Launcher", seed);

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
    /// Takes over the chosen folder: nothing downloaded, nothing installed, nothing written but the
    /// port if it was changed.
    /// </summary>
    private async Task AddExistingAsync()
    {
        var folder = ExistingFolderBox.Text?.Trim() ?? string.Empty;
        if (folder != _detectedFolder) await DetectFolderAsync(folder);
        var found = _detected ?? ServerDetection.Nothing;

        var type = found.Type ?? SelectedServerType();
        var form = new ExistingServerForm(
            Name: NameBox.Text?.Trim() ?? string.Empty,
            Folder: folder,
            Type: type,
            GameVersion: (VersionCombo.SelectedItem as MinecraftVersion)?.Id,
            JarFile: JarCombo.SelectedItem as string,
            MinRamGb: (int)(MinRamBox.Value ?? 2m),
            MaxRamGb: (int)(MaxRamBox.Value ?? 4m),
            JavaPath: "java",
            Playit: PlayitCheck.IsChecked == true,
            Crossplay: CrossplayCheck.IsChecked == true,
            MultiVersion: MultiVersionCheck.IsChecked == true,
            Hydraulic: HydraulicCheck.IsChecked == true);

        if (ExistingServer.Problem(form, found, _registeredFolders) is { } problem)
        {
            await Warn(Localizer.Get(problem));
            return;
        }

        var port = (int)(PortBox.Value ?? 25565m);
        if (_usedPorts.Contains(port)) { await Warn(string.Format(Localizer.Get("Msg_PortAssigned"), port)); return; }
        // Only a port the user changed is checked against the system: the one the folder already
        // uses may well be busy because this very server is running outside the app right now.
        if (port != found.Port && _ports.IsPortInUse(port))
        {
            await Warn(string.Format(Localizer.Get("Msg_PortInUseOther"), port));
            return;
        }

        // A warning rather than a refusal, unlike when creating: the folder is already there and
        // already named, and whether it works is something its owner can see for themselves.
        if (ServerNameRule.Check(folder, type) is { } issue
            && !await MessageBox.ConfirmAsync(
                Describe(issue, folder) + Environment.NewLine + Environment.NewLine + Localizer.Get("Cs_ExistingAddAnyway"),
                Localizer.Get("CreateServer"), this))
            return;

        SetBusy(true);
        try
        {
            // The Java this Minecraft needs, as when creating — when the version is one Mojang's
            // list knows. Otherwise the start does it: it checks the Java every time anyway.
            var javaPath = "java";
            if (VersionCombo.SelectedItem is MinecraftVersion { Url.Length: > 0 } version)
            {
                try
                {
                    var details = await _versions.GetVersionDetailsAsync(version);
                    AppendLog(string.Format(Localizer.Get("Msg_CheckingJava"), version.Id, details.JavaMajor));
                    javaPath = await _java.EnsureJavaAsync(details.JavaMajor, new Progress<string>(AppendLog));
                }
                catch (Exception jex)
                {
                    AppendLog(string.Format(Localizer.Get("Msg_ErrorFmt"), jex.Message));
                    AppendLog(Localizer.Get("Msg_UseSystemJava"));
                }
            }

            if (ExistingServer.ApplyPort(folder, found.Port, port))
                AppendLog(string.Format(Localizer.Get("Cs_ExistingPortWrittenFmt"), port));

            ResultConfig = ExistingServer.ToConfig(form with { JavaPath = javaPath }, found);
            AutoStart = AutoStartCheck.IsChecked == true;
            CreateTunnel = ResultConfig.PlayitEnabled && CreateTunnelCheck.IsChecked == true;
            Close(true);
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
            await Warn(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
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
