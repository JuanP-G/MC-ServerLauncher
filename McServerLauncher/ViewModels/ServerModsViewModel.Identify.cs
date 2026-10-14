using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

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
/// </remarks>
public partial class ServerModsViewModel
{
    /// <summary>What the store said each hash is; null when it answered and did not know it.</summary>
    private readonly Dictionary<string, VersionResult?> _storeAnswers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-1 of every jar in <paramref name="paths"/> that can be read, by path.</summary>
    private async Task<Dictionary<string, string>> HashesAsync(IEnumerable<string> paths,
        CancellationToken ct)
    {
        var hashByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try { hashByPath[path] = await _hashes.Sha1Async(path, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* unreadable or locked: it simply is not identified */ }
        }
        return hashByPath;
    }

    /// <summary>Asks the store about the hashes it has not been asked about this session.</summary>
    private async Task LearnAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        var unasked = hashes.Where(h => !_storeAnswers.ContainsKey(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unasked.Count == 0) return;

        var answer = await _modrinthService.GetVersionsByHashAsync(unasked, ct);
        foreach (var (hash, version) in answer) _storeAnswers[hash] = version;

        // The store answered, so what it left out it does not know. An empty answer says nothing.
        if (answer.Count > 0)
            foreach (var hash in unasked) _storeAnswers.TryAdd(hash, null);
    }

    /// <summary>The version the store said <paramref name="hash"/> is, or null.</summary>
    private VersionResult? Known(string hash) =>
        _storeAnswers.TryGetValue(hash, out var version) ? version : null;

    /// <summary>The project <paramref name="hash"/> is, when it is that version's main file.</summary>
    private string? ProjectOf(string hash) =>
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
    internal static bool IsPrimaryFile(VersionResult version, string sha1)
    {
        var match = version.Files.FirstOrDefault(f =>
            string.Equals(f.Hashes?.Sha1, sha1, StringComparison.OrdinalIgnoreCase));
        return match is null || match.Primary || version.Files.Count == 1;
    }

    /// <summary>The content files in <paramref name="folder"/>: jars, switched on or off.</summary>
    private static IEnumerable<string> ContentFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(ContentManifest.IsContentFile)
            : Enumerable.Empty<string>();
}
