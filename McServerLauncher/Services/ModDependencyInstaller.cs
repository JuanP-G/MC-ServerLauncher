using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;

namespace McServerLauncher.Services;

/// <summary>
/// Finds the library mods a server's content needs and puts them on disk.
/// </summary>
/// <remarks>
/// <para>
/// Three callers want it and each shows progress somewhere different: the install banner while a mod
/// is being installed, the line under the installed list for "install the missing ones", and the
/// server's console when a start finds something missing. So this class says what it is doing to an
/// <see cref="IProgress{T}"/> it is handed and never decides where that goes.
/// </para>
/// <para>
/// It holds no UI and no token of its own: every call that can wait takes the caller's.
/// </para>
/// </remarks>
public sealed class ModDependencyInstaller
{
    private readonly ServerConfig _config;
    private readonly ModrinthService _store;
    private readonly ModDependencyService _dependencies;
    private readonly InstalledContentIndex _index;

    /// <param name="config">The server whose content folder is being completed.</param>
    /// <param name="store">Where the libraries are downloaded from.</param>
    /// <param name="dependencies">Decides what is missing; shares the store's HTTP client and cache.</param>
    /// <param name="index">What the store says each installed jar is.</param>
    public ModDependencyInstaller(ServerConfig config, ModrinthService store,
        ModDependencyService dependencies, InstalledContentIndex index)
    {
        _config = config;
        _store = store;
        _dependencies = dependencies;
        _index = index;
    }

    private string ContentFolder => Path.Combine(_config.FolderPath, ServerTypeCatalog.ContentFolder(_config.Type));

    /// <summary>
    /// Looks for library mods that the installed ones need and that nobody installed.
    /// </summary>
    /// <remarks>
    /// Runs off the hashes the update check already computed, so it costs one extra request. It
    /// exists for the servers that were built before the installer started pulling dependencies in:
    /// those fail at start-up with the loader listing what is missing, and there is nothing in the
    /// app that would otherwise say so.
    /// </remarks>
    /// <param name="sha1Hashes">The hashes of the installed jars.</param>
    /// <param name="installedFiles">The installed jars, for the ids they declare themselves.</param>
    /// <param name="ct">Cancels the lookups.</param>
    /// <returns>What is missing, or null when the store knows none of the installed jars.</returns>
    public async Task<ModDependencyService.Plan?> FindMissingAsync(IEnumerable<string> sha1Hashes,
        IEnumerable<string> installedFiles, CancellationToken ct)
    {
        // What each installed jar *is* — the update endpoint answers what could replace it, and the
        // dependencies that matter are the ones declared by the file actually on disk.
        var hashes = sha1Hashes.ToList();
        await _index.LearnAsync(hashes, ct);
        var roots = hashes.Select(_index.Known).OfType<VersionResult>().ToList();
        if (roots.Count == 0) return null;

        var installedIds = roots.Select(r => r.ProjectId).ToList();

        var plan = await _dependencies.ResolveMissingAsync(
            roots, _config.Type, _config.GameVersion, installedIds, ct);

        // And what the jars ask for themselves, which is how a mod whose Modrinth page lists no
        // dependencies at all still turns out to need fabric-api.
        var known = installedIds.Concat(plan.Install.Select(i => i.ProjectId)).ToList();

        var providedHere = InstalledContentIndex.ModIdsProvidedIn(ContentFolder);

        var fromJars = await _dependencies.ResolveByModIdAsync(
            installedFiles.SelectMany(ModDependencyService.DeclaredModIds)
                .Where(id => !providedHere.Contains(id)),
            _config.Type, _config.GameVersion, known, ct);

        return new ModDependencyService.Plan(
            plan.Install.Concat(fromJars.Install).ToList(), plan.Unresolved);
    }

    /// <summary>
    /// Fetches the mods or plugins named by <paramref name="ids"/>, for the check before a start.
    /// </summary>
    /// <remarks>
    /// The ids come from the jars, and a mod id is not a store id: <c>cristallib</c> is published on
    /// Modrinth as <c>cristel-lib</c>, and there is no rule that maps one to the other. So anything
    /// the store does not recognise is skipped rather than reported as an error — the caller checks
    /// again afterwards and says what is still missing, which is the honest answer.
    /// </remarks>
    /// <returns>True when something was downloaded, so the caller knows to re-read the folder.</returns>
    public async Task<bool> InstallByIdAsync(IEnumerable<string> ids, IProgress<string>? log,
        CancellationToken ct)
    {
        var wanted = ids.ToList();
        if (wanted.Count == 0) return false;

        log?.Report(Localizer.Get("Msg_ResolvingDependencies"));

        var plan = await _dependencies.ResolveByModIdAsync(
            wanted, _config.Type, _config.GameVersion,
            await _index.InstalledProjectIdsAsync(ContentManifest.FolderOf(_config), null, ct), ct);

        if (plan.Install.Count == 0)
        {
            log?.Report(Localizer.Get("Msg_DepsNoneResolved"));
            return false;
        }

        var folder = ContentFolder;
        Directory.CreateDirectory(folder);

        foreach (var needed in plan.Install)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report(Localizer.Format("Msg_DownloadingMod", needed.Label));
            await DownloadAsync(needed, folder, ct);
        }

