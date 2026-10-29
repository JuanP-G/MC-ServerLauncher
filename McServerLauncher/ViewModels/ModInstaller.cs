using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Installs one project from the store: picks the version, downloads and verifies it, takes the
/// other copies out, and brings the libraries it needs.
/// </summary>
/// <remarks>
/// <para>
/// This is the only install path in the app — the details page, the cards and the Performance page
/// all end here through the panel. It decides nothing about how the answer is worded: it returns a
/// <see cref="Result"/> and the panel turns that into the banner, because the wording is the panel's
/// and the install is not.
/// </para>
/// <para>
/// What it is doing right now (resolving, downloading, the libraries) goes to the
/// <see cref="IProgress{T}"/> it is given.
/// </para>
/// </remarks>
internal sealed class ModInstaller
{
    /// <summary>How an install ended.</summary>
    public enum Outcome
    {
        /// <summary>The file is in place.</summary>
        Installed,

        /// <summary>The project has no version for this loader and Minecraft version.</summary>
        NoCompatibleVersion,

        /// <summary>Another copy is held open by the running server, so nothing was changed.</summary>
        InUse,

        /// <summary>Anything else: no connection, a checksum that did not match, a full disk.</summary>
        Failed,
    }

    /// <summary>What happened, with what the panel needs to say so.</summary>
    /// <param name="Outcome">How it ended.</param>
    /// <param name="Libraries">How many libraries were installed with it.</param>
    /// <param name="UnresolvedLibraries">Libraries with no build for this Minecraft version.</param>
    /// <param name="BlockingCopy">The file name in the way, for <see cref="Outcome.InUse"/>.</param>
    /// <param name="Error">The description of the error, for <see cref="Outcome.Failed"/>.</param>
    public sealed record Result(Outcome Outcome, int Libraries = 0,
        IReadOnlyList<string>? UnresolvedLibraries = null, string? BlockingCopy = null, string? Error = null);

    private readonly ServerConfig _config;
    private readonly ModrinthService _store;
    private readonly InstalledContentIndex _index;
    private readonly ModDependencyInstaller _dependencies;

    /// <param name="config">The server the project is installed into.</param>
    /// <param name="store">Resolves versions and downloads the file.</param>
    /// <param name="index">What the store says each jar already here is.</param>
    /// <param name="dependencies">Brings the libraries the project needs.</param>
    public ModInstaller(ServerConfig config, ModrinthService store, InstalledContentIndex index,
        ModDependencyInstaller dependencies)
    {
        _config = config;
        _store = store;
        _index = index;
        _dependencies = dependencies;
    }

    /// <summary>
    /// Installs a project. <paramref name="chosen"/> installs that exact version (the details page
    /// offers the last few); without it the newest compatible one is resolved.
    /// </summary>
    /// <param name="projectId">The project to install.</param>
    /// <param name="chosen">One exact version, or null for the newest compatible one.</param>
    /// <param name="toPlayerPack">
    /// Put it in the players' pack instead of the server's folder: for the client-only mods, which
    /// do nothing on a server. Everything else about the install is the same. The caller has already
    /// ruled it out for a plugin server.
    /// </param>
    /// <param name="progress">Told what is under way. Progress, not outcome.</param>
    /// <param name="ct">Cancels the downloads.</param>
    public async Task<Result> InstallAsync(string projectId, VersionResult? chosen, bool toPlayerPack,
        IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            progress.Report(Localizer.Get("Msg_ResolvingVersion"));
            var version = chosen
                ?? await _store.GetLatestProjectVersionAsync(projectId, _config.Type, _config.GameVersion, ct);
            if (version == null || version.Files.Count == 0)
                return new Result(Outcome.NoCompatibleVersion);

            var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.First();

            var folder = toPlayerPack
                ? ClientPack.FolderOf(_config)
                : Path.Combine(_config.FolderPath, ServerTypeCatalog.ContentFolder(_config.Type));
            Directory.CreateDirectory(folder);

            // The file name comes from the Modrinth API, so the join goes through the one
            // place that keeps it inside the folder.
            var destPath = AtomicDownload.PathIn(folder, file.Filename);
            var replacesAFile = File.Exists(destPath);

            // Which project each jar already here is. Found before downloading, while the folder
            // still holds only what the user had.
            var projectByPath = await _index.ProjectByPathAsync(folder, ct);

            progress.Report(string.Format(Localizer.Get("Msg_DownloadingMod"), file.Filename));

            // Mods are third-party jars chosen by the user: verify against Modrinth's own checksum.
            await _store.DownloadModAsync(file.Url, destPath, file.Hashes?.Sha512, file.Hashes?.Sha1, ct: ct);

            if (RemoveOtherCopies(folder, destPath, version.ProjectId, projectByPath, replacesAFile)
                is { } blocking)
                return new Result(Outcome.InUse, BlockingCopy: blocking);

            // The libraries it needs, in the same click. Almost every Fabric mod declares at least
            // fabric-api, and a mod installed without them is a server that refuses to start with a
            // list of names the user never chose and has no way to act on from here.
            var (libraries, unresolved) = await _dependencies.InstallForAsync(version, destPath, folder,
                toPlayerPack, progress, ct);
            return new Result(Outcome.Installed, libraries, unresolved);
        }
        catch (Exception ex)
        {
            return new Result(Outcome.Failed, Error: ErrorMessages.Describe(ex));
        }
    }

    /// <summary>
    /// Installing a second version beside the first made the loader refuse to start with
    /// "duplicate mod". The new one replaces every other copy — another version, a disabled one,
    /// one the store does not know — the way an update does; and like an update, a jar the running
    /// server holds open means nothing changes.
    /// </summary>
    /// <returns>The display name of the copy in the way, or null when the others are gone.</returns>
    private string? RemoveOtherCopies(string folder, string destPath, string projectId,
        IReadOnlyDictionary<string, string> projectByPath, bool replacesAFile)
    {
        var others = ContentReplacement.OthersOf(folder, destPath, projectId, projectByPath,
            _index.Manifests.Read);
        var (result, _) = ContentReplacement.RemoveAllOrNothing(others, replacesAFile ? null : destPath);

        return result == ContentReplacement.RemoveResult.Locked
            ? ContentReplacement.DisplayName(others[0])
            : null;
    }
}
