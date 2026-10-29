using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What the new-server form asks for, once it has been checked and is safe to act on.</summary>
/// <param name="Name">The name shown in the app.</param>
/// <param name="Folder">The folder the server is created in.</param>
/// <param name="Type">The kind of server.</param>
/// <param name="Version">The Minecraft version, as Mojang's list knows it.</param>
/// <param name="Memory">Whether the RAM is picked by the app or by the user.</param>
/// <param name="MinRamGb">Minimum RAM in whole gigabytes.</param>
/// <param name="MaxRamGb">Maximum RAM in whole gigabytes.</param>
/// <param name="Port">The port the server listens on.</param>
/// <param name="Seed">The world seed, or null/empty for a random one.</param>
/// <param name="MultiVersion">Whether other client versions may join.</param>
/// <param name="Hydraulic">Whether Bedrock mod content is enabled.</param>
/// <param name="Playit">Whether the server goes out through a Playit tunnel.</param>
/// <param name="Crossplay">Whether Bedrock players may join (only honoured when the type allows it).</param>
public sealed record NewServerPlan(
    string Name, string Folder, ServerType Type, MinecraftVersion Version,
    MemoryMode Memory, int MinRamGb, int MaxRamGb, int Port, string? Seed,
    bool MultiVersion, bool Hydraulic, bool Playit, bool Crossplay);

/// <summary>
/// Makes a server on disk, or takes over a folder that already holds one: everything the new-server
/// panel does once the form has been accepted.
/// </summary>
/// <remarks>
/// <para>
/// This used to be written into the panel's click handlers, so the only way to run the create path
/// was through a window. Here it is the same steps in the same order, with the screen replaced by a
/// <c>log</c> callback: the panel keeps what is about the form (checking it, the busy state, the
/// warnings) and this keeps what is about the files. It never shows anything itself — a failure is an
/// exception for the caller to word.
/// </para>
/// <para>
/// The steps are the ones the panel has always taken: resolve the version, make sure the Java that
/// version needs exists (the Forge installer needs it too), install the jar, then write the EULA, the
/// launch script and <c>server.properties</c>.
/// </para>
/// </remarks>
public sealed class ServerProvisioningService
{
    private readonly MinecraftVersionService _versions;
    private readonly ServerJarInstaller _installer;
    private readonly ServerCreationService _creation;
    private readonly JavaService _java;

    public ServerProvisioningService(MinecraftVersionService? versions = null,
        ServerJarInstaller? installer = null, ServerCreationService? creation = null,
        JavaService? java = null)
    {
        _versions = versions ?? new MinecraftVersionService();
        _installer = installer ?? new ServerJarInstaller();
        _creation = creation ?? new ServerCreationService();
        _java = java ?? new JavaService();
    }

    /// <summary>
    /// Creates the server described by <paramref name="plan"/> and returns its configuration, ready to
    /// register.
    /// </summary>
    /// <param name="plan">The form, already checked.</param>
    /// <param name="log">Told what is happening, line by line. Called on the caller's context.</param>
    public async Task<ServerConfig> CreateAsync(NewServerPlan plan, Action<string> log)
    {
        var progress = new Progress<string>(log);
        Directory.CreateDirectory(plan.Folder);

        log(Localizer.Format("Msg_Resolving", plan.Version.Id));
        var details = await _versions.GetVersionDetailsAsync(plan.Version);

        // The Java this version needs goes first: the Forge installer requires a compatible one to run.
        log(Localizer.Format("Msg_CheckingJava", plan.Version.Id, details.JavaMajor));
        var javaPath = await EnsureJavaOrSystemAsync(details.JavaMajor, progress, log,
            ex => Localizer.Format("Msg_JavaPrepareFail", details.JavaMajor, ErrorMessages.Describe(ex)));

        var installed = await _installer.InstallAsync(
            plan.Type, plan.Folder, plan.Version.Id, details, javaPath, plan.MinRamGb, plan.MaxRamGb, progress);

        WriteInitialFiles(plan, installed.JarFile, javaPath, log);
        log(Localizer.Get("Msg_ServerCreated"));
        return ToConfig(plan, installed, javaPath);
    }