        log?.Report(Localizer.Format("Msg_DepsInstalledFmt", plan.Install.Count));
        return true;
    }

    /// <summary>Downloads libraries the scan already found missing, saying which one is under way.</summary>
    public async Task InstallListedAsync(IReadOnlyList<ModDependencyService.Needed> missing,
        IProgress<string> progress, CancellationToken ct)
    {
        var folder = ContentFolder;
        Directory.CreateDirectory(folder);

        foreach (var needed in missing)
        {
            progress.Report(Localizer.Format("Msg_DownloadingMod", needed.Label));
            await DownloadAsync(needed, folder, ct);
        }
    }

    /// <summary>
    /// Installs the required dependencies of <paramref name="version"/> that are not there yet.
    /// </summary>
    /// <returns>
    /// How many were installed, and any that could not be resolved at all. Zero and empty is the
    /// normal answer for a plugin or for a library mod that depends on nothing.
    /// </returns>
    /// <remarks>
    /// <paramref name="forPlayerPack"/> is true when <paramref name="folder"/> is the players' pack.
    /// A library the server already has (Fabric API, nearly always) satisfies the mod and is not
    /// downloaded again; one that is only in the pack does not satisfy a server mod, which is why
    /// this goes one way.
    /// </remarks>
    public async Task<(int Installed, IReadOnlyList<string> Unresolved)> InstallForAsync(
        VersionResult version, string jarPath, string folder, bool forPlayerPack,
        IProgress<string> progress, CancellationToken ct = default)
    {
        var declared = ModDependencyService.DeclaredModIds(jarPath);
        var hasModrinthDeps = version.Dependencies.Any(d =>
            string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase));

        // A plugin, or a mod that genuinely needs nothing: no request, and no hash of the folder.
        if (!hasModrinthDeps && declared.Count == 0) return (0, Array.Empty<string>());

        progress.Report(Localizer.Get("Msg_ResolvingDependencies"));

        var installedIds = await _index.InstalledProjectIdsAsync(ContentManifest.FolderOf(_config),
            forPlayerPack ? folder : null, ct);
        var plan = await _dependencies.ResolveMissingAsync(
            new[] { version }, _config.Type, _config.GameVersion, installedIds, ct);

        var installed = 0;
        foreach (var needed in plan.Install)
        {
            progress.Report(Localizer.Format("Msg_DownloadingMod", needed.Label));
            await DownloadAsync(needed, folder, ct);
            installed++;
        }

        installed += await InstallDeclaredAsync(declared, installedIds.Concat(plan.Install.Select(i => i.ProjectId)),
            folder, forPlayerPack, progress, ct);

        return (installed, plan.Unresolved);
    }

    /// <summary>
    /// Now what the jars themselves ask for, which Modrinth does not always know: Explorify lists
    /// no dependencies there and its own metadata requires fabric-api.
    /// </summary>
    /// <remarks>
    /// Rounds rather than one pass, because a library pulled in this way can declare its own.
    /// </remarks>
    private async Task<int> InstallDeclaredAsync(IReadOnlyCollection<string> declared,
        IEnumerable<string> alreadyKnownProjects, string folder, bool forPlayerPack,
        IProgress<string> progress, CancellationToken ct)
    {
        var installed = 0;
        var known = new List<string>(alreadyKnownProjects);
        var pending = new List<string>(declared);

        for (var round = 0; round < 3 && pending.Count > 0; round++)
        {
            // Read again each round: what was just downloaded may carry what the next one needs.
            var providedHere = InstalledContentIndex.ModIdsProvidedIn(folder);
            if (forPlayerPack)
                providedHere.UnionWith(InstalledContentIndex.ModIdsProvidedIn(ContentManifest.FolderOf(_config)));
            pending = pending.Where(id => !providedHere.Contains(id)).ToList();
            if (pending.Count == 0) break;

            var extra = await _dependencies.ResolveByModIdAsync(
                pending, _config.Type, _config.GameVersion, known, ct);
            if (extra.Install.Count == 0) break;

            pending = new List<string>();
            foreach (var needed in extra.Install)
            {
                progress.Report(Localizer.Format("Msg_DownloadingMod", needed.Label));
                var written = AtomicDownload.PathIn(folder, needed.File.Filename);
                await DownloadAsync(needed, folder, ct);

                known.Add(needed.ProjectId);
                installed++;
                pending.AddRange(ModDependencyService.DeclaredModIds(written));
            }
        }

        return installed;
    }

    /// <summary>One dependency onto disk, verified, with the same path safety as any other install.</summary>
    private Task DownloadAsync(ModDependencyService.Needed needed, string folder, CancellationToken ct)
    {
        var path = AtomicDownload.PathIn(folder, needed.File.Filename);
        return _store.DownloadModAsync(needed.File.Url, path,
            needed.File.Hashes?.Sha512, needed.File.Hashes?.Sha1, ct: ct);
    }
}

/// <summary>
/// Reports on the thread that called it, at once. <see cref="Progress{T}"/> posts to the thread's
/// synchronization context instead, so a late message can land after the line that was written to
/// say the work finished.
/// </summary>
/// <typeparam name="T">What is reported.</typeparam>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    /// <param name="handler">Called for every report, on the reporting thread.</param>
    public InlineProgress(Action<T> handler) => _handler = handler;

    /// <inheritdoc />
    public void Report(T value) => _handler(value);
}
