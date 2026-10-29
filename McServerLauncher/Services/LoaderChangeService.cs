using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What the Loader page asks for: change this server's type, keeping its world.</summary>
/// <param name="Folder">The server's folder.</param>
/// <param name="From">The type it is now.</param>
/// <param name="To">The type it should become.</param>
/// <param name="Version">The Minecraft version to install the type for.</param>
/// <param name="CurrentJavaPath">The Java the server has now; kept if a new one cannot be prepared.</param>
/// <param name="MinRamGb">The server's minimum RAM, written into the launch files.</param>
/// <param name="MaxRamGb">The server's maximum RAM, written into the launch files.</param>
/// <param name="KeepRunBat">The user's own <c>run.bat</c> is restored after the install, and the
/// loader's JVM-args file is left alone.</param>
public sealed record LoaderChange(
    string Folder, ServerType From, ServerType To, MinecraftVersion Version,
    string CurrentJavaPath, int MinRamGb, int MaxRamGb, bool KeepRunBat);

/// <summary>The fields of the server's config that an install changes.</summary>
public sealed record LoaderChangeResult(
    ServerType Type, string GameVersion, string ModLoaderVersion, string ForgeArgs,
    string JarFile, string JavaPath);

/// <summary>
/// Changes a server from one type to another — Vanilla into a loader, a loader into another, or back
/// to Vanilla — without touching the world.
/// </summary>
/// <remarks>
/// <para>
/// The work of the Loader page's Install button, taken out of its click handler so the page keeps
/// only the form and the busy state. It does not change the server's config: it returns what the
/// install produced and the page writes it, because the config is the page's draft and only the page
/// knows whether the write may happen.
/// </para>
/// <para>
/// The order matters. The user's <c>run.bat</c> is read first, because the Forge and NeoForge
/// installers overwrite it; the content of the old family is archived under the name the OLD type
/// used, before anything is installed; and only then is the jar installed.
/// </para>
/// </remarks>
public sealed class LoaderChangeService
{
    private readonly MinecraftVersionService _versions;
    private readonly ServerJarInstaller _installer;
    private readonly ServerCreationService _creation;
    private readonly JavaService _java;

    public LoaderChangeService(MinecraftVersionService? versions = null, ServerJarInstaller? installer = null,
        ServerCreationService? creation = null, JavaService? java = null)
    {
        _versions = versions ?? new MinecraftVersionService();
        _installer = installer ?? new ServerJarInstaller();
        _creation = creation ?? new ServerCreationService();
        _java = java ?? new JavaService();
    }

    /// <summary>
    /// Installs <paramref name="change"/>.To in the server's folder and returns what changed.
    /// </summary>
    /// <param name="change">What to convert, already checked by the page.</param>
    /// <param name="log">Told what is happening, line by line. Called on the caller's context.</param>
    public async Task<LoaderChangeResult> ConvertAsync(LoaderChange change, Action<string> log)
    {
        var progress = new Progress<string>(log);

        log(string.Format(Localizer.Get("Msg_Resolving"), change.Version.Id));
        var details = await _versions.GetVersionDetailsAsync(change.Version);

        log(string.Format(Localizer.Get("Msg_CheckingJava"), change.Version.Id, details.JavaMajor));
        var javaPath = await EnsureJavaOrKeepAsync(details.JavaMajor, change.CurrentJavaPath, progress, log);

        // Forge and NeoForge installers overwrite run.bat. Keep the user's copy if they asked,
        // and leave user_jvm_args.txt alone in that case too: their script reads it, and
        // rewriting the memory settings would quietly undo whatever they had set.
        var runBatPath = Path.Combine(change.Folder, "run.bat");
        var keptRunBat = change.KeepRunBat && File.Exists(runBatPath) ? File.ReadAllText(runBatPath) : null;

        // Before the config says the new type: the old family's folder has to be found under
        // the name the OLD type used.
        var archived = ContentMigrationService.ArchiveIfFamilyChanged(
            change.Folder, change.From, change.To, DateTime.Now);
        if (archived is not null)
            log(string.Format(Localizer.Get("Msg_ContentArchivedFmt"), archived));

        var installed = await _installer.InstallAsync(
            change.To, change.Folder, change.Version.Id, details, javaPath,
            change.MinRamGb, change.MaxRamGb, progress, writeLoaderJvmArgs: !change.KeepRunBat);

        if (keptRunBat is not null)
            File.WriteAllText(runBatPath, keptRunBat);
        else if (!change.KeepRunBat && !installed.LaunchesViaArgsFile)
            _creation.WriteRunBat(change.Folder, change.MinRamGb, change.MaxRamGb, installed.JarFile, javaPath);

        return new LoaderChangeResult(
            change.To, change.Version.Id, installed.LoaderVersion, installed.ForgeArgs,
            // A loader launched through an args file has no runnable jar; the field still has to
            // name something, and "server.jar" is what the rest of the app expects to find there.
            installed.LaunchesViaArgsFile ? "server.jar" : installed.JarFile,
            javaPath);
    }

    /// <summary>The Java the version needs, or the one the server already had if it cannot be prepared.</summary>
    private async Task<string> EnsureJavaOrKeepAsync(int major, string current, IProgress<string> progress,
        Action<string> log)
    {
        try
        {
            return await _java.EnsureJavaAsync(major, progress);
        }
        catch (Exception ex)
        {
            log(string.Format(Localizer.Get("Msg_JavaPrepareFail"), major, ErrorMessages.Describe(ex)));
            log(Localizer.Get("Msg_UseSystemJava"));
            return current;
        }
    }
}
