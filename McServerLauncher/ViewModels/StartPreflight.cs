using System.IO;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>What <see cref="StartPreflight"/> needs from its server.</summary>
/// <param name="Name">The server's display name, for dialogs.</param>
/// <param name="Say">Writes a line in the server's console.</param>
/// <param name="ConfigChanged">Tells the app the config was changed here, so it is saved.</param>
/// <param name="InstallMissing">Installs the named mods and plugins, reporting progress.</param>
public sealed record PreflightHost(
    Func<string> Name,
    Action<string> Say,
    Action ConfigChanged,
    Func<IEnumerable<string>, IProgress<string>, Task> InstallMissing);

/// <summary>
/// Everything that is checked, and offered to fix, before a server's JVM is launched: that the port
/// is free, that the path is one the server accepts, that its mods have what they need, and that
/// the Java is the one its version requires.
/// </summary>
/// <remarks>
/// <para>
/// Each check either lets the start go ahead or says why not in the console, and the ones that ask
/// a question never ask during an unattended start (an auto-restart after a crash, a wake on
/// demand): a modal nobody is there to answer would leave the server down, which is precisely what
/// those features exist to prevent.
/// </para>
/// <para>
/// It was part of <c>ServerViewModel</c>, three hundred lines of it. The view model still decides
/// the order the checks run in, and what to do when one refuses; this class only knows how to
/// perform each.
/// </para>
/// </remarks>
public sealed class StartPreflight
{
    private readonly PreflightHost _host;
    private readonly JavaService _java;
    private readonly PortService _ports;
    private readonly ServerPropertiesService _properties;

    private ServerConfig Config { get; }

    public StartPreflight(ServerConfig config, PreflightHost host, JavaService java, PortService ports,
        ServerPropertiesService properties)
    {
        Config = config;
        _host = host;
        _java = java;
        _ports = ports;
        _properties = properties;
    }

    /// <summary>
    /// Whether the server's port is free to start on, offering to close whatever holds it.
    /// </summary>
    /// <remarks>
    /// Skipped during an unattended auto-restart: nobody would be there to answer the confirmation
    /// dialog, so a busy port is reported and the start is given up.
    /// </remarks>
    public async Task<bool> PortIsFreeAsync(bool isAutoRestart)
    {
        var port = _properties.GetServerPort(Config.PropertiesPath);
        if (!port.HasValue || !_ports.IsPortInUse(port.Value)) return true;

        if (isAutoRestart)
        {
            _host.Say(string.Format(Localizer.Get("Msg_AutoRestartPortBusyFmt"), port.Value));
            return false;
        }
        return await TryFreePortAsync(port.Value);
    }

    /// <summary>
    /// Checks that the configured Java works for this server's version (read from the jar).
    /// If not, installs/uses the correct Java and saves it in the config.
    /// </summary>
    public async Task EnsureCompatibleJavaAsync()
    {
        var required = _java.GetRequiredJavaFromJar(Config.JarFullPath);

        // Modern Forge launches via an @args-file and Config.JarFullPath points to a server.jar
        // that doesn't exist, so the lookup above always comes back null for it — leaving exactly
        // the most fragile servers without the Java safety net. Derive the requirement from the
        // vanilla server jar the Forge installer keeps under libraries/, or (last resort, needs
        // network) from Mojang's manifest via GameVersion.
        if (required is null && !string.IsNullOrWhiteSpace(Config.ForgeArgs))
            required = _java.GetRequiredJavaFromForgeLibraries(Config.FolderPath, Config.GameVersion)
                ?? await GetRequiredJavaFromManifestAsync();

        if (required is null) return; // cannot be determined (old jar): don't block the start

        // Off the UI thread: the first time for each Java it starts a JVM. The answer is remembered,
        // so the start that follows (ServerProcessManager reads it again for the JVM flags) is free.
        var javaPath = Config.JavaPath;
        var current = await Task.Run(() => _java.GetMajorVersion(javaPath));
        if (current > 0 && JavaService.IsCompatible(current, required.Value))
            return;

        _host.Say(current > 0
            ? string.Format(Localizer.Get("Msg_NeedsJavaCurrentPreparing"), required, current)
            : string.Format(Localizer.Get("Msg_NeedsJavaPreparing"), required));
        try
        {
            var path = await _java.EnsureJavaAsync(required.Value, new Progress<string>(_host.Say));
            if (!string.Equals(path, Config.JavaPath, StringComparison.OrdinalIgnoreCase))
            {
                Config.JavaPath = path;
                _host.ConfigChanged();
                _host.Say(string.Format(Localizer.Get("Msg_JavaConfigured"), path));
            }
        }
        catch (Exception ex)
        {
            _host.Say(string.Format(Localizer.Get("Msg_JavaPrepareFailStart"),
                required, ErrorMessages.Describe(ex)));
        }
    }

