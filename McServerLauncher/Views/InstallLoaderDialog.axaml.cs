using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// Installs a mod loader (Fabric for now) into an EXISTING server folder, turning a vanilla
/// server into a moddable one. On success it updates the passed <see cref="ServerConfig"/> in place
/// and the dialog returns true.
/// </summary>
public partial class InstallLoaderDialog : Window
{
    private readonly MinecraftVersionService _versions = new();
    private readonly ModLoaderService _mods = new();
    private readonly JavaService _java = new();
    private readonly ServerCreationService _creation = new();
    private readonly ServerConfig _config;

    private List<MinecraftVersion> _allVersions = new();
    private string _latestRelease = string.Empty;
    private string? _detectedVersion;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public InstallLoaderDialog() : this(new ServerConfig()) { }

    // --- Log batching (see CreateServerDialog): the Forge installer prints thousands of lines. ---
    private const int MaxLogLines = 400;
    private readonly List<string> _logLines = new();
    private bool _logDirty;
    private readonly DispatcherTimer _logTimer;

    public InstallLoaderDialog(ServerConfig config)
    {
        InitializeComponent();
        _config = config;
        Loaded += OnLoaded;
        LoaderCombo.SelectionChanged += (_, _) => UpdateWarning();
        UpdateWarning();

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _logTimer.Stop();
        base.OnClosed(e);
    }

    /// <summary>Shows a warning whose wording and color depend on the conversion direction.</summary>
    private void UpdateWarning()
    {
        var current = _config.Type;
        var target = LoaderCombo.SelectedIndex switch
        {
            1 => ServerType.Vanilla,
            2 => ServerType.Forge,
            _ => ServerType.Fabric
        };

        string key, bg, border;
        bool danger;
        if (current == target)
            (key, bg, border, danger) = ("Loader_WarnSame", "#33E3A82B", "#E3A82B", false);
        else if (current == ServerType.Vanilla)
            (key, bg, border, danger) = ("Loader_WarnVanillaToLoader", "#332E7D32", "#3FB950", false);
        else if (target == ServerType.Vanilla)
            (key, bg, border, danger) = ("Loader_WarnToVanilla", "#33E05561", "#E05561", true);
        else // crossing between Fabric and Forge
            (key, bg, border, danger) = ("Loader_WarnCrossLoader", "#33E05561", "#E05561", true);

        WarnText.Text = Localizer.Get(key);
        WarnText.FontWeight = danger ? FontWeight.SemiBold : FontWeight.Normal;
        WarnBox.Background = new SolidColorBrush(Color.Parse(bg));
        WarnBox.BorderBrush = new SolidColorBrush(Color.Parse(border));
        WarnIcon.Symbol = danger ? Symbol.Warning : Symbol.Info;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Try to detect the server's current Minecraft version (from the vanilla jar) to pre-select it,
        // so converting keeps the same version as the existing world.
        _detectedVersion = _java.GetGameVersionFromJar(_config.JarFullPath)
                           ?? (string.IsNullOrWhiteSpace(_config.GameVersion) ? null : _config.GameVersion);
        try
        {
            var (latest, list) = await _versions.GetVersionsAsync();
            _latestRelease = latest;
            _allVersions = list;
            PopulateVersions();
            VersionStatus.Text = _detectedVersion is not null
                ? string.Format(Localizer.Get("Loader_DetectedFmt"), _detectedVersion)
                : string.Format(Localizer.Get("Msg_LatestRelease"), latest);
        }
        catch (Exception ex)
        {
            VersionStatus.Text = string.Format(Localizer.Get("Msg_VersionsLoadError"), ex.Message);
        }
    }

    private void Snapshots_Changed(object? sender, RoutedEventArgs e) => PopulateVersions();

