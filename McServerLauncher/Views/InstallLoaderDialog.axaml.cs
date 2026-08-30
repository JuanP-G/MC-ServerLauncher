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
    private readonly ServerJarInstaller _installer = new();
    private readonly JavaService _java = new();
    private readonly ServerCreationService _creation = new();
    private readonly ServerConfig _config;

    private List<MinecraftVersion> _allVersions = new();
    private string _latestRelease = string.Empty;
    private string? _detectedVersion;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public InstallLoaderDialog() : this(new ServerConfig()) { }

    // Buffered progress log (see LogBatcher: the Forge installer prints thousands of lines).
    private readonly LogBatcher _log;

    public InstallLoaderDialog(ServerConfig config)
    {
        InitializeComponent();
        _config = config;
        Loaded += OnLoaded;
        // Start on the type the server already is, so pressing Install without touching
        // anything converts nothing. The old drop-down opened on Fabric whatever the
        // server was, which made the destructive option the default one.
        TypePicker.SelectedType = _config.Type;
        TypePicker.SelectionChanged += (_, _) => UpdateWarning();
        UpdateWarning();

        _log = new LogBatcher(ProgressLog);
    }

    protected override void OnClosed(EventArgs e)
    {
        _log.Stop();
        base.OnClosed(e);
    }

    /// <summary>The picked loader.</summary>
    /// <remarks>
    /// The same picker the create dialog uses, so the two can no longer offer different lists —
    /// which they did, in different orders, each parsing a string that could silently fail to
    /// match.
    /// </remarks>
    private ServerType SelectedLoader() => TypePicker.SelectedType;

    /// <summary>Shows a warning whose wording and color depend on the conversion direction.</summary>
    private void UpdateWarning()
    {
        var current = _config.Type;
        var target = SelectedLoader();

        string key, bg, border;
        bool danger;
        if (current == target)
            (key, bg, border, danger) = ("Loader_WarnSame", "#33E3A82B", "#E3A82B", false);
        else if (current == ServerType.Vanilla)
            (key, bg, border, danger) = ("Loader_WarnVanillaToLoader", "#332E7D32", "#3FB950", false);
        else if (target == ServerType.Vanilla)
            (key, bg, border, danger) = ("Loader_WarnToVanilla", "#33E05561", "#E05561", true);
        else // crossing between loaders: Fabric, Forge, NeoForge, Paper
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
            var target = SelectedLoader();

            // Forge and NeoForge installers overwrite run.bat. Keep the user's copy if they asked,
            // and leave user_jvm_args.txt alone in that case too: their script reads it, and
            // rewriting the memory settings would quietly undo whatever they had set.
            var runBatPath = Path.Combine(_config.FolderPath, "run.bat");
            var keepRunBat = KeepRunBatCheck.IsChecked == true;
            var keptRunBat = keepRunBat && File.Exists(runBatPath) ? File.ReadAllText(runBatPath) : null;

            // Before the config says the new type: the old family's folder has to be found under
            // the name the OLD type used.
            var archived = ContentMigrationService.ArchiveIfFamilyChanged(
                _config.FolderPath, _config.Type, target, DateTime.Now);
            if (archived is not null)
                AppendLog(string.Format(Localizer.Get("Msg_ContentArchivedFmt"), archived));

            var installed = await _installer.InstallAsync(
                target, _config.FolderPath, version.Id, details, javaPath,
                _config.MinRamGb, _config.MaxRamGb, progress, writeLoaderJvmArgs: !keepRunBat);

            if (keptRunBat is not null)
                File.WriteAllText(runBatPath, keptRunBat);
            else if (!keepRunBat && !installed.LaunchesViaArgsFile)
                _creation.WriteRunBat(_config.FolderPath, _config.MinRamGb, _config.MaxRamGb,
                    installed.JarFile, javaPath);

            _config.Type = target;
            _config.GameVersion = version.Id;
            _config.ModLoaderVersion = installed.LoaderVersion;
            _config.ForgeArgs = installed.ForgeArgs;
            // A loader launched through an args file has no runnable jar; the field still has to
            // name something, and "server.jar" is what the rest of the app expects to find there.
            _config.JarFile = installed.LaunchesViaArgsFile ? "server.jar" : installed.JarFile;
            _config.JavaPath = javaPath;

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

    private void AppendLog(string line) => _log.Append(line);

    private Task Warn(string message) => MessageBox.ShowAsync(message, Localizer.Get("Loader_Title"), this);
}
