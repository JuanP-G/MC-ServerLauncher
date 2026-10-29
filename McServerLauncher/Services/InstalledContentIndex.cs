using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models.Modrinth;

namespace McServerLauncher.Services;

/// <summary>
/// What the store says each jar on disk is: one answer per file, asked once a session.
/// </summary>
/// <remarks>
/// <para>
/// The same question — "which Modrinth version is this jar?" — was asked in four places with two
/// caches between them and one with none: the replacement of old copies, the "already installed"
/// check, the cleanup panel and the export. Each hashed every jar again and sent the store the
/// hashes the others had just sent, and the cleanup panel asked again on every toggle for the jars
/// the store had already said it did not know.
/// </para>
/// <para>
/// One cache now, by SHA-1. A jar the store answered without is remembered as unknown; an empty
/// answer is not remembered at all, because it cannot be told apart from being offline.
/// </para>
/// <para>
/// <b>One instance per server's mods panel, shared by everything in it</b> — the update check, the
/// install, the cleanup panel and the export. That is the point: the answer one of them paid for is
/// the answer the next one reads. It holds no UI and no cancellation of its own; every call takes
/// the token of whoever is asking.
/// </para>
/// </remarks>
public sealed class InstalledContentIndex
{
    private readonly ModrinthService _store;

    /// <summary>What the store said each hash is; null when it answered and did not know it.</summary>
    private readonly Dictionary<string, VersionResult?> _answers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="store">Asked about the hashes nobody has asked about yet.</param>
    public InstalledContentIndex(ModrinthService store) => _store = store;

    /// <summary>Hashing a jar is the expensive part, so every scan shares these.</summary>
    public FileHashCache Hashes { get; } = new();

    /// <summary>What each jar says about itself, for finding the other copies of a mod.</summary>
    public ContentManifestCache Manifests { get; } = new();

    /// <summary>The SHA-1 of every jar in <paramref name="paths"/> that can be read, by path.</summary>
    public async Task<Dictionary<string, string>> HashesAsync(IEnumerable<string> paths,
        CancellationToken ct)
    {
        var hashByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try { hashByPath[path] = await Hashes.Sha1Async(path, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* unreadable or locked: it simply is not identified */ }
        }
        return hashByPath;
    }

    /// <summary>Asks the store about the hashes it has not been asked about this session.</summary>
    public async Task LearnAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        var unasked = hashes.Where(h => !_answers.ContainsKey(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unasked.Count == 0) return;

        var answer = await _store.GetVersionsByHashAsync(unasked, ct);
        foreach (var (hash, version) in answer) _answers[hash] = version;

        // The store answered, so what it left out it does not know. An empty answer says nothing.
        if (answer.Count > 0)
            foreach (var hash in unasked) _answers.TryAdd(hash, null);
    }

    /// <summary>The version the store said <paramref name="hash"/> is, or null.</summary>
    public VersionResult? Known(string hash) =>
        _answers.TryGetValue(hash, out var version) ? version : null;

    /// <summary>The project <paramref name="hash"/> is, when it is that version's main file.</summary>
    public string? ProjectOf(string hash) =>
        Known(hash) is { } version && IsPrimaryFile(version, hash) ? version.ProjectId : null;

    /// <summary>
    /// Whether the file with <paramref name="sha1"/> is the main one of <paramref name="version"/>.
    /// </summary>
    /// <remarks>
    /// Some projects publish more than one file in a version — a module, a sources jar. The project
    /// is what decides that an installed jar is an old copy of a new one, and a module counted as the
    /// project would be deleted by installing the core. When the store does not say which file it
    /// matched, the version counts as one file.
    /// </remarks>
    public static bool IsPrimaryFile(VersionResult version, string sha1)
    {
        var match = version.Files.FirstOrDefault(f =>
            string.Equals(f.Hashes?.Sha1, sha1, StringComparison.OrdinalIgnoreCase));
        return match is null || match.Primary || version.Files.Count == 1;
    }

    /// <summary>The content files in <paramref name="folder"/>: jars, switched on or off.</summary>
    public static IEnumerable<string> ContentFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(ContentManifest.IsContentFile)
            : Enumerable.Empty<string>();

    /// <summary>
    /// Which Modrinth project each jar in <paramref name="folder"/> is, by path. Jars the store does
    /// not know, and files that are not a version's main one, are left out.
    /// </summary>
    public async Task<Dictionary<string, string>> ProjectByPathAsync(string folder,
        CancellationToken ct = default)
    {
        var hashByPath = await HashesAsync(ContentFiles(folder), ct);
        await LearnAsync(hashByPath.Values, ct);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in hashByPath)
            if (ProjectOf(hash) is { } projectId) result[path] = projectId;
        return result;
    }

    /// <summary>
    /// The project id of every installed jar Modrinth recognises, in <paramref name="folder"/> and
    /// also in <paramref name="alsoFolder"/> when one is given.
    /// </summary>
    /// <remarks>
    /// This is what makes "already installed" a real answer rather than a filename comparison.
    /// Modrinth's dependencies carry no version range, so a project that is present satisfies them;
    /// installing a second copy under a different file name is what produces the loader's
    /// "duplicate mod" failure, which is worse than the one being fixed.
    /// </remarks>
    public async Task<List<string>> InstalledProjectIdsAsync(string folder, string? alsoFolder,
        CancellationToken ct)
    {
        var files = ContentFiles(folder).ToList();
        if (alsoFolder is not null) files.AddRange(ContentFiles(alsoFolder));
        var hashes = (await HashesAsync(files, ct)).Values.ToList();
        await LearnAsync(hashes, ct);
        return hashes.Select(Known).OfType<VersionResult>().Select(v => v.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Every id the enabled jars in a folder answer to, the ones carried inside them included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whatever is in here is not missing, and asking the store for it either finds nothing —
    /// fabric-api's forty-odd modules are not projects — or offers a second copy of a library
    /// that is already loaded because it ships inside the mod next to it.
    /// </para>
    /// <para>
    /// One answer for both places that ask. The scan and the install used to work it out
    /// differently, and the scan also counted disabled jars as providing things, which the start
    /// check rightly does not: a <c>.jar.disabled</c> loads nothing.
    /// </para>
    /// </remarks>
    public static HashSet<string> ModIdsProvidedIn(string folder) =>
        ContentManifest.ReadFolder(folder)
            .SelectMany(m => m.Provides)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
