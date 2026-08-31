using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Puts the right server files on disk for a server type, whichever way that type is obtained.
/// </summary>
/// <remarks>
/// <para>
/// The ways differ more than they look: Vanilla, Paper and Purpur are a jar to download, Fabric is
/// a jar the loader builds for a version pair, and Forge and NeoForge run an installer that leaves
/// no runnable jar at all and must be launched through an args file instead.
/// </para>
/// <para>
/// This chain used to exist twice, spelled out inline in the create-server dialog and again in the
/// change-loader dialog. Every type added had to be added to both, and a type present in one and
/// missing from the other fell through to "download the vanilla jar" without complaining — you
/// would pick Purpur and quietly get Vanilla.
/// </para>
/// </remarks>
public class ServerJarInstaller
{
    private readonly MinecraftVersionService _versions;
    private readonly ModLoaderService _mods;
    private readonly PaperService _paper;
    private readonly PurpurService _purpur;
    private readonly ServerCreationService _creation;

    public ServerJarInstaller(
        MinecraftVersionService? versions = null, ModLoaderService? mods = null, PaperService? paper = null,
        PurpurService? purpur = null, ServerCreationService? creation = null)
    {
        _versions = versions ?? new MinecraftVersionService();
        _mods = mods ?? new ModLoaderService();
        _paper = paper ?? new PaperService();
        _purpur = purpur ?? new PurpurService();
        _creation = creation ?? new ServerCreationService();
    }

    /// <summary>What the install left behind, in the shape the caller needs for its config.</summary>
    /// <remarks>
    /// An empty <c>JarFile</c> is not a failure: modern Forge and NeoForge genuinely have no
    /// runnable jar and launch through <c>ForgeArgs</c> instead. Callers must not write a run script
    /// in that case, because the loader ships its own.
    /// </remarks>
    public record InstallResult(string JarFile, string LoaderVersion, string ForgeArgs)
    {
        /// <summary>True when this loader launches through an args file rather than a jar.</summary>
        public bool LaunchesViaArgsFile => string.IsNullOrEmpty(JarFile);
    }

    /// <summary>Every type this can install. A type absent from here cannot be offered.</summary>
    /// <remarks>
    /// Exposed so the picker and its test can be checked against reality rather than against a list
    /// written out a second time by hand.
    /// </remarks>
    public static readonly ServerType[] Installable =
    {
        ServerType.Vanilla, ServerType.Paper, ServerType.Purpur,
        ServerType.Fabric, ServerType.Forge, ServerType.NeoForge
    };

