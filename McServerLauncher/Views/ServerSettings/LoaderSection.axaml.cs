using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>
/// Changes the type of the server, keeping the world: Vanilla into a moddable or a plugin server,
/// one loader into another, or any of them back to Vanilla.
/// </summary>
/// <remarks>
/// <para>
/// It offers the same list as the new-server panel (the shared <see cref="ServerTypePicker"/>) and
/// installs through the same <see cref="ServerJarInstaller"/>, so the two can't drift apart. The
/// warning above the button is keyed on the direction of the change, because they are not equally
/// safe: gaining a loader is additive, dropping to Vanilla or crossing between families is not.
/// </para>
/// <para>
/// Unlike the rest of the page it acts at once: the files are downloaded and the live config is
/// written as it goes, which is why it waits until nothing else on the page is unsaved. Otherwise
/// the install would run with the saved RAM and folder while the page showed others, and a later
/// Discard would seem to undo a conversion that can no longer be undone.
/// </para>
/// </remarks>
public partial class LoaderSection : UserControl, IServerSettingsSection
{
    /// <summary>The fields an install writes into the live config.</summary>
    internal static readonly string[] InstalledFields =
    [
        nameof(ServerConfig.Type), nameof(ServerConfig.GameVersion), nameof(ServerConfig.ModLoaderVersion),
        nameof(ServerConfig.ForgeArgs), nameof(ServerConfig.JarFile), nameof(ServerConfig.JavaPath),
    ];

    private readonly MinecraftVersionService _versions = new();
    private readonly ServerJarInstaller _installer = new();
    private readonly JavaService _java = new();
    private readonly ServerCreationService _creation = new();
    private readonly ServerSettingsDraft _draft;

    private List<MinecraftVersion> _allVersions = new();
    private string _latestRelease = string.Empty;
    private string? _detectedVersion;
    private bool _versionsRequested;
    private bool _busy;

    // Buffered progress log (see LogBatcher: the Forge installer prints thousands of lines).
    private readonly LogBatcher _log;

    /// <summary>Raised once a loader is installed: the live config and the disk have changed.</summary>
    public event Action? Installed;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public LoaderSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public LoaderSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        _log = new LogBatcher(ProgressLog);

        // Start on the type the server already is, so pressing Install without touching
        // anything converts nothing. The old drop-down opened on Fabric whatever the
        // server was, which made the destructive option the default one.
        TypePicker.SelectedType = draft.Live.Type;
        TypePicker.SelectionChanged += (_, _) => UpdateWarning();
        draft.Changed += UpdateInstallable;

        Reload();
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _log.Start();
        // Only the first time it is shown: a page nobody opens should not reach the network.
        if (_versionsRequested) return;
        _versionsRequested = true;
        await LoadVersionsAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _log.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    public void Reload()
    {
        var config = _draft.Live;
        CurrentText.Text = Localizer.Get("Loader_Current") + config.Type + " " + config.GameVersion;
        UpdateWarning();
        UpdateInstallable();
    }

    /// <inheritdoc />
    public void ShowError(string? message)
    {
        Error.Text = message;
        Error.IsVisible = message is not null;
    }

    /// <summary>The picked loader.</summary>
    private ServerType SelectedLoader() => TypePicker.SelectedType;

    private void UpdateInstallable()
    {
        var unsaved = _draft.IsDirty;
        NeedsSaveNote.IsVisible = unsaved && !_busy;
        InstallButton.IsEnabled = !unsaved && !_busy;
    }

    /// <summary>Shows a warning whose wording and color depend on the conversion direction.</summary>
    private void UpdateWarning()
    {
        var current = _draft.Live.Type;
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

    private async Task LoadVersionsAsync()
    {
        // Try to detect the server's current Minecraft version (from the vanilla jar) to pre-select it,
        // so converting keeps the same version as the existing world.
        var config = _draft.Live;
        _detectedVersion = _java.GetGameVersionFromJar(config.JarFullPath)
                           ?? (string.IsNullOrWhiteSpace(config.GameVersion) ? null : config.GameVersion);
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
        ShowError(null);
        var config = _draft.Live;

        if (!Directory.Exists(config.FolderPath))
        {
            ShowError(Localizer.Get("Msg_FolderNotExist"));
            return;
        }
        if (VersionCombo.SelectedItem is not MinecraftVersion version)
        {
            ShowError(Localizer.Get("Msg_SelectVersion"));
            return;
        }

        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        try
        {
            AppendLog(string.Format(Localizer.Get("Msg_Resolving"), version.Id));
            var details = await _versions.GetVersionDetailsAsync(version);

            AppendLog(string.Format(Localizer.Get("Msg_CheckingJava"), version.Id, details.JavaMajor));
            var javaPath = config.JavaPath;
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
            var runBatPath = Path.Combine(config.FolderPath, "run.bat");
            var keepRunBat = KeepRunBatCheck.IsChecked == true;
            var keptRunBat = keepRunBat && File.Exists(runBatPath) ? File.ReadAllText(runBatPath) : null;

            // Checked before anything is downloaded or moved: converting to Paper in a folder it
            // refuses to run from produces a server that installs perfectly and never starts.
            if (BukkitPathRule.Rejects(config.FolderPath, target))
            {
                var bad = BukkitPathRule.OffendingCharacter(config.FolderPath)!.Value;
                AppendLog(string.Format(Localizer.Get("Msg_BukkitPathFmt"), bad));
                ShowError(string.Format(Localizer.Get("Msg_BukkitPathFmt"), bad));
                SetBusy(false);
                return;
            }

            // Before the config says the new type: the old family's folder has to be found under
            // the name the OLD type used.
            var archived = ContentMigrationService.ArchiveIfFamilyChanged(
                config.FolderPath, config.Type, target, DateTime.Now);
            if (archived is not null)
                AppendLog(string.Format(Localizer.Get("Msg_ContentArchivedFmt"), archived));

            var installed = await _installer.InstallAsync(
                target, config.FolderPath, version.Id, details, javaPath,
                config.MinRamGb, config.MaxRamGb, progress, writeLoaderJvmArgs: !keepRunBat);

            if (keptRunBat is not null)
                File.WriteAllText(runBatPath, keptRunBat);
            else if (!keepRunBat && !installed.LaunchesViaArgsFile)
                _creation.WriteRunBat(config.FolderPath, config.MinRamGb, config.MaxRamGb,
                    installed.JarFile, javaPath);

            config.Type = target;
            config.GameVersion = version.Id;
            config.ModLoaderVersion = installed.LoaderVersion;
            config.ForgeArgs = installed.ForgeArgs;
            // A loader launched through an args file has no runnable jar; the field still has to
            // name something, and "server.jar" is what the rest of the app expects to find there.
            config.JarFile = installed.LaunchesViaArgsFile ? "server.jar" : installed.JarFile;
            config.JavaPath = javaPath;

            AppendLog(Localizer.Get("Loader_Done"));
            SetBusy(false, keepLog: true);
            Installed?.Invoke();
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localizer.Get("Msg_ErrorFmt"), ex.Message));
            ShowError(string.Format(Localizer.Get("Loader_Error"), ex.Message));
            SetBusy(false, keepLog: true);
        }
    }

    private void SetBusy(bool busy, bool keepLog = false)
    {
        _busy = busy;
        FormPanel.IsEnabled = !busy;
        ProgressBox.IsVisible = busy || keepLog;
        Spinner.IsIndeterminate = busy;
        Spinner.IsVisible = busy;
        UpdateInstallable();
    }

    private void AppendLog(string line) => _log.Append(line);
}
