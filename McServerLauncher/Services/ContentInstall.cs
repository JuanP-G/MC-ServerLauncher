using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What every content installer does: refuse a server type it cannot serve, make sure the content
/// folder exists, fetch a jar from Modrinth or GeyserMC verified, and put it in place of any older
/// copy.
/// </summary>
/// <remarks>
/// <para>
/// Crossplay, Hydraulic and the multi-version plugins each opened with the same five lines, apart
/// from the message key, and each fetched from Modrinth with its own copy of the same twelve — one
/// of which took a version's first file where the store takes its main one. Three copies is where the
/// fourth installer copies one of them and the four start to differ.
/// </para>
/// <para>
/// The folder comes from <see cref="ContentManifest.FolderOf"/>, so "plugins for the Bukkit family,
/// mods for the loaders" is still decided in exactly one place.
/// </para>
/// </remarks>
internal static class ContentInstall
{
    /// <summary>
    /// The server's content folder, created if missing.
    /// </summary>
    /// <param name="config">The server being installed into.</param>
    /// <param name="supported">Whether the caller can install onto this server type at all.</param>
    /// <param name="unsupportedFmtKey">
    /// Resx key of the refusal; <c>{0}</c> receives the server type.
    /// </param>
    /// <exception cref="InvalidOperationException">When <paramref name="supported"/> is false.</exception>
    public static string PrepareFolder(ServerConfig config, bool supported, string unsupportedFmtKey)
    {
        if (!supported)
            throw new UserFacingException(
                string.Format(Localizer.Get(unsupportedFmtKey), config.Type));

        var folder = ContentManifest.FolderOf(config);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Downloads one jar into <paramref name="folder"/>, then takes out every other copy of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These installers run again whenever their switch is turned back on, and on every import of a
    /// folder that already has the jars. Each run used to add the newest build beside whatever was
    /// there — a Geyser from last month next to today's, which the loader refuses to start with.
    /// </para>
    /// <para>
    /// Matched by the id the jar gives itself, never by the store: Floodgate for Paper and Hydraulic
    /// come from GeyserMC's site, which Modrinth's hash lookup has never heard of, and this has to
    /// work for them too.
    /// </para>
    /// </remarks>
    /// <param name="folder">The content folder.</param>
    /// <param name="fileName">The new jar's file name, from the download source.</param>
    /// <param name="display">What to call it in the error, if an old copy is in use.</param>
    /// <param name="download">Writes the verified jar to the path it is given.</param>
    /// <exception cref="InvalidOperationException">An old copy is in use; nothing was changed.</exception>
    public static async Task InstallReplacingAsync(string folder, string fileName, string display,
        Func<string, Task> download)
    {
        var path = AtomicDownload.PathIn(folder, fileName);
        var replacesAFile = File.Exists(path);

        await download(path);

        ReplacePrevious(folder, path, display, canRollBack: !replacesAFile);
    }

    /// <summary>
    /// The newest version of a Modrinth project for this server, put in place of any older copy.
    /// </summary>
    /// <param name="modrinth">The store.</param>
    /// <param name="config">The server, for its type and Minecraft version.</param>
    /// <param name="folder">The content folder.</param>
    /// <param name="projectId">The Modrinth project.</param>
    /// <param name="display">Its name, as the log and the errors say it.</param>
    /// <param name="log">Where the steps are reported.</param>
    /// <param name="ct">Cancels the download.</param>
    public static async Task InstallFromModrinthAsync(ModrinthService modrinth, ServerConfig config,
        string folder, string projectId, string display, IProgress<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log?.Report(string.Format(Localizer.Get("Msg_CrossplayResolvingFmt"), display));

        var version = await modrinth.GetLatestProjectVersionAsync(projectId, config.Type, config.GameVersion,
            ct);
        // The main file, as the store's own install picks it: a version can carry a module or a
        // sources jar beside it, and the first file listed is not always the right one.
        var file = version?.Files.FirstOrDefault(f => f.Primary) ?? version?.Files.FirstOrDefault();
        if (file is null)
            throw new UserFacingException(string.Format(
                Localizer.Get("Msg_CrossplayNoVersionFmt"), display, config.GameVersion, config.Type));

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayInstallingFmt"), display,
            version!.VersionNumber));
        await InstallReplacingAsync(folder, file.Filename, display, path =>
            modrinth.DownloadModAsync(file.Url, path, file.Hashes?.Sha512, file.Hashes?.Sha1, ct: ct));
    }

    /// <summary>
    /// A build from GeyserMC's downloads site, verified against the SHA-256 it publishes before it is
    /// kept, and put in place of any older copy.
    /// </summary>
    /// <param name="artifact">The build.</param>
    /// <param name="folder">The content folder.</param>
    /// <param name="display">Its name, as the errors say it.</param>
    /// <param name="alsoCheck">One more check of the file before it is kept; it throws to refuse.</param>
    /// <param name="ct">Cancels the download.</param>
    public static async Task InstallFromGeyserAsync(GeyserDownloadsApi.Artifact artifact, string folder,
        string display, Func<string, CancellationToken, Task>? alsoCheck, CancellationToken ct)
    {
        using var resp = await GeyserDownloadsApi.OpenAsync(artifact, ct);
        resp.EnsureSuccessStatusCode();

        await InstallReplacingAsync(folder, artifact.FileName, display, path =>
            AtomicDownload.ToFileAsync(resp.Content, path,
                verifyAsync: async (part, token) =>
                {
                    await DownloadVerifier.VerifyAsync(part, artifact.Sha256, HashAlgorithmName.SHA256,
                        token);
                    if (alsoCheck is not null) await alsoCheck(part, token);
                },
                ct: ct));
    }

    /// <summary>
    /// Takes out every other copy of <paramref name="newJar"/>, or throws having changed nothing.
    /// </summary>
    /// <param name="folder">The content folder.</param>
    /// <param name="newJar">The jar just installed.</param>
    /// <param name="display">What to call it in the error.</param>
    /// <param name="canRollBack">
    /// Whether the new jar may be deleted if the old ones cannot. Not when it overwrote a file of the
    /// same name: then it is the only copy left.
    /// </param>
    internal static IReadOnlyList<string> ReplacePrevious(string folder, string newJar, string display,
        bool canRollBack = true)
    {
        var others = ContentReplacement.OthersOf(folder, newJar);
        if (others.Count == 0) return others;

        var (result, removed) = ContentReplacement.RemoveAllOrNothing(others, canRollBack ? newJar : null);
        if (result == ContentReplacement.RemoveResult.Locked)
            throw new UserFacingException(string.Format(Localizer.Get("Msg_ContentInUseFmt"), display));

        return removed;
    }
}
