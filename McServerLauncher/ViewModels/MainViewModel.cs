using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Main ViewModel: manages the server list, the selected server and persistence.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ServerStorageService _storage;
    private readonly AppSettingsService _settings;

    /// <summary>
    /// The settings, loaded once at startup and kept in memory (EFI-7): every use used to re-read
    /// and re-deserialize settings.json (and re-decrypt the Playit key). This view model is the
    /// only writer, so the cached instance can't go stale.
    /// </summary>
    private readonly AppSettings _appSettings;

    /// <summary>The main window, used as the owner of modal dialogs.</summary>
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public ObservableCollection<ServerViewModel> Servers { get; } = new();

    [ObservableProperty]
    private ServerViewModel? _selectedServer;

    public bool HasSelection => SelectedServer is not null;

    /// <summary>The new server being made or added, kept while it is unfinished; null otherwise.</summary>
    /// <remarks>
    /// Kept even while it is not on screen: opening a server from the list puts the panel away
    /// without throwing it out, and "+ Nuevo" brings it back where it was left. A download that
    /// had already started goes on meanwhile, and the server joins the list when it ends.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreatingServer), nameof(ShowServerDetail), nameof(ShowEmptyState),
        nameof(HasNewServerDraft), nameof(NewServerTip))]
    private NewServerView? _newServerPanel;

    /// <summary>Whether the new-server panel has the detail area, instead of a server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreatingServer), nameof(ShowServerDetail), nameof(ShowEmptyState),
        nameof(HasNewServerDraft), nameof(NewServerTip))]
    private bool _isNewServerOpen;

    /// <summary>The new-server panel is on screen.</summary>
    public bool IsCreatingServer => IsNewServerOpen && NewServerPanel is not null;

    /// <summary>An unfinished new server is waiting, put away while another server is being looked at.</summary>
    public bool HasNewServerDraft => NewServerPanel is not null && !IsNewServerOpen;

    public string NewServerTip => Localizer.Get(HasNewServerDraft ? "New_ResumeTip" : "New_Title");

    /// <summary>What was selected when the panel opened, to go back to if it is cancelled.</summary>
    private ServerViewModel? _beforeNewServer;

    /// <summary>The selected server's detail, unless the new-server panel has the space.</summary>
    public bool ShowServerDetail => HasSelection && !IsCreatingServer;

    public bool ShowEmptyState => !HasSelection && !IsCreatingServer;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateCheckText))]
    private string _updateText = string.Empty;

    [ObservableProperty]
    private bool _isUpdating;

    /// <summary>What the About screen says about updates; the banner keeps its own flag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckingUpdates), nameof(IsUpToDate), nameof(UpdateCheckFailed), nameof(UpdateCheckText))]
    private UpdateCheckState _updateCheckState = UpdateCheckState.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateCheckText))]
    private DateTime? _lastUpdateCheck;

    public bool IsCheckingUpdates => UpdateCheckState == UpdateCheckState.Checking;
    public bool IsUpToDate => UpdateCheckState == UpdateCheckState.UpToDate;
    public bool UpdateCheckFailed => UpdateCheckState == UpdateCheckState.Failed;

    /// <summary>The line under the version in About: what the last look found, and when.</summary>
    public string UpdateCheckText => UpdateCheckState switch
    {
        UpdateCheckState.Checking => Localizer.Get("Upd_Checking"),
        UpdateCheckState.UpToDate => string.Format(Localizer.Get("Upd_UpToDateFmt"), LastCheckClock()),
        UpdateCheckState.Available => UpdateText,
        UpdateCheckState.Failed => Localizer.Get("Upd_Failed"),
        _ => Localizer.Get("Upd_NotChecked"),
    };

    private string LastCheckClock() => LastUpdateCheck?.ToString("t", CultureInfo.CurrentCulture) ?? "";

    /// <summary>The version running, as its release calls it.</summary>
    public string VersionText =>
        Changelog.Format(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    /// <summary>"Version 1.12.1 · MIT", under the name in About.</summary>
    public string VersionLineText => string.Format(Localizer.Get("About_VersionFmt"), VersionText);

    // ---- Sections ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServersSection), nameof(IsTunnelsSection), nameof(IsSettingsSection), nameof(IsAboutSection))]
    private AppSection _section = AppSection.Servers;

    public bool IsServersSection => Section == AppSection.Servers;
    public bool IsTunnelsSection => Section == AppSection.Tunnels;
    public bool IsSettingsSection => Section == AppSection.Settings;
    public bool IsAboutSection => Section == AppSection.About;

    /// <summary>The settings screen, which saves as it goes.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The player-history settings changed: every server's recorder follows them now.</summary>
    internal void OnHistorySettingsChanged()
    {
        foreach (var server in Servers) server.History.OnSettingsChanged();
    }

    [RelayCommand]
    private void ShowSettings() => Section = AppSection.Settings;

    /// <summary>The tunnels screen: the Playit account, every tunnel on it, and what to do about them.</summary>
    public TunnelsViewModel Tunnels { get; }

    [RelayCommand]
    private void ShowServers() => Section = AppSection.Servers;

    [RelayCommand]
    private void ShowTunnels()
    {
        Section = AppSection.Tunnels;
        // Read when the screen opens, never on a timer: the account is another machine's data, and
        // asking for it while nobody is looking would spend requests on an answer no one reads. The
        // one exception is the reading just after start (Tunnels.PrefetchAsync), so the first opening
        // shows a table straight away; this one then refreshes it without emptying it.
        _ = Tunnels.RefreshAsync();
    }

    [RelayCommand]
    private void ShowAbout() => Section = AppSection.About;

    private string? _releaseUrl;
    private string? _packageUrl;
    private string? _packageName;
    private string? _checksumUrl;

    /// <summary>One entry of the language selector.</summary>
    /// <param name="Code">The culture code stored in <c>AppSettings.Language</c> (es, en, pt, fr, de).</param>
    /// <param name="Name">The language's name in itself, never translated — that is how a selector is read.</param>
    public record LanguageOption(string Code, string Name);

    public IReadOnlyList<LanguageOption> Languages { get; } = new List<LanguageOption>
    {
        new("es", "Español"),
        new("en", "English"),
        new("pt", "Português"),
        new("fr", "Français"),
        new("de", "Deutsch"),
    };

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    private bool _languageReady;

    /// <summary>Default constructor uses %APPDATA%; <paramref name="dataDir"/> is for tests.</summary>
    /// <remarks>
    /// Both files go in the same folder, so one parameter settles both services. A test that opened
    /// this view model without it would read the server list of whoever is running the test and
    /// write its own back over it.
    /// </remarks>
    public MainViewModel(string? dataDir = null)
    {
        _storage = new ServerStorageService(dataDir);
        _settings = new AppSettingsService(dataDir);

        Load();
        _appSettings = _settings.Load();
        Tunnels = new TunnelsViewModel(Servers, _appSettings, _settings, () => Owner, ConfigureServerAsync);
        Settings = new SettingsViewModel(this, _appSettings, _settings, ApplyConsoleColours, ApplyWindowBehavior);

        // Make the saved notification preferences the app-wide defaults for this session.
        NotificationPreferences.Global = _appSettings.Notifications;
        PlayerHistoryPreferences.Current = _appSettings.PlayerHistory.Clamped();
        ApplyConsoleColours();
        ApplyWindowBehavior();

        var saved = _appSettings.Language;
        var code = !string.IsNullOrWhiteSpace(saved) ? saved : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        _languageReady = true;

        // Checking only at startup missed the case this app is designed for: it lives in the tray
        // with the servers running, so on a machine that is never turned off it would simply never
        // look again. Built here, started in Activate.
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
    }

    /// <summary>True once <see cref="Activate"/> has run, so servers added later start watching.</summary>
    private bool _activated;

    /// <summary>
    /// Starts everything that reaches outside the app: the Playit agent, the update check and its
    /// timer, and each server's own watching.
    /// </summary>
    /// <remarks>
    /// Called from <c>MainWindow</c> once the window is up, not from the constructor. The same
    /// split as <see cref="ServerViewModel.Activate"/> and for the same reasons: a constructor goes
    /// back to assembling, the app stops downloading an agent and calling GitHub before anything is
    /// on screen, and this view model becomes reachable from a test at all.
    /// </remarks>
    public void Activate()
    {
        if (_activated) return;
        _activated = true;

        // Make the per-user Playit agent key (if the user already connected) the credential for all
        // Playit API reads/writes this session.
        PlayitApiService.SetAgentKey(_appSettings.PlayitAgentSecretKey);

        // If already connected, run the embedded Playit agent so tunnels forward traffic (downloads
        // it once; nothing for the user to install).
        if (!string.IsNullOrWhiteSpace(_appSettings.PlayitAgentSecretKey))
            _ = PlayitAgentRunner.Shared.StartAsync(_appSettings.PlayitAgentSecretKey);

        foreach (var server in Servers)
            server.Activate();

        // The history of servers no longer in the list, once it is past the retention period.
        var liveIds = Servers.Select(s => s.Config.Id).ToList();
        var days = PlayerHistoryPreferences.Current.RetentionDays;
        _ = Task.Run(() => PlayerHistoryStore.PruneOrphans(liveIds, DateTime.UtcNow, days));

        _ = CheckForUpdatesAsync();
        _ = Tunnels.PrefetchAsync();
        _updateTimer.Start();
    }

    /// <summary>
    /// How often to look for a new version while the app stays open.
    /// </summary>
    /// <remarks>
    /// Four requests a day against GitHub's 60-per-hour unauthenticated limit, for something that
    /// changes every few weeks. Frequent enough that an app left running for a month still finds
    /// out the same day.
    /// </remarks>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly DispatcherTimer _updateTimer;

    /// <summary>The version already announced, so the same one is never announced twice.</summary>
    /// <remarks>
    /// Kept in memory only. Persisting it would mean an update that appeared while the app was
    /// closed goes unannounced on the next launch, and the point of this is to reach people who
    /// leave the app running — for whom in-memory is exactly as good.
    /// </remarks>
    private string? _notifiedVersion;

    partial void OnIsUpdatingChanged(bool value) => UpdateNowCommand.NotifyCanExecuteChanged();

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (!_languageReady || value is null) return;

        if (_appSettings.Language == value.Code) return;

        _appSettings.Language = value.Code;
        _settings.Save(_appSettings);

        _ = AskRestartAsync();
    }

    private async Task AskRestartAsync()
    {
        if (await MessageBox.ConfirmAsync(Localizer.Get("RestartNeeded"), Localizer.Get("Language")))
            await RestartAppAsync();
    }

    /// <summary>
    /// Pushes the saved console colours to the app-wide state, and repaints every open console.
    /// </summary>
    /// <remarks>
    /// Every server, not just the selected one: the colours are app-wide, and a console that only
    /// updated when you happened to be looking at it would leave the others showing the old palette
    /// until something else forced them to redraw.
    /// </remarks>
    private void ApplyConsoleColours()
    {
        ConsolePreferences.ChatColor = _appSettings.ConsoleChatColor;
        ConsolePreferences.PlayersColor = _appSettings.ConsolePlayersColor;

        foreach (var server in Servers)
            server.RefreshConsoleColours();
    }

    /// <summary>Pushes the saved minimize/close preferences to the app-wide state the window reads.</summary>
    private void ApplyWindowBehavior()
    {
        WindowBehavior.MinimizeToTray = _appSettings.MinimizeToTray;
        WindowBehavior.CloseToTray = _appSettings.CloseToTray;
    }

    private async Task RestartAppAsync()
    {
        await ShutdownAllAsync();
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exe))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
            catch { /* if it can't be relaunched, at least exit */ }
        }
        Environment.Exit(0);
    }

    /// <summary>
    /// If the current version differs from the last one seen by the user (i.e. it was just
    /// updated), shows the what's-new window. Saves the seen version so it isn't repeated.
    /// </summary>
    public void ShowWhatsNewIfUpdated(Window owner)
    {
        var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (asmVersion is null) return;
        var current = new Version(asmVersion.Major, asmVersion.Minor, Math.Max(0, asmVersion.Build));
        var version = $"{current.Major}.{current.Minor}.{current.Build}";

        if (_appSettings.LastVersionSeen == version) return; // already seen in this version

        // Show the notes of every version between the last one seen and this one (accumulated),
        // so users who skipped releases still learn what's new in each.
        var lastSeen = ParseSeenVersion(_appSettings.LastVersionSeen);
        var sections = Changelog.NotesSince(lastSeen, current);

        if (sections.Count == 0)
        {
            // Nothing to show for this version: just mark it as seen.
            _appSettings.LastVersionSeen = version;
            _settings.Save(_appSettings);
            return;
        }

        try
        {
            _ = new WhatsNewDialog(version, sections).ShowDialog(owner);
            // Marked as seen only once the dialog is actually up: if creating/showing it threw,
            // the notes are offered again on the next start instead of being lost forever.
            _appSettings.LastVersionSeen = version;
            _settings.Save(_appSettings);
        }
        catch { /* if something fails, don't block startup; the notes stay pending */ }
    }

    /// <summary>Parses a stored "seen version" string (e.g. "1.1.0"). Null on a fresh install.</summary>
    private static Version? ParseSeenVersion(string? seen)
    {
        if (string.IsNullOrWhiteSpace(seen) || !Version.TryParse(seen, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>
    /// Asks GitHub whether a newer version exists. <paramref name="manual"/> is a person pressing the
    /// button in About, who is owed an answer either way; the startup and six-hourly checks stay
    /// silent unless there is news, as before.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (IsCheckingUpdates) return;
        if (manual) UpdateCheckState = UpdateCheckState.Checking;

        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        var (state, info) = await UpdateCheck.RunAsync(
            () => new UpdateService().CheckAsync(current, _appSettings.ReceiveBetas));

        if (state != UpdateCheckState.Failed || manual)
        {
            LastUpdateCheck = DateTime.Now;
            UpdateCheckState = state;
        }
        else if (UpdateCheckState == UpdateCheckState.Checking)
        {
            UpdateCheckState = UpdateCheckState.Unknown;
        }

        // A clear "nothing newer" takes back whatever was being offered: a beta, after betas were
        // switched off, would otherwise stay in the banner until the app was restarted. A failed
        // check says nothing about it either way, so it leaves the offer alone.
        if (state == UpdateCheckState.UpToDate && UpdateAvailable)
        {
            UpdateAvailable = false;
            _packageUrl = _packageName = _checksumUrl = null;
        }

        if (info is null) return;

        _releaseUrl = info.Url;
        _packageUrl = info.PackageUrl;
        _packageName = info.PackageName;
        _checksumUrl = info.ChecksumUrl;
        // A beta says so before the button, not after installing.
        UpdateText = string.Format(
            Localizer.Get(info.IsPreRelease ? "Msg_UpdateBetaAvailableFmt" : "Msg_UpdateAvailableFmt"),
            info.Version);
        UpdateAvailable = true;
        OnPropertyChanged(nameof(UpdateCheckText));
        NotifyUpdateOnce(info.Version, info.IsPreRelease);
    }

    /// <summary>The "Check for updates" button in About.</summary>
    [RelayCommand]
    private Task CheckForUpdatesNow() => CheckForUpdatesAsync(manual: true);

    /// <summary>The betas switch in Settings moved: ask again, so what is offered follows it.</summary>
    /// <remarks>Not before <see cref="Activate"/>: nothing here calls GitHub until the app is up.</remarks>
    internal void OnReceiveBetasChanged()
    {
        if (_activated) _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void OpenLink(string? url) => BrowserLauncher.Open(url);

    /// <summary>
    /// Raises a desktop notification the first time a given version is seen, and only while the
    /// window is out of sight — the banner already says it when the window is there to be read.
    /// </summary>
    private void NotifyUpdateOnce(string version, bool isBeta)
    {
        if (_notifiedVersion == version) return;
        _notifiedVersion = version;

        if (!ToastService.MainWindowInactive) return;

        // The global master switch silences this like everything else: someone who turned
        // notifications off does not want the launcher tapping them on the shoulder either.
        if (!NotificationPreferences.Global.Enabled) return;

        // Updating restarts the app, which stops every server with it. Saying so is the difference
        // between an informed choice and pulling the rug out from under whoever is playing.
        var key = isBeta
            ? (AnyServerRunning ? "Notif_UpdateBetaWhileRunningFmt" : "Notif_UpdateBetaFmt")
            : (AnyServerRunning ? "Notif_UpdateWhileRunningFmt" : "Notif_UpdateFmt");

        var message = string.Format(Localizer.Get(key), version);

        // The one notification with no NotificationKind behind it: it belongs to the app, not to a
        // server, so there is no kind to look up and no per-server override to consult. Info and a
        // download arrow, said here rather than left to the default, so adding a level later cannot
        // silently move it.
        ToastService.Shared.Notify(Localizer.Get("Notif_UpdateTitle"), message,
            NotificationLevel.Info, "\U0001F4E5", NotificationPreferences.Global);
    }

    private bool CanUpdateNow => !IsUpdating;

    /// <summary>
    /// Downloads the new version's installer and runs it to update the app without going through
    /// GitHub. If the release has no installer, opens the page as a fallback.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNow()
    {
        // Every platform updates itself now; only the mechanism differs (see SelfUpdater). The
        // release page stays as the fallback for when this release ships nothing for us, or when
        // this particular install can't be replaced in place — an AppImage under /opt, say.
        if (string.IsNullOrEmpty(_packageUrl) || !SelfUpdater.CanUpdateInPlace)
        {
            var blocker = string.IsNullOrEmpty(_packageUrl) ? null : SelfUpdater.Blocker;
            if (!string.IsNullOrEmpty(blocker))
                await MessageBox.ShowAsync(blocker, Localizer.Get("Update_Now"), Owner);
            OpenRelease();
            return;
        }

        IsUpdating = true;
        UpdateText = Localizer.Get("Update_Downloading");
        try
        {
            // Random per-run folder: fixed names in %TEMP% could be pre-planted/replaced by
            // another local process between download and execution.
            var updateDir = Path.Combine(Path.GetTempPath(), "mcsl-" + Path.GetRandomFileName());
            var dest = Path.Combine(updateDir, SelfUpdater.PackageFileName(_packageName));
            var updateService = new UpdateService();

            // The package is about to become the app itself, so its checksum is REQUIRED, not
            // best-effort. A missing or unreadable one means a broken (or tampered) release:
            // refuse and send the user to the release page rather than install an unverified
            // Resolved before the download so a refusal doesn't cost the ~35 MB transfer.
            var expectedSha256 = string.IsNullOrEmpty(_checksumUrl) || string.IsNullOrEmpty(_packageName)
                ? null
                : await updateService.GetExpectedSha256Async(_checksumUrl, _packageName);
            if (string.IsNullOrEmpty(expectedSha256))
                throw new InvalidOperationException(Localizer.Get("Msg_UpdateNoChecksum"));

            await updateService.DownloadInstallerAsync(_packageUrl, dest);

            UpdateText = Localizer.Get("Msg_VerifyingChecksum");
            await DownloadVerifier.VerifyAsync(dest, expectedSha256, HashAlgorithmName.SHA256);

            UpdateText = Localizer.Get("Update_Installing");
            await ShutdownAllAsync();

            // From here the platform decides: run the silent installer, swap the AppImage, or
            // hand the .dmg to a script that replaces the bundle once we are gone.
            SelfUpdater.Apply(dest);
            Environment.Exit(0);
        }
        catch (InvalidOperationException ex)
        {
            // Security-relevant refusals land here: either DownloadVerifier's mismatch (the
            // downloaded installer doesn't match the release's checksum) or the release publishing
            // no usable SHA256SUMS.txt at all. Tell the user explicitly instead of silently
            // falling back to the browser.
            IsUpdating = false;
            UpdateText = string.Empty;
            await MessageBox.ShowAsync(ex.Message, Localizer.Get("Update_Now"), Owner);
            OpenRelease();
        }
        catch
        {
            // If the download/install fails, let the user open the page manually.
            IsUpdating = false;
            UpdateText = string.Empty;
            OpenRelease();
        }
    }

    // Through BrowserLauncher, not Process.Start directly: _releaseUrl is the html_url the GitHub
    // API returned, i.e. a remote value, and UseShellExecute hands whatever it gets to the shell.
    // The guard that rejects anything but absolute http(s) already exists for exactly this — it was
    // simply not being used here.
    private void OpenRelease() => BrowserLauncher.Open(_releaseUrl);

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;

    private void Load()
    {
        // The app starts with no servers; the user creates a new one or adds an existing folder.
        // For servers saved before Type/GameVersion existed, detect them from the folder so the
        // mods browser works (older Fabric/Forge servers). AddServer does the same on the way in,
        // so a folder registered today does not have to wait for the next start to be recognised.
        var detector = new ServerDetectionService();
        var changed = false;
        foreach (var cfg in _storage.Load())
        {
            if (detector.DetectAndFill(cfg)) changed = true;
            Register(cfg);
        }
        if (changed) Save();

        SelectedServer = Servers.FirstOrDefault();
    }

    private bool _corruptWarned;

    /// <summary>
    /// If servers.json was corrupt at startup, tells the user what happened — recovered from the
    /// ".bak" backup, or started empty with the damaged file kept as ".bad" — instead of silently
    /// showing an empty list. Called from MainWindow.Loaded, which in Avalonia can fire again every
    /// time the window re-attaches to the visual tree (e.g. restoring from the tray), so the
    /// warning is one-shot per session.
    /// </summary>
    public async Task WarnIfServersFileWasCorruptAsync(Window owner)
    {
        var outcome = _storage.LastLoadOutcome;
        if (outcome == AtomicJsonFile.LoadOutcome.Ok || _corruptWarned) return;
        _corruptWarned = true;

        var key = outcome == AtomicJsonFile.LoadOutcome.RecoveredFromBackup
            ? "Msg_ServersRecoveredFmt"
            : "Msg_ServersCorruptFmt";
        await MessageBox.ShowAsync(
            string.Format(Localizer.Get(key), _storage.QuarantinedFilePath),
            Localizer.Get("Title_ServersDamaged"), owner);
    }

    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        // Picking a server while the new-server panel is open goes to that server. The unfinished
        // one is kept (see NewServerPanel), not thrown away.
        if (value is not null && IsNewServerOpen) IsNewServerOpen = false;

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowServerDetail));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>
    /// Returns the Playit credential used for tunnel management (the per-user agent secret key from
    /// the setup-code flow, or a legacy write key for users who already had one). If none is stored,
    /// runs the setup-code flow. Returns null if the user cancels or the flow is unavailable.
    /// </summary>
    private async Task<string?> EnsurePlayitAgentAsync()
    {
        if (Owner is null) return null;
        // Returns the stored connection if there is one, or runs the "Connect to Playit" flow (paste
        // a setup code) right here — so clicking "Create tunnel" while not connected just works.
        return await PlayitConnection.EnsureAsync(Owner, _appSettings, _settings);
    }

    /// <summary>The Bedrock ports every server except <paramref name="except"/> already holds.</summary>
    /// <remarks>
    /// Read live rather than captured: servers are added and removed while the app runs, and a list
    /// taken when the view model was built would go stale the first time either happens.
    /// </remarks>
    private IEnumerable<int> BedrockPortsOf(ServerViewModel except) =>
        CrossplayService.PortsHeldBy(Servers.Select(s => s.Config), except.Config);

    /// <summary>Creates a server's ViewModel, adds it to the list and persists its changes.</summary>
    private ServerViewModel Register(ServerConfig config)
    {
        var vm = new ServerViewModel(config);
        vm.ConfigChanged += Save;
        vm.BedrockPortsInUse = () => BedrockPortsOf(vm);
        Servers.Add(vm);
        // A server registered while the app is already running has nobody else to switch it on. The
        // ones Load builds at startup wait for Activate, which reaches all of them at once.
        if (_activated) vm.Activate();
        return vm;
    }

    /// <summary>Opens the new-server panel: create one from scratch, or add one that already exists.</summary>
    /// <remarks>
    /// <para>
    /// One panel for both, in the window, where two buttons used to open two dialogs. An unfinished
    /// one is kept: pressing the button again brings it back rather than throwing it away.
    /// </para>
    /// <para>
    /// The list loses its selection while the panel is open. It used to keep the previous server
    /// highlighted, as if that server were the one on screen, and picking it again did nothing
    /// visible because it already counted as picked.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private void ShowNewServer()
    {
        Section = AppSection.Servers;
        if (IsCreatingServer) return;

        NewServerPanel ??= CreateNewServerPanel();
        _beforeNewServer = SelectedServer;
        IsNewServerOpen = true;
        SelectedServer = null;
    }

    private NewServerView CreateNewServerPanel()
    {
        var propertiesService = new ServerPropertiesService();
        var usedPorts = Servers
            .Select(s => propertiesService.GetServerPort(s.Config.PropertiesPath))
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        var panel = new NewServerView(usedPorts, Servers.Select(s => s.Config.FolderPath));
        panel.Cancelled += () => CloseNewServer(select: _beforeNewServer);
        panel.Completed += result =>
        {
            // Finished while on screen: show it. Finished while put away (a download left running
            // while looking at another server): it joins the list without taking the screen.
            var wasOnScreen = IsCreatingServer;
            NewServerPanel = null;
            IsNewServerOpen = false;
            _ = FinishNewServerAsync(result, select: wasOnScreen || SelectedServer is null);
        };
        return panel;
    }

    /// <summary>Throws the panel away and gives the detail area back to a server.</summary>
    private void CloseNewServer(ServerViewModel? select)
    {
        var wasOnScreen = IsCreatingServer;
        NewServerPanel = null;
        IsNewServerOpen = false;
        if (wasOnScreen && SelectedServer is null)
            SelectedServer = select is not null && Servers.Contains(select) ? select : Servers.FirstOrDefault();
    }
    /// <summary>Registers what the panel produced and does what was asked for it.</summary>
    private async Task FinishNewServerAsync(NewServerResult result, bool select)
    {
        var vm = Register(result.Config);
        if (select) SelectedServer = vm;
        Save();

        // The same steps for a folder that was taken over as for a server just made: the options
        // the panel showed apply to both.
        // Create the Playit tunnel (errors are visible in the server's console).
        string? playitKey = null;
        if (result.CreateTunnel)
        {
            playitKey = await EnsurePlayitAgentAsync();
            if (playitKey is not null)
                await vm.CreateTunnelAsync(playitKey);
        }

        // Crossplay after the Java tunnel, not before: setting it up needs the Playit key that
        // step obtains, and the Bedrock tunnel is a second one alongside the Java one.
        if (result.Config.CrossplayEnabled)
        {
            await vm.SetUpCrossplayAsync(playitKey);
            Save();
        }

        if (result.Config.MultiVersionEnabled)
        {
            await vm.SetUpMultiVersionAsync();
            Save();
        }

        if (result.Config.BedrockModContentEnabled)
        {
            await vm.SetUpBedrockModContentAsync();
            Save();
        }

        // First launch to generate the world and files.
        if (result.AutoStart)
            vm.StartCommand.Execute(null);
    }

    /// <summary>Whether a row's own button, or the selection, names a server to act on.</summary>
    private bool CanActOn(ServerViewModel? target) => target is not null || HasSelection;

    /// <summary>A row's button acts on its row: it selects it first, so what happens is on screen.</summary>
    private ServerViewModel? Target(ServerViewModel? target)
    {
        if (target is not null && !ReferenceEquals(target, SelectedServer)) SelectedServer = target;
        return SelectedServer;
    }

    [RelayCommand(CanExecute = nameof(CanActOn))]
    private async Task EditServer(ServerViewModel? target)
    {
        if (Target(target) is not { } server || Owner is null) return;
        var oldName = server.Name;

        // Read before the dialog: these two checkboxes are requests to install something, not
        // settings that take effect by being remembered. Turning one on and having nothing happen
        // is worse than not offering it, because the app then claims a server can do something it
        // cannot.
        var hadCrossplay = server.Config.CrossplayEnabled;
        var hadMultiVersion = server.Config.MultiVersionEnabled;
        var hadModContent = server.Config.BedrockModContentEnabled;

        var dialog = new AddEditServerDialog(server.Config);
        var accepted = await dialog.ShowDialog<bool>(Owner);

        // A loader install mutates the config and the disk in the act (files already downloaded),
        // so it must be persisted even if the user then cancels the edit dialog — otherwise
        // servers.json keeps naming the old type while the disk is already Fabric/Forge/Paper.
        // Cancel still reverts the ordinary editable fields.
        //
        // Nothing is refreshed here. The dialog writes into the config the view model is showing,
        // the config announces each change and ServerConfigEffects says what it costs, so the card
        // and the panels have already followed — including on the Cancel path, where restoring the
        // snapshot announces its own eighteen assignments. A blanket refresh at this point used to
        // be the mechanism; leaving it in would mean the app never exercised the one that replaced
        // it, and would throw away the store page the user had open for an edit they cancelled.
        if (accepted || dialog.LoaderInstalled)
        {
            Save();
            _ = Tunnels.RenameTunnelsForServerAsync(server, oldName);

            if (!hadCrossplay && server.Config.CrossplayEnabled)
            {
                var key = server.Config.PlayitEnabled ? await EnsurePlayitAgentAsync() : null;
                await server.SetUpCrossplayAsync(key);
                Save();
            }

            if (!hadMultiVersion && server.Config.MultiVersionEnabled)
            {
                await server.SetUpMultiVersionAsync();
                Save();
            }

            if (!hadModContent && server.Config.BedrockModContentEnabled)
            {
                await server.SetUpBedrockModContentAsync();
                Save();
            }
        }
    }

    /// <summary>Opens the editor for the card: icon, name and the two lines of the MOTD.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAppearance()
    {
        if (SelectedServer is null || Owner is null) return;
        var server = SelectedServer;
        var oldName = server.Name;

        var dialog = new ServerAppearanceDialog(server.Config, server.IsRunning);
        if (!await dialog.ShowDialog<bool>(Owner)) return;

        // The dialog wrote the icon and the MOTD to disk and set the name on the config; what is
        // left is what the config alone cannot do: tell the view model, persist, re-read the disk.
        server.Name = server.Config.Name;
        Save();
        server.RefreshFromDisk();
        _ = Tunnels.RenameTunnelsForServerAsync(server, oldName);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ConfigureServer() => SelectedServer is null ? Task.CompletedTask : ConfigureServerAsync(SelectedServer);

    /// <summary>Opens the properties editor for any server; the tunnels screen uses it to change a port.</summary>
    private async Task ConfigureServerAsync(ServerViewModel server)
    {
        if (Owner is null) return;
        var dialog = new ServerConfigDialog(server.Config, port =>
            Servers.FirstOrDefault(s => !ReferenceEquals(s, server) &&
                                        CrossplayService.EffectiveBedrockPort(s.Config) == port)?.Name);
        var accepted = await dialog.ShowDialog<bool>(Owner);
        if (accepted)
        {
            server.RefreshFromDisk();
            if (dialog.BedrockPortChanged)
            {
                Save();
                _ = server.RefreshTunnelInfoAsync();
            }
        }

        // Whether it was accepted or cancelled: forgetting a server's players happens the moment
        // the button is pressed, not when the dialog is saved, so the Players tab would otherwise
        // go on listing people whose history is no longer there.
        if (dialog.HistoryCleared)
            server.History.Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanActOn))]
    private async Task RemoveServer(ServerViewModel? target)
    {
        // Captured once, and only this from here on. Stopping a running server takes up to fifteen
        // seconds with the window still usable, and reading SelectedServer again after that removed
        // whichever server had been clicked in the meantime — taking it out of servers.json and
        // leaving it running with nobody watching it.
        if (Target(target) is not { } server) return;

        var folder = server.Config.FolderPath;
        // Read the ports BEFORE deleting anything (we need them to locate the tunnels).
        var port = new ServerPropertiesService().GetServerPort(server.Config.PropertiesPath);

        // A crossplay server has two: the Java one and the Bedrock one. Forgetting the second
        // leaves an orphan tunnel on the account that nothing will ever clean up.
        var bedrockPort = CrossplayService.EffectiveBedrockPort(server.Config);

        if (Owner is null) return;
        var dialog = new DeleteServerDialog(server.Name, folder);
        if (!await dialog.ShowDialog<bool>(Owner))
            return;

        await ForgetServerAsync(server);

        // Not "&& port.HasValue". The Java port comes from server.properties, which can be
        // unreadable or already gone, and hanging the whole block on it took the Bedrock tunnel
        // down with it — even though that one is identified by Config.BedrockPort, which lives in
        // servers.json and needs no file on disk. The result was an orphan UDP tunnel that nothing
        // would ever clean up, sitting on a port the next server would be handed as free.
        if (dialog.DeleteTunnel)
        {
            var key = await EnsurePlayitAgentAsync();
            try
            {
                var api = new PlayitApiService();
                // Java is TCP, Bedrock is UDP. Naming the protocol is what keeps this from
                // deleting somebody else's tunnel that happens to share the port number.
                bool? javaDeleted = null;
                if (key is not null && port.HasValue)
                    javaDeleted = await api.DeleteTunnelForPortAsync(key, port.Value, udp: false);

                if (key is not null && PlayitApiService.ShouldDeleteBedrockTunnel(dialog.DeleteTunnel, bedrockPort))
                    await api.DeleteTunnelForPortAsync(key, bedrockPort!.Value, udp: true);

                if (key is null)
                {
                    // The user didn't provide a key; the tunnel is not deleted.
                }
                else if (!port.HasValue)
                    await MessageBox.ShowAsync(
                        Localizer.Get("Msg_JavaTunnelPortUnknown"),
                        Localizer.Get("Title_DeleteTunnel"));
                else if (javaDeleted == false)
                    await MessageBox.ShowAsync(
                        string.Format(Localizer.Get("Msg_NoTunnelForPort"), port),
                        Localizer.Get("Title_DeleteTunnel"));
            }
            catch (Exception ex)
            {
                await MessageBox.ShowAsync(
                    string.Format(Localizer.Get("Msg_TunnelDeleteError"), ex.Message),
                    Localizer.Get("Title_DeleteTunnel"));
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
                await MessageBox.ShowAsync(
                    string.Format(Localizer.Get("Msg_FilesDeleteError"), ex.Message),
                    Localizer.Get("Title_DeleteFiles"));
            }
        }
    }

    /// <summary>Stops <paramref name="server"/>, takes it off the list and saves the list.</summary>
    /// <remarks>
    /// The selection only moves if it was on the server being removed: whatever the user picked
    /// while this one was stopping is theirs to keep looking at.
    /// </remarks>
    internal async Task ForgetServerAsync(ServerViewModel server)
    {
        await server.ShutdownAsync();
        Servers.Remove(server);
        if (SelectedServer is null || ReferenceEquals(SelectedServer, server))
            SelectedServer = Servers.FirstOrDefault();
        Save();
    }

    [RelayCommand]
    private void Save() => _storage.Save(Servers.Select(s => s.Config));

    /// <summary>True if any server is running (to warn on close).</summary>
    public bool AnyServerRunning => Servers.Any(s => s.IsRunning);

    /// <summary>Stops all servers IN PARALLEL and saves when the app closes.</summary>
    public async Task ShutdownAllAsync()
    {
        PlayitAgentRunner.Shared.Stop(); // stop the embedded Playit agent along with the servers
        await Task.WhenAll(Servers.Select(s => s.ShutdownAsync()));
        Save();
        // The console log buffers and flushes on a timer (EFI-5); push the tail out before the
        // Environment.Exit that follows every shutdown path.
        ConsoleLogService.Shared.Flush();
        PlayerHistoryStore.FlushAll();
    }

    partial void OnSelectedServerChanged(ServerViewModel? oldValue, ServerViewModel? newValue)
    {
        EditServerCommand.NotifyCanExecuteChanged();
        RemoveServerCommand.NotifyCanExecuteChanged();
        ConfigureServerCommand.NotifyCanExecuteChanged();
        EditAppearanceCommand.NotifyCanExecuteChanged();
    }
}
