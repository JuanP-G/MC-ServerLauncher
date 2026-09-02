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
    private readonly ServerStorageService _storage = new();
    private readonly AppSettingsService _settings = new();

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

    /// <summary>Which part of the app the rail is showing.</summary>
    [ObservableProperty]
    private AppSection _section = AppSection.Servers;

    /// <summary>
    /// One flag per section, because a view cannot compare an enum without a converter and adding
    /// one for two values would be more machinery than it saves.
    /// </summary>
    public bool IsServersSection => Section == AppSection.Servers;

    /// <inheritdoc cref="IsServersSection" />
    public bool IsTunnelsSection => Section == AppSection.Tunnels;

    /// <summary>
    /// Lo mismo, pero como opacidad, para que el cambio de seccion se pueda FUNDIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Con <c>IsVisible</c> no hay nada que fundir: false saca el elemento del layout, asi que
    /// desaparece de golpe antes de que ninguna transicion llegue a empezar. Las dos secciones se
    /// quedan montadas y lo que cambia es la opacidad.
    /// </para>
    /// <para>
    /// Y por eso existe tambien <see cref="IsServersSection"/> atado a <c>IsEnabled</c> en la vista:
    /// un panel a opacidad cero <b>sigue recibiendo clics y foco de tabulador</b>. Invisible y
    /// pulsable es peor que visible, porque el clic va a algo que no se ve. No se usa
    /// <see cref="ViewModels.BoolOpacityConverter"/> porque ese apaga a 0.45 —esta para atenuar un
    /// mod desactivado— y aqui hace falta llegar a cero.
    /// </para>
    /// </remarks>
    public double ServersOpacity => IsServersSection ? 1.0 : 0.0;

    /// <inheritdoc cref="ServersOpacity" />
    public double TunnelsOpacity => IsTunnelsSection ? 1.0 : 0.0;

    partial void OnSectionChanged(AppSection value)
    {
        OnPropertyChanged(nameof(IsServersSection));
        OnPropertyChanged(nameof(IsTunnelsSection));
        OnPropertyChanged(nameof(ServersOpacity));
        OnPropertyChanged(nameof(TunnelsOpacity));
    }

    [RelayCommand]
    private void ShowServers() => Section = AppSection.Servers;

    [RelayCommand]
    private void ShowTunnels()
    {
        Section = AppSection.Tunnels;
        // Asked for when the section is opened, not on a timer and not at startup: it is a network
        // request to somebody else's API, and nobody is looking at the answer until now.
        _ = RefreshTunnels();
    }

    // --- The tunnels table ---

    /// <summary>Every tunnel on the account, with who owns it and what is wrong with it.</summary>
    public ObservableCollection<TunnelRowViewModel> TunnelRows { get; } = new();

    [ObservableProperty]
    private bool _tunnelsLoading;

    /// <summary>Null until the account has answered once; then how many rows are flagged.</summary>
    [ObservableProperty]
    private int _tunnelsAttention;

    [ObservableProperty]
    private bool _tunnelsAsked;

    /// <summary>What went wrong asking the account, or null. Shown in the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTunnelsError))]
    private string? _tunnelsError;

    public bool HasTunnelsError => !string.IsNullOrEmpty(TunnelsError);

    /// <summary>Whether there is a Playit account to ask in the first place.</summary>
    public bool PlayitConnected => PlayitConnection.IsConnected(_appSettings);

    /// <summary>What the summary line says: either all is well or how many need looking at.</summary>
    public string TunnelsSummary => TunnelsAttention == 0
        ? Localizer.Get("Tun_AllGood")
        : string.Format(Localizer.Get("Tun_AttentionFmt"), TunnelsAttention);

    /// <summary>Nothing needs doing. A bool, not the count, because the view binds visibility.</summary>
    /// <remarks>
    /// The panel used to say <c>IsVisible="{Binding !TunnelsAttention}"</c>, negating an int. These
    /// views are <c>x:CompileBindings="False"</c>, so that does not fail — it just quietly decides
    /// something, and a green dot that is always on says "all good" while three rows underneath say
    /// otherwise. Cheaper to expose the bool than to trust the coercion.
    /// </remarks>
    public bool TunnelsAllGood => TunnelsAttention == 0;

    partial void OnTunnelsAttentionChanged(int value)
    {
        OnPropertyChanged(nameof(TunnelsSummary));
        OnPropertyChanged(nameof(TunnelsAllGood));
        OnPropertyChanged(nameof(TunnelsNeedAttention));
    }

    /// <inheritdoc cref="TunnelsAllGood" />
    public bool TunnelsNeedAttention => TunnelsAttention > 0;

    /// <summary>
    /// "You have no tunnels" — only once the account has actually been asked and answered.
    /// </summary>
    /// <remarks>
    /// The three conditions are one property rather than a MultiBinding in the view because getting
    /// it wrong is invisible: the views are <c>x:CompileBindings="False"</c>, so a converter that
    /// does not resolve draws nothing and the message silently never appears. Here it is a bool
    /// that can be read, and reasoned about, in one place.
    /// </remarks>
    public bool ShowNoTunnels => PlayitConnected && TunnelsAsked && TunnelRows.Count == 0;

    private void RefreshTunnelsEmptyState()
    {
        OnPropertyChanged(nameof(ShowNoTunnels));
        OnPropertyChanged(nameof(HasTunnelRows));
    }

    public bool HasTunnelRows => TunnelRows.Count > 0;

    partial void OnTunnelsAskedChanged(bool value) => RefreshTunnelsEmptyState();

    /// <summary>The playit dashboard for the account, not for one server's tunnel.</summary>
    [RelayCommand]
    private void OpenPlayitAccount() => BrowserLauncher.Open("https://playit.gg/account/tunnels");

    /// <summary>Puts a row's public address on the clipboard.</summary>
    [RelayCommand]
    private async Task CopyTunnelRow(TunnelRowViewModel? row)
    {
        if (row is null || Owner?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(row.Address); } catch { /* clipboard busy */ }
    }

    /// <summary>
    /// Deletes one tunnel from the account, after asking.
    /// </summary>
    /// <remarks>
    /// Asking is not ceremony here: the rows that most want deleting are the orphans, and an orphan
    /// looks exactly like a tunnel whose server this app has simply not been told about — a server
    /// added on another machine, or one removed from the list but not from disk. The confirmation
    /// says the one thing that settles it: no server is touched either way.
    /// </remarks>
    [RelayCommand]
    private async Task DeleteTunnelRow(TunnelRowViewModel? row)
    {
        if (row is null || Owner is null) return;

        var ok = await MessageBox.ConfirmAsync(
            string.Format(Localizer.Get("Tun_DeleteFmt"), row.Address),
            Localizer.Get("Nav_Tunnels"), Owner);
        if (!ok) return;

        try
        {
            var key = new PlayitApiService().ReadSecretKey() ?? _appSettings.PlayitAgentSecretKey;
            if (string.IsNullOrWhiteSpace(key)) return;

            await new PlayitApiService().DeleteTunnelForPortAsync(key, row.LocalPortNumber, row.IsBedrock);
            await RefreshTunnels();
        }
        catch (Exception ex)
        {
            TunnelsError = ex.Message;
        }
    }

    /// <summary>The two ports each server can have a tunnel on.</summary>
    /// <remarks>
    /// The Java port lives in <c>server.properties</c> and not in the config, so it is read here
    /// rather than derived — and read as null when the file cannot be, because "I do not know the
    /// port" must not quietly become "the port is whatever you asked about".
    /// </remarks>
    private IReadOnlyList<TunnelInventory.ServerPorts> ServerPortsSnapshot()
    {
        var properties = new ServerPropertiesService();
        return Servers.Select(s => new TunnelInventory.ServerPorts(
            s.Config.Name,
            SafeJavaPort(properties, s.Config.PropertiesPath),
            s.Config.BedrockPort)).ToList();
    }

    private static int? SafeJavaPort(ServerPropertiesService properties, string path)
    {
        try { return properties.GetServerPort(path); }
        catch { return null; }   // unreadable or gone: not knowing is an answer, and it is null
    }

    [RelayCommand]
    private async Task RefreshTunnels()
    {
        OnPropertyChanged(nameof(PlayitConnected));
        if (!PlayitConnected)
        {
            TunnelRows.Clear();
            RefreshTunnelsEmptyState();
            TunnelsAsked = true;
            return;
        }

        TunnelsLoading = true;
        TunnelsError = null;
        try
        {
            var key = new PlayitApiService().ReadSecretKey() ?? _appSettings.PlayitAgentSecretKey;
            if (string.IsNullOrWhiteSpace(key)) { TunnelRows.Clear(); return; }

            var (_, tunnels) = await new PlayitApiService().GetRunDataAsync(key);
            var rows = TunnelInventory.Build(tunnels, ServerPortsSnapshot());

            TunnelRows.Clear();
            foreach (var row in rows) TunnelRows.Add(new TunnelRowViewModel(row));
            TunnelsAttention = TunnelInventory.AttentionCount(rows);
            RefreshTunnelsEmptyState();
        }
        catch (Exception ex)
        {
            // Said here and not in a server's console: this is the account failing to answer, and
            // no server did anything. Clearing the rows matters too — leaving the previous ones up
            // after a failed request is worse than an empty table, because they would look current.
            TunnelRows.Clear();
            RefreshTunnelsEmptyState();
            TunnelsError = ex.Message;
        }
        finally
        {
            TunnelsLoading = false;
            TunnelsAsked = true;
        }
    }

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateText = string.Empty;

    [ObservableProperty]
    private bool _isUpdating;

    private string? _releaseUrl;
    private string? _packageUrl;
    private string? _packageName;
    private string? _checksumUrl;

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

    public MainViewModel()
    {
        Load();
        _appSettings = _settings.Load();

        // Make the per-user Playit agent key (if the user already connected) the credential for all
        // Playit API reads/writes this session.
        PlayitApiService.SetAgentKey(_appSettings.PlayitAgentSecretKey);

        // If already connected, run the embedded Playit agent so tunnels forward traffic (downloads
        // it once; nothing for the user to install).
        if (!string.IsNullOrWhiteSpace(_appSettings.PlayitAgentSecretKey))
            _ = PlayitAgentRunner.Shared.StartAsync(_appSettings.PlayitAgentSecretKey);

        // Make the saved notification preferences the app-wide defaults for this session.
        NotificationPreferences.Global = _appSettings.Notifications;
        ApplyConsoleColours();
        ApplyWindowBehavior();
        // En caliente y sin reiniciar: quitar la capa de estilos apaga las animaciones de todo lo
        // que ya esta en pantalla, porque no habia ningun "if" repartido que actualizar.
        if (Application.Current is { } app) MotionSwitch.Apply(app.Styles, _appSettings.Animations);

        var saved = _appSettings.Language;
        var code = !string.IsNullOrWhiteSpace(saved) ? saved : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        _languageReady = true;

        _ = CheckForUpdatesAsync();

        // Checking only at startup missed the case this app is designed for: it lives in the tray
        // with the servers running, so on a machine that is never turned off it would simply never
        // look again.
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
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

    /// <summary>Opens the app settings (language, notifications, …).</summary>
    /// <summary>Version, repositorio, reportar un fallo y los avisos legales.</summary>
    [RelayCommand]
    private async Task OpenAbout()
    {
        if (Owner is null) return;
        await new AboutDialog(UpdateAvailable).ShowDialog(Owner);
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Owner is null) return;
        var dialog = new SettingsDialog(Languages, SelectedLanguage, _appSettings.Notifications, _appSettings, _settings);
        if (!await dialog.ShowDialog<bool>(Owner)) return;

        // Notifications and window behavior: apply + persist immediately (no restart needed).
        _appSettings.Notifications = dialog.Notifications;
        NotificationPreferences.Global = _appSettings.Notifications;
        _appSettings.MinimizeToTray = dialog.MinimizeToTray;
        _appSettings.CloseToTray = dialog.CloseToTray;
        _appSettings.Animations = dialog.Animations;
        _appSettings.ConsoleChatColor = dialog.ConsoleChatColor;
        _appSettings.ConsolePlayersColor = dialog.ConsolePlayersColor;
        ApplyConsoleColours();
        ApplyWindowBehavior();
        _settings.Save(_appSettings);

        // Language: assigning SelectedLanguage reuses the existing handler (persist + restart prompt).
        if (dialog.SelectedLanguage is { } lang && lang.Code != _appSettings.Language)
            SelectedLanguage = lang;
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

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            var info = await new UpdateService().CheckAsync(current);
            if (info is not null)
            {
                _releaseUrl = info.Url;
                _packageUrl = info.PackageUrl;
                _packageName = info.PackageName;
                _checksumUrl = info.ChecksumUrl;
                // A beta says so before the button, not after installing.
                UpdateText = string.Format(
                    Localizer.Get(info.IsPreRelease ? "Msg_UpdateBetaAvailableFmt" : "Msg_UpdateAvailableFmt"),
                    info.Version);
                UpdateAvailable = true;
                NotifyUpdateOnce(info.Version, info.IsPreRelease);
            }
        }
        catch
        {
            // No connection or GitHub unavailable: it's fine.
        }
    }

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
        // mods browser works (older Fabric/Forge servers).
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

    partial void OnSelectedServerChanged(ServerViewModel? value) => OnPropertyChanged(nameof(HasSelection));

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

    /// <summary>Creates the Playit tunnel for the selected server (the "Create tunnel" button).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CreateTunnelForSelected()
    {
        if (SelectedServer is null) return;
        var key = await EnsurePlayitAgentAsync();
        if (key is null) return;
        await SelectedServer.CreateTunnelAsync(key);
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
        return vm;
    }

    [RelayCommand]
    private async Task AddServer()
    {
        if (Owner is null) return;
        var config = new ServerConfig();
        var dialog = new AddEditServerDialog(config);
        if (await dialog.ShowDialog<bool>(Owner))
        {
            SelectedServer = Register(config);
            Save();
        }
    }

    [RelayCommand]
    private async Task CreateServer()
    {
        var propertiesService = new ServerPropertiesService();
        var usedPorts = Servers
            .Select(s => propertiesService.GetServerPort(s.Config.PropertiesPath))
            .Where(p => p.HasValue)
            .Select(p => p!.Value);

        if (Owner is null) return;
        var dialog = new CreateServerDialog(usedPorts);
        if (await dialog.ShowDialog<bool>(Owner) && dialog.ResultConfig is not null)
        {
            var vm = Register(dialog.ResultConfig);
            SelectedServer = vm;
            Save();

            // Create the Playit tunnel (errors are visible in the server's console).
            string? playitKey = null;
            if (dialog.CreateTunnel)
            {
                playitKey = await EnsurePlayitAgentAsync();
                if (playitKey is not null)
                    await vm.CreateTunnelAsync(playitKey);
            }

            // Crossplay after the Java tunnel, not before: setting it up needs the Playit key that
            // step obtains, and the Bedrock tunnel is a second one alongside the Java one.
            if (dialog.ResultConfig.CrossplayEnabled)
            {
                await vm.SetUpCrossplayAsync(playitKey);
                Save();
            }

            if (dialog.ResultConfig.MultiVersionEnabled)
            {
                await vm.SetUpMultiVersionAsync();
                Save();
            }

            if (dialog.ResultConfig.BedrockModContentEnabled)
            {
                await vm.SetUpBedrockModContentAsync();
                Save();
            }

            // First launch to generate the world and files.
            if (dialog.AutoStart)
                vm.StartCommand.Execute(null);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditServer()
    {
        if (SelectedServer is null || Owner is null) return;
        var server = SelectedServer;
        var oldType = server.Config.Type;

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
        // so it must be persisted even if the user then cancels the edit dialog — otherwise the
        // type badge, the Mods tab and servers.json keep showing the old type while the disk is
        // already Fabric/Forge/Paper. Cancel still reverts the ordinary editable fields.
        if (accepted || dialog.LoaderInstalled)
        {
            server.Name = server.Config.Name;
            Save();

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

            // If the loader type changed (e.g. a vanilla server was converted to Fabric), rebuild the
            // view model so computed state (IsModded, the Mods tab/browser) refreshes.
            if (server.Config.Type != oldType && !server.IsRunning)
                ReplaceServer(server);
        }
    }

    /// <summary>Replaces a server's view model in place (keeping its position) and reselects it.</summary>
    private void ReplaceServer(ServerViewModel old)
    {
        var index = Servers.IndexOf(old);
        if (index < 0) return;

        _ = old.ShutdownAsync(); // stop its timers (it isn't running)
        var vm = new ServerViewModel(old.Config);
        vm.ConfigChanged += Save;
        vm.BedrockPortsInUse = () => BedrockPortsOf(vm);
        Servers[index] = vm;
        SelectedServer = vm;
    }

    /// <summary>Opens the selected server's folder in the system file manager.</summary>
    /// <remarks>
    /// Un boton y no una pestaña de archivos: lo que se pide al querer "los archivos" del servidor es
    /// llegar a la carpeta, y para eso ya existe el explorador del sistema. Un navegador de ficheros
    /// dentro de la app seria una funcion entera que mantener para hacer peor lo que el sistema ya
    /// hace bien.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenServerFolder() => FolderLauncher.Open(SelectedServer?.Config.FolderPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ConfigureServer()
    {
        if (SelectedServer is null || Owner is null) return;
        var dialog = new ServerConfigDialog(SelectedServer.Config);
        if (await dialog.ShowDialog<bool>(Owner))
            SelectedServer.RefreshFromDisk();
    }

    /// <summary>
    /// Opens the sign editor over the selected server: name, icon and MOTD — the three things the
    /// preview card shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives here and not in "Editar" because the way in is now the pencil on the card itself.
    /// Editing what the card shows by opening a different dialog and looking for a button in it was
    /// asking people to know where things are kept rather than pointing at the thing they want.
    /// </para>
    /// <para>
    /// <see cref="ServerViewModel.RefreshFromDisk"/> at the end is not tidying up: without it the
    /// card kept showing the old sign and the old icon until the app was restarted, because nothing
    /// re-reads <c>server.properties</c> or <c>server-icon.png</c> on its own. It was the same gap
    /// the configure dialog had already closed for itself, on a path that never got it.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditSign()
    {
        if (SelectedServer is null || Owner is null) return;

        var properties = new ServerPropertiesService();
        var path = SelectedServer.Config.PropertiesPath;
        var current = properties.Read(path).TryGetValue("motd", out var m) ? m : string.Empty;

        var dialog = new MotdEditorDialog(current, SelectedServer.Name, SelectedServer.PlayerCountText,
            SelectedServer.ServerIcon, SelectedServer.Config.FolderPath,
            SelectedServer.Config.WakeOnDemand);

        if (!await dialog.ShowDialog<bool>(Owner)) return;

        properties.Update(path, new Dictionary<string, string> { ["motd"] = dialog.Result });

        if (!string.Equals(dialog.ResultName, SelectedServer.Name, StringComparison.Ordinal))
        {
            SelectedServer.Name = dialog.ResultName;   // escribe en Config.Name
            Save();
        }

        SelectedServer.RefreshFromDisk();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveServer()
    {
        if (SelectedServer is null) return;

        var folder = SelectedServer.Config.FolderPath;
        // Read the ports BEFORE deleting anything (we need them to locate the tunnels).
        var port = new ServerPropertiesService().GetServerPort(SelectedServer.Config.PropertiesPath);

        // A crossplay server has two: the Java one and the Bedrock one. Forgetting the second
        // leaves an orphan tunnel on the account that nothing will ever clean up.
        var bedrockPort = SelectedServer.Config.CrossplayEnabled && SelectedServer.Config.BedrockPort > 0
            ? SelectedServer.Config.BedrockPort
            : (int?)null;

        if (Owner is null) return;
        var dialog = new DeleteServerDialog(SelectedServer.Name, folder);
        if (!await dialog.ShowDialog<bool>(Owner))
            return;

        await SelectedServer.ShutdownAsync();
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.FirstOrDefault();
        Save();

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
    }

    partial void OnSelectedServerChanged(ServerViewModel? oldValue, ServerViewModel? newValue)
    {
        EditServerCommand.NotifyCanExecuteChanged();
        RemoveServerCommand.NotifyCanExecuteChanged();
        CreateTunnelForSelectedCommand.NotifyCanExecuteChanged();
        ConfigureServerCommand.NotifyCanExecuteChanged();
        OpenServerFolderCommand.NotifyCanExecuteChanged();
    }
}