    /// <summary>
    /// Installs <paramref name="type"/> for a Minecraft version into <paramref name="folder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The RAM figures are here because Forge and NeoForge need them written into
    /// <c>user_jvm_args.txt</c> during the install — their own run script reads that file, and
    /// without it the server ignores the memory the user asked for.
    /// </para>
    /// <para>
    /// <paramref name="writeLoaderJvmArgs"/> exists for the one caller that must not touch it: the
    /// change-loader dialog offers to keep a run script the user wrote themselves, and overwriting
    /// the memory settings it reads would quietly undo their edit.
    /// </para>
    /// </remarks>
    public async Task<InstallResult> InstallAsync(
        ServerType type, string folder, string mcVersion, MinecraftVersionService.VersionDetails details,
        string javaPath, int minRamGb, int maxRamGb, IProgress<string>? log,
        bool writeLoaderJvmArgs = true, CancellationToken ct = default)
    {
        switch (type)
        {
            case ServerType.Fabric:
            {
                log?.Report(Localizer.Get("Msg_FabricResolving"));
                var loaderVersion = await _mods.GetLatestFabricLoaderVersionAsync(ct);
                // Named apart from the vanilla jar on purpose: ServerDetectionService recognises a
                // Fabric server by finding exactly this file, so renaming it makes older servers
                // come back as Vanilla.
                const string jarName = "fabric-server.jar";
                await _mods.DownloadFabricServerAsync(mcVersion, loaderVersion, Path.Combine(folder, jarName), log, ct);
                return new InstallResult(jarName, loaderVersion, string.Empty);
            }

            case ServerType.Paper:
            {
                log?.Report(Localizer.Get("Msg_PaperResolving"));
                var build = await _paper.GetLatestBuildAsync(mcVersion, ct)
                    ?? throw new InvalidOperationException(string.Format(Localizer.Get("Msg_PaperNoBuild"), mcVersion));

                const string jarName = "paper-server.jar";
                await _paper.DownloadPaperServerAsync(build, Path.Combine(folder, jarName), log, ct);
                return new InstallResult(jarName, build.Build.ToString(), string.Empty);
            }

            case ServerType.Purpur:
            {
                log?.Report(Localizer.Get("Msg_PurpurResolving"));
                var build = await _purpur.GetLatestBuildAsync(mcVersion, ct)
                    ?? throw new InvalidOperationException(string.Format(Localizer.Get("Msg_PurpurNoBuild"), mcVersion));

                const string jarName = "purpur-server.jar";
                await _purpur.DownloadPurpurServerAsync(build, Path.Combine(folder, jarName), log, ct);
                return new InstallResult(jarName, build.Build, string.Empty);
            }

            case ServerType.Forge:
            {
                log?.Report(Localizer.Get("Msg_ForgeResolving"));
                var forgeVersion = await _mods.GetRecommendedForgeVersionAsync(mcVersion, ct);
                if (string.IsNullOrEmpty(forgeVersion))
                    throw new InvalidOperationException(string.Format(Localizer.Get("Msg_ForgeNoVersion"), mcVersion));

                var forge = await _mods.InstallForgeServerAsync(folder, mcVersion, forgeVersion, javaPath, log, ct);

                if (forge.ArgsId is not null)
                {
                    // Its own run script reads user_jvm_args.txt, so the RAM settings go there.
                    if (writeLoaderJvmArgs) _creation.WriteForgeUserJvmArgs(folder, minRamGb, maxRamGb);
                    return new InstallResult(string.Empty, forgeVersion, forge.ArgsId);
                }

                if (!string.IsNullOrEmpty(forge.JarFile))
                    return new InstallResult(forge.JarFile!, forgeVersion, string.Empty);   // old Forge

                throw new InvalidOperationException(Localizer.Get("Msg_ForgeInstallNoOutput"));
            }

            case ServerType.NeoForge:
            {
                log?.Report(Localizer.Get("Msg_NeoForgeResolving"));
                var choice = await _mods.GetNeoForgeVersionAsync(mcVersion, ct)
                    ?? throw new InvalidOperationException(
                        string.Format(Localizer.Get("Msg_NeoForgeNoVersion"), mcVersion));

                // Said before installing, not after: for six Minecraft versions a pre-release was
                // the only NeoForge there had ever been, and that is worth knowing up front.
                if (choice.IsBeta)
                    log?.Report(string.Format(Localizer.Get("Msg_NeoForgeBetaWarning"), choice.Version));

                var neo = await _mods.InstallNeoForgeServerAsync(folder, choice.Version, javaPath, log, ct);
                if (neo.ArgsId is null)
                    throw new InvalidOperationException(Localizer.Get("Msg_NeoForgeInstallNoOutput"));

                if (writeLoaderJvmArgs) _creation.WriteForgeUserJvmArgs(folder, minRamGb, maxRamGb);
                return new InstallResult(string.Empty, choice.Version, neo.ArgsId);
            }

            case ServerType.Vanilla:
            {
                const string jarName = "server.jar";
                await _versions.DownloadFileAsync(details.ServerUrl, Path.Combine(folder, jarName), log, details.Sha1, ct);
                return new InstallResult(jarName, string.Empty, string.Empty);
            }

            default:
                // Reached only by a type added to the enum and not here. Saying so beats installing
                // the vanilla jar and letting the user find out their pick was ignored.
                throw new InvalidOperationException(
                    string.Format(Localizer.Get("Msg_TypeNotInstallableFmt"), type));
        }
    }
}