    /// <summary>
    /// Last-resort Java lookup (used for modern Forge when the local libraries don't yield it):
    /// asks Mojang's manifest for the Java that <see cref="ServerConfig.GameVersion"/> needs — the
    /// same source used when the server was created. Null when offline or the version is unknown;
    /// the caller then simply skips the check rather than blocking the start.
    /// </summary>
    private async Task<int?> GetRequiredJavaFromManifestAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.GameVersion)) return null;
        try
        {
            var versions = new MinecraftVersionService();
            var (_, list) = await versions.GetVersionsAsync();
            var match = list.FirstOrDefault(v =>
                string.Equals(v.Id, Config.GameVersion, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            return (await versions.GetVersionDetailsAsync(match)).JavaMajor;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks that every installed mod and plugin has what it needs, and asks what to do if not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the jars, so it costs a handful of zip opens and no network at all. That is not
    /// only about speed: the store calls that could answer this swallow every error and return
    /// nothing, so a check built on them would report "all good" whenever the connection was down,
    /// on the one screen where being wrong means the server does not come up.
    /// </para>
    /// <para>
    /// Never blocks an automatic start. An auto-restart after a crash and a wake-on-demand both
    /// happen with nobody in front of the app, and a modal waiting for an answer would leave the
    /// server down until somebody came back — which is precisely what those two features exist to
    /// prevent. Same guard the other two pre-flight dialogs use.
    /// </para>
    /// <para>
    /// Returns true when the start should go ahead.
    /// </para>
    /// </remarks>
    public async Task<bool> CheckContentDependenciesAsync(bool isAutoRestart)
    {
        IReadOnlyList<ContentDependencyCheck.Missing> missing;
        try
        {
            missing = ContentDependencyCheck.CheckServer(Config);
        }
        catch (Exception ex)
        {
            // A check that cannot run is not a reason to refuse a start: the server may well be
            // fine, and the loader will say so far better than a guess would. Said out loud, though
            // — a silent skip here is how the old code came to claim nothing was ever missing.
            _host.Say(string.Format(Localizer.Get("Msg_DepsCheckFailedFmt"), ErrorMessages.Describe(ex)));
            return true;
        }

        if (missing.Count == 0) return true;

        var summary = string.Join(", ", missing.Select(m => m.Id));
        _host.Say(string.Format(Localizer.Get("Msg_DepsMissingFmt"), missing.Count, summary));

        if (!ContentDependencyCheck.ShouldAsk(missing.Count, isAutoRestart))
        {
            // Nobody to answer. Start anyway and let the loader report it: refusing here would turn
            // a server that might still work into one that is definitely down.
            _host.Say(Localizer.Get("Msg_DepsMissingAutoStart"));
            return true;
        }

        var dialog = new MissingDependenciesDialog(_host.Name(), missing);
        var choice = await ShowDialogAsync(dialog);

        switch (choice)
        {
            case MissingDependenciesChoice.StartAnyway:
                return true;

            case MissingDependenciesChoice.InstallAndStart:
                await _host.InstallMissing(missing.Select(m => m.Id), new Progress<string>(_host.Say));

                // Checked again rather than assumed: an install can fail, and a store that does not
                // know a name — cristallib is published as cristel-lib — resolves to nothing at all.
                // Starting while still claiming it was handled is the one outcome worth avoiding.
                var left = ContentDependencyCheck.CheckServer(Config);
                if (left.Count > 0)
                    _host.Say(string.Format(Localizer.Get("Msg_DepsStillMissingFmt"),
                        string.Join(", ", left.Select(m => m.Id))));
                return true;

            default:
                _host.Say(Localizer.Get("Msg_DepsStartCancelled"));
                return false;
        }
    }

    /// <summary>Shows a dialog owned by the main window, so it cannot open behind it.</summary>
    private static async Task<MissingDependenciesChoice> ShowDialogAsync(MissingDependenciesDialog dialog)
    {
        var owner = AppWindows.Main;

        if (owner is null)
        {
            // No window to own it means no way to show it. Treated as "start anyway" rather than
            // "cancel": the user pressed Start, and swallowing that because of a missing owner
            // would look exactly like the button not working.
            return MissingDependenciesChoice.StartAnyway;
        }

        await dialog.ShowDialog(owner);
        return dialog.Choice;
    }

    /// <summary>
    /// Stops a start that would fail on the path, offering to rename the folder when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Renaming is offered only when the offending character is in the server's own folder name.
    /// When it sits in a parent, renaming that folder would move everything else under it as well,
    /// which is not the app's to decide — so it says which character and where, and stops.
    /// </para>
    /// <para>
    /// Skipped during an unattended auto-restart for the same reason the busy-port check is: there
    /// is nobody there to answer a dialog.
    /// </para>
    /// </remarks>
    public async Task<bool> TryFixRejectedPathAsync(bool isAutoRestart)
    {
        if (!BukkitPathRule.Rejects(Config.FolderPath, Config.Type)) return true;

        var bad = BukkitPathRule.OffendingCharacter(Config.FolderPath)!.Value;
        var suggestion = BukkitPathRule.SuggestCleanPath(Config.FolderPath);

        if (suggestion is null || isAutoRestart)
        {
            _host.Say(string.Format(Localizer.Get("Msg_BukkitPathFmt"), bad));
            return false;
        }

        var accepted = await Dialogs.Current.ConfirmAsync(
            string.Format(Localizer.Get("Msg_BukkitPathRenameConfirm"),
                bad, Path.GetFileName(Config.FolderPath), Path.GetFileName(suggestion)),
            Localizer.Get("Msg_BukkitPathRenameTitle"));

        if (!accepted)
        {
            _host.Say(string.Format(Localizer.Get("Msg_BukkitPathFmt"), bad));
            return false;
        }

        try
        {
            // Refuse rather than merge into something that already exists: a folder of that name
            // may be another server, and Directory.Move would fail halfway or bury it.
            if (Directory.Exists(suggestion) || File.Exists(suggestion))
                throw new IOException(string.Format(
                    Localizer.Get("Msg_BukkitPathRenameExists"), Path.GetFileName(suggestion)));

            Directory.Move(Config.FolderPath, suggestion);
            // Announces on its own now, which is what re-reads the port, the MOTD, the icon, the
            // content folder and the backup list. Setting it used to refresh only the MOTD, so the
            // rest went on describing a folder that no longer existed under that name.
            Config.FolderPath = suggestion;
            _host.ConfigChanged();      // persists the new path before anything else runs

            _host.Say(string.Format(Localizer.Get("Msg_BukkitPathRenamedFmt"), suggestion));
            return true;
        }
        catch (Exception ex)
        {
            _host.Say(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
            return false;
        }
    }

    /// <summary>
    /// The port is busy: identifies the process and offers to close it. Returns true if it became free.
    /// </summary>
    private async Task<bool> TryFreePortAsync(int port)
    {
        var pid = _ports.GetListeningPid(port);
        var (procDesc, isJava) = DescribeHolder(pid);

        // A Java holding the port is most likely a server left running when the app closed badly,
        // with players' progress not yet saved. Killing it is still offered — it may be the only
        // way — but not without saying what it costs.
        var question = string.Format(Localizer.Get("Msg_PortBusyConfirm"), port, procDesc);
        if (isJava) question += Environment.NewLine + Environment.NewLine + Localizer.Get("Msg_PortBusyJavaNote");

        var accepted = await Dialogs.Current.ConfirmAsync(question, Localizer.Get("Msg_PortBusyTitle"));

        if (!accepted)
        {
            _host.Say(string.Format(Localizer.Get("Msg_PortBusyNotStarted"), port, procDesc));
            return false;
        }

        if (!CloseHolder(pid, port, procDesc))
            return false;

        return await WaitForFreeAsync(port);
    }

    /// <summary>Who holds the port, as the question to the user words it, and whether it is a Java.</summary>
    private static (string Description, bool IsJava) DescribeHolder(int? pid)
    {
        var description = Localizer.Get("Msg_OtherApp");
        if (!pid.HasValue) return (description, false);

        try
        {
            var name = System.Diagnostics.Process.GetProcessById(pid.Value).ProcessName;
            return ($"\"{name}\" (PID {pid})", name.StartsWith("java", StringComparison.OrdinalIgnoreCase));
        }
        catch { return ($"PID {pid}", false); }
    }

    /// <summary>
    /// Closes the process the user agreed to close. False when it must not go on, after saying why.
    /// </summary>
    private bool CloseHolder(int? pid, int port, string procDesc)
    {
        try
        {
            if (!pid.HasValue) return true;

            // The confirmation dialog may have stayed open for a while: re-check that the SAME
            // process still holds the port right before killing. If the original one died in
            // the meantime, the OS can reuse its PID — killing blindly (with entireProcessTree)
            // could take down an innocent process the user never approved.
            var currentPid = _ports.GetListeningPid(port);
            if (currentPid is null)
            {
                // Nobody is listening anymore: nothing to kill, fall through to the free check.
                return true;
            }
            if (currentPid != pid)
            {
                _host.Say(string.Format(Localizer.Get("Msg_PortOwnerChangedFmt"), port));
                return false;
            }

            System.Diagnostics.Process.GetProcessById(pid.Value).Kill(entireProcessTree: true);
            _host.Say(string.Format(Localizer.Get("Msg_ClosedPortProcess"), port, procDesc));
            return true;
        }
        catch (Exception ex)
        {
            _host.Say(string.Format(Localizer.Get("Msg_CannotKill"), ErrorMessages.Describe(ex)));
            return false;
        }
    }

    /// <summary>Waits a few seconds for the port to be released; false (after saying so) if it is not.</summary>
    private async Task<bool> WaitForFreeAsync(int port)
    {
        for (var i = 0; i < 12 && _ports.IsPortInUse(port); i++)
            await Task.Delay(300);

        if (_ports.IsPortInUse(port))
        {
            _host.Say(string.Format(Localizer.Get("Msg_PortStillBusy"), port));
            return false;
        }
        return true;
    }
}