    /// <summary>
    /// Takes over a folder that already holds a server: nothing downloaded, nothing installed, nothing
    /// written but the port if it was changed and the EULA if the user accepted it here.
    /// </summary>
    /// <param name="form">What the form says about the folder.</param>
    /// <param name="found">What the folder turned out to contain.</param>
    /// <param name="port">The port chosen in the form.</param>
    /// <param name="version">The version picked, or null when Mojang's list does not know it.</param>
    /// <param name="writeEula">True only when the EULA box was there to tick and was ticked.</param>
    /// <param name="log">Told what is happening, line by line.</param>
    public async Task<ServerConfig> AdoptAsync(ExistingServerForm form, ServerDetection found, int port,
        MinecraftVersion? version, bool writeEula, Action<string> log)
    {
        // The Java this Minecraft needs, as when creating — when the version is one Mojang's list
        // knows. Otherwise the start does it: it checks the Java every time anyway.
        var javaPath = version is { Url.Length: > 0 } known
            ? await EnsureJavaForExistingAsync(known, log)
            : "java";

        if (ExistingServer.ApplyPort(form.Folder, found.Port, port))
            log(Localizer.Format("Cs_ExistingPortWrittenFmt", port));

        // Only when the box was there to tick: a folder that had accepted it already is left alone.
        if (writeEula)
        {
            _creation.WriteEula(form.Folder);
            log(Localizer.Get("Cs_ExistingEulaWritten"));
        }

        return ExistingServer.ToConfig(form with { JavaPath = javaPath }, found);
    }

    private async Task<string> EnsureJavaForExistingAsync(MinecraftVersion version, Action<string> log)
    {
        try
        {
            var details = await _versions.GetVersionDetailsAsync(version);
            log(Localizer.Format("Msg_CheckingJava", version.Id, details.JavaMajor));
            return await _java.EnsureJavaAsync(details.JavaMajor, new Progress<string>(log));
        }
        catch (Exception ex)
        {
            log(Localizer.Format("Msg_ErrorFmt", ErrorMessages.Describe(ex)));
            log(Localizer.Get("Msg_UseSystemJava"));
            return "java";
        }
    }

    /// <summary>The Java the version needs, or the one on the PATH when it cannot be prepared.</summary>
    private async Task<string> EnsureJavaOrSystemAsync(int major, IProgress<string> progress,
        Action<string> log, Func<Exception, string> describeFailure)
    {
        try
        {
            return await _java.EnsureJavaAsync(major, progress);
        }
        catch (Exception ex)
        {
            log(describeFailure(ex));
            log(Localizer.Get("Msg_UseSystemJava"));
            return "java";
        }
    }

    /// <summary>The files every new server starts with.</summary>
    private void WriteInitialFiles(NewServerPlan plan, string jarName, string javaPath, Action<string> log)
    {
        log(Localizer.Get("Msg_WritingEula"));
        _creation.WriteEula(plan.Folder);

        // Modern Forge ships its own run.bat (no single jar); only write ours when there is a jar.
        if (!string.IsNullOrEmpty(jarName))
            _creation.WriteRunBat(plan.Folder, plan.MinRamGb, plan.MaxRamGb, jarName, javaPath);

        var seed = plan.Seed?.Trim();
        if (!string.IsNullOrEmpty(seed) && ServerCreationService.WorldExists(plan.Folder))
            log(Localizer.Get("Msg_SeedIgnoredWorldExists"));
        _creation.WriteInitialProperties(plan.Folder, plan.Port, $"{plan.Name} - MC Server Launcher", seed);
    }

    private static ServerConfig ToConfig(NewServerPlan plan, ServerJarInstaller.InstallResult installed,
        string javaPath) => new()
    {
        Name = plan.Name,
        FolderPath = plan.Folder,
        JarFile = string.IsNullOrEmpty(installed.JarFile) ? "server.jar" : installed.JarFile,
        Type = plan.Type,
        MultiVersionEnabled = plan.MultiVersion,
        BedrockModContentEnabled = plan.Hydraulic,
        GameVersion = plan.Version.Id,
        ModLoaderVersion = installed.LoaderVersion,
        ForgeArgs = installed.ForgeArgs,
        JavaPath = javaPath,
        MemoryMode = plan.Memory,
        MinRamGb = plan.MinRamGb,
        MaxRamGb = plan.MaxRamGb,
        PlayitEnabled = plan.Playit,
        CrossplayEnabled = plan.Crossplay && CrossplayService.CanEnable(plan.Type)
    };
}