    private void PopulateVersions()
    {
        if (_allVersions.Count == 0) return;

        var includeSnapshots = SnapshotsCheck.IsChecked == true;
        var filtered = includeSnapshots ? _allVersions : _allVersions.Where(v => v.IsRelease).ToList();
        VersionCombo.ItemsSource = filtered;

        var preferred = (_detectedVersion is not null ? filtered.FirstOrDefault(v => v.Id == _detectedVersion) : null)
                        ?? filtered.FirstOrDefault(v => v.Id == _latestRelease)
                        ?? filtered.FirstOrDefault();
        VersionCombo.SelectedItem = preferred;
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (VersionCombo.SelectedItem is not MinecraftVersion version)
        {
            await Warn(Localizer.Get("Msg_SelectVersion"));
            return;
        }

        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        try
        {
            AppendLog(string.Format(Localizer.Get("Msg_Resolving"), version.Id));
            var details = await _versions.GetVersionDetailsAsync(version);

            AppendLog(string.Format(Localizer.Get("Msg_CheckingJava"), version.Id, details.JavaMajor));
            var javaPath = _config.JavaPath;
            try
            {
                javaPath = await _java.EnsureJavaAsync(details.JavaMajor, progress);
            }
            catch (Exception jex)
            {
                AppendLog(string.Format(Localizer.Get("Msg_JavaPrepareFail"), details.JavaMajor, jex.Message));
                AppendLog(Localizer.Get("Msg_UseSystemJava"));
            }

            // Update the existing server's config in place (the world is kept).
            if (LoaderCombo.SelectedIndex == 1)
            {
                // Revert to Vanilla: download the vanilla server jar for this version.
                const string jarName = "server.jar";
                await _versions.DownloadFileAsync(details.ServerUrl, Path.Combine(_config.FolderPath, jarName), progress);
                if (KeepRunBatCheck.IsChecked != true)
                    _creation.WriteRunBat(_config.FolderPath, _config.MinRamGb, _config.MaxRamGb, jarName, javaPath);

                _config.Type = ServerType.Vanilla;
                _config.GameVersion = version.Id;
                _config.ModLoaderVersion = string.Empty;
                _config.ForgeArgs = string.Empty;
                _config.JarFile = jarName;
                _config.JavaPath = javaPath;
            }
            else if (LoaderCombo.SelectedIndex == 2)
            {
                // Forge: run the official installer in the server folder.
                AppendLog(Localizer.Get("Msg_ForgeResolving"));
                var forgeVersion = await _mods.GetRecommendedForgeVersionAsync(version.Id);
                if (string.IsNullOrEmpty(forgeVersion))
                    throw new InvalidOperationException(string.Format(Localizer.Get("Msg_ForgeNoVersion"), version.Id));

                // The Forge installer overwrites run.bat; back it up if the user asked to keep it.
                var runBatPath = Path.Combine(_config.FolderPath, "run.bat");
                var keptRunBat = KeepRunBatCheck.IsChecked == true && File.Exists(runBatPath)
                    ? File.ReadAllText(runBatPath) : null;

                var forge = await _mods.InstallForgeServerAsync(_config.FolderPath, version.Id, forgeVersion, javaPath, progress);
                if (forge.ArgsId is not null)
                {
                    // Modern Forge: launched via args file; Forge's own run.bat reads user_jvm_args.txt.
                    _config.ForgeArgs = forge.ArgsId;
                    _config.JarFile = "server.jar";
                    if (KeepRunBatCheck.IsChecked != true)
                        _creation.WriteForgeUserJvmArgs(_config.FolderPath, _config.MinRamGb, _config.MaxRamGb);
                }
                else if (!string.IsNullOrEmpty(forge.JarFile))
                {
                    // Old Forge (≤1.16.5): a runnable forge-*.jar.
                    _config.ForgeArgs = string.Empty;
                    _config.JarFile = forge.JarFile;
                    if (KeepRunBatCheck.IsChecked != true)
                        _creation.WriteRunBat(_config.FolderPath, _config.MinRamGb, _config.MaxRamGb, forge.JarFile, javaPath);
                }
                else
                {
                    throw new InvalidOperationException(Localizer.Get("Msg_ForgeInstallNoOutput"));
                }

                if (keptRunBat is not null)
                    File.WriteAllText(runBatPath, keptRunBat);

                _config.Type = ServerType.Forge;
                _config.GameVersion = version.Id;
                _config.ModLoaderVersion = forgeVersion;
                _config.JavaPath = javaPath;
            }
            else
            {
                // Fabric (index 0).
                AppendLog(Localizer.Get("Msg_FabricResolving"));
                var loaderVersion = await _mods.GetLatestFabricLoaderVersionAsync();
                const string jarName = "fabric-server.jar";
                await _mods.DownloadFabricServerAsync(version.Id, loaderVersion,
                    Path.Combine(_config.FolderPath, jarName), progress);
                if (KeepRunBatCheck.IsChecked != true)
                    _creation.WriteRunBat(_config.FolderPath, _config.MinRamGb, _config.MaxRamGb, jarName, javaPath);

                _config.Type = ServerType.Fabric;
                _config.GameVersion = version.Id;
                _config.ModLoaderVersion = loaderVersion;
                _config.ForgeArgs = string.Empty;
                _config.JarFile = jarName;
                _config.JavaPath = javaPath;
            }

            AppendLog(Localizer.Get("Loader_Done"));
            Close(true);
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
            await Warn(string.Format(Localizer.Get("Loader_Error"), ex.Message));
            SetBusy(false);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void SetBusy(bool busy)
    {
        FormPanel.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        ProgressBox.IsVisible = busy;
        Spinner.IsIndeterminate = busy;
    }

    private void AppendLog(string line)
    {
        _logLines.Add(line);
        if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
        _logDirty = true;
    }

    private void FlushLog()
    {
        if (!_logDirty) return;
        _logDirty = false;
        ProgressLog.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
        ProgressLog.CaretIndex = ProgressLog.Text.Length;
    }

    private Task Warn(string message) => MessageBox.ShowAsync(message, Localizer.Get("Loader_Title"), this);
}
