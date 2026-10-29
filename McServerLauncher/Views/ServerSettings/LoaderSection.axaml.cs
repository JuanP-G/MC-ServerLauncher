using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

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
/// <para>
/// It was a modal dialog once, and nothing else could happen while it ran. On a page that is not
/// modal it has to say so itself: no install while the server is running or getting ready to start,
/// no start while it installs (<see cref="ServerViewModel.IsConverting"/>), and the page cannot be
/// left or saved meanwhile (<see cref="IsBusy"/>).
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
    private readonly JavaService _java = new();
    private readonly LoaderChangeService _loaderChange = new();
    private readonly ServerSettingsDraft _draft;
    private readonly ServerViewModel _server;

    private List<MinecraftVersion> _allVersions = new();
    private string _latestRelease = string.Empty;
    private string? _detectedVersion;
    private bool _versionsRequested;
    private bool _busy;

    // Buffered progress log (see LogBatcher: the Forge installer prints thousands of lines).
    private readonly LogBatcher _log;

    /// <summary>Raised once a loader is installed: the live config and the disk have changed.</summary>
    public event Action? Installed;

    /// <summary>True while an install is downloading or writing into the server's folder.</summary>
    public bool IsBusy => _busy;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public LoaderSection()
        : this(new ServerSettingsDraft(new ServerConfig()), new ServerViewModel(new ServerConfig())) { }

    public LoaderSection(ServerSettingsDraft draft, ServerViewModel server)
    {
        InitializeComponent();
        _draft = draft;
        _server = server;
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
        CurrentText.Text = string.Format(Localizer.Get("Loader_CurrentFmt"),
            ServerTypeCatalog.For(config.Type).DisplayName, config.GameVersion);
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
        InstallButton.IsEnabled = !unsaved && !_busy && !ServerIsUp;
    }

    /// <summary>The server is running or getting ready to: its files are not for replacing.</summary>
    private bool ServerIsUp => _server.IsRunning || _server.IsPreparing;

    /// <summary>Shows a warning whose wording and color depend on the conversion direction.</summary>
    private void UpdateWarning()
    {
        var current = _draft.Live.Type;
        var target = SelectedLoader();

        string key, fill, edge;
        bool danger;
        if (current == target)
            (key, fill, edge, danger) =
                ("Loader_WarnSame", "SemanticWarnPanel", "SemanticWarn", false);
        else if (current == ServerType.Vanilla)
            (key, fill, edge, danger) =
                ("Loader_WarnVanillaToLoader", "SemanticOkPanel", "SemanticOk", false);
        else if (target == ServerType.Vanilla)
            (key, fill, edge, danger) =
                ("Loader_WarnToVanilla", "SemanticDangerPanel", "SemanticDanger", true);
        else // crossing between loaders: Fabric, Forge, NeoForge, Paper
            (key, fill, edge, danger) =
                ("Loader_WarnCrossLoader", "SemanticDangerPanel", "SemanticDanger", true);

        WarnText.Text = Localizer.Get(key);
        WarnText.FontWeight = danger ? FontWeight.SemiBold : FontWeight.Normal;
        WarnBox.Background = this.FindResource(fill) as IBrush;
        WarnBox.BorderBrush = this.FindResource(edge) as IBrush;
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
            VersionStatus.Text = string.Format(Localizer.Get("Msg_VersionsLoadError"),
                ErrorMessages.Describe(ex));
        }
    }

    private void Snapshots_Changed(object? sender, RoutedEventArgs e) => PopulateVersions();

    private void PopulateVersions()
    {
        if (_allVersions.Count == 0) return;

        var includeSnapshots = SnapshotsCheck.IsChecked == true;
        var filtered = includeSnapshots ? _allVersions : _allVersions.Where(v => v.IsRelease).ToList();
        VersionCombo.ItemsSource = filtered;

        var preferred = (_detectedVersion is not null ? filtered.FirstOrDefault(v => v.Id == _detectedVersion)
            : null)
                        ?? filtered.FirstOrDefault(v => v.Id == _latestRelease)
                        ?? filtered.FirstOrDefault();
        VersionCombo.SelectedItem = preferred;
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        ShowError(null);
        var config = _draft.Live;

        // Read once, here, and not from the live config after each await: the rest of the page can
        // be saved meanwhile, and a folder changed mid-install would split it between two folders.
        var folder = config.FolderPath;
        var target = SelectedLoader();

        if (InstallBlocker(folder, target, out var version) is { } blocker)
        {
            ShowError(blocker);
            return;
        }

        if (!await ConvertAsync(config, folder, target, version!)) return;

        // Outside the install's try: what the page and the window do with a finished install
        // (taking the new type in, saving servers.json) failing is not the install failing, and the
        // log had just said it was done.
        try
        {
            Installed?.Invoke();
        }
        catch (Exception ex)
        {
            ShowError(string.Format(Localizer.Get("Msg_ConfigSaveError"), ErrorMessages.Describe(ex)));
        }
    }

    /// <summary>
    /// Why the install cannot start, or null when it can (and then <paramref name="version"/> is the
    /// one chosen). Checked before anything is downloaded or moved.
    /// </summary>
    private string? InstallBlocker(string folder, ServerType target, out MinecraftVersion? version)
    {
        version = null;

        if (ServerIsUp) return Localizer.Get("Loader_StopServerFirst");
        if (!Directory.Exists(folder)) return Localizer.Get("Msg_FolderNotExist");
        if (VersionCombo.SelectedItem is not MinecraftVersion chosen) return Localizer.Get("Msg_SelectVersion");

        // Converting to Paper in a folder it refuses to run from produces a server that installs
        // perfectly and never starts.
        if (BukkitPathRule.Rejects(folder, target))
            return string.Format(Localizer.Get("Msg_BukkitPathFmt"), BukkitPathRule.OffendingCharacter(folder)!.Value);

        version = chosen;
        return null;
    }

    /// <summary>
    /// Runs the conversion and, if it worked, takes its result into the server's config. True when the
    /// install finished; false when it failed, after saying so on the page.
    /// </summary>
    private async Task<bool> ConvertAsync(ServerConfig config, string folder, ServerType target,
        MinecraftVersion version)
    {
        SetBusy(true);
        try
        {
            var result = await _loaderChange.ConvertAsync(
                new LoaderChange(folder, config.Type, target, version, config.JavaPath,
                    config.MinRamGb, config.MaxRamGb, KeepRunBatCheck.IsChecked == true),
                AppendLog);

            // Update the existing server's config in place (the world is kept).
            config.Type = result.Type;
            config.GameVersion = result.GameVersion;
            config.ModLoaderVersion = result.ModLoaderVersion;
            config.ForgeArgs = result.ForgeArgs;
            config.JarFile = result.JarFile;
            config.JavaPath = result.JavaPath;

            AppendLog(Localizer.Get("Loader_Done"));
            return true;
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
            ShowError(string.Format(Localizer.Get("Loader_Error"), ErrorMessages.Describe(ex)));
            return false;
        }
        finally
        {
            SetBusy(false, keepLog: true);
        }
    }

    private void SetBusy(bool busy, bool keepLog = false)
    {
        _busy = busy;
        _server.IsConverting = busy;
        FormPanel.IsEnabled = !busy;
        ProgressBox.IsVisible = busy || keepLog;
        Spinner.IsIndeterminate = busy;
        Spinner.IsVisible = busy;
        UpdateInstallable();
    }

    private void AppendLog(string line) => _log.Append(line);
}
