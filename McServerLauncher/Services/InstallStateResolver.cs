using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;

namespace McServerLauncher.Services;

/// <summary>
/// Works out, for each project already in a folder, whether the store browser should offer to
/// install it, update it, switch it on — or nothing, because it is there and current.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is recorded when something is installed: a jar is known by its SHA-1, which the store
/// translates into a project (<see cref="InstalledContentIndex"/>). That is also what makes a jar
/// copied in by hand count, as long as the store knows it. One it does not know shows "Install", the
/// same limit the update check has.
/// </para>
/// <para>
/// "Newer" is what the update check asks for — the newest build compatible with this server's loader
/// and Minecraft version, by the installed jar's hash — and is remembered for a few minutes, because
/// the answer is wanted again after every install, switch and delete, and the store's answer does not
/// change that fast. When the store cannot be asked, a project that is there counts as installed and
/// nothing more: saying "up to date" without an answer is a claim about the mod made on no evidence.
/// </para>
/// <para>
/// Not thread-safe, like the index it shares: it belongs to one panel and is used from its UI thread.
/// </para>
/// </remarks>
public sealed class InstallStateResolver
{
    /// <summary>How long a "newest build" answer is reused.</summary>
    internal static readonly TimeSpan NewestLifetime = TimeSpan.FromMinutes(10);

    private readonly ServerConfig _config;
    private readonly ModrinthService _store;
    private readonly InstalledContentIndex _index;
    private readonly TimeProvider _clock;

    /// <summary>The newest build the store named for an installed jar, by the jar's SHA-1.</summary>
    private readonly Dictionary<string, (VersionResult Version, DateTimeOffset At)> _newest
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The loader and Minecraft version <see cref="_newest"/> was asked for.</summary>
    private (ServerType Type, string GameVersion) _newestFor;

    /// <param name="config">The server whose loader and version decide what counts as newer.</param>
    /// <param name="store">Asked for the newest build of what is installed.</param>
    /// <param name="index">What the store says each jar is.</param>
    /// <param name="clock">Only for the tests: tells when an answer has gone stale.</param>
    public InstallStateResolver(ServerConfig config, ModrinthService store, InstalledContentIndex index,
        TimeProvider? clock = null)
    {
        _config = config;
        _store = store;
        _index = index;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The state of every project that has a jar in <paramref name="folder"/>.</summary>
    /// <returns>By project id. A project that is not in it is not installed.</returns>
    public async Task<IReadOnlyDictionary<string, ProjectState>> ResolveAsync(string folder,
        CancellationToken ct)
    {
        var states = new Dictionary<string, ProjectState>(StringComparer.OrdinalIgnoreCase);

        var hashByPath = await _index.HashesAsync(InstalledContentIndex.ContentFiles(folder), ct);
        if (hashByPath.Count == 0) return states;
        await _index.LearnAsync(hashByPath.Values, ct);

        // A project can have several jars — two versions, a switched-off copy — so group them.
        var byProject = new Dictionary<string, List<(string Path, string Hash)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in hashByPath.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (_index.ProjectOf(hash) is not { } projectId) continue;
            if (!byProject.TryGetValue(projectId, out var jars))
                byProject[projectId] = jars = new();
            jars.Add((path, hash));
        }
        if (byProject.Count == 0) return states;

        var enabledByProject = byProject.ToDictionary(p => p.Key,
            p => p.Value.Where(j => !ContentManifest.IsDisabled(j.Path)).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var newest = await NewestAsync(
            enabledByProject.Values.SelectMany(j => j).Select(j => j.Hash), ct);

        foreach (var (projectId, jars) in byProject)
        {
            var enabled = enabledByProject[projectId];
            if (enabled.Count == 0)
            {
                states[projectId] = new ProjectState(InstallState.Disabled, Path: jars[0].Path);
                continue;
            }

            // Any enabled copy that is behind makes the project updatable: installing the newest
            // replaces every copy, so the other ones are not left behind by it.
            states[projectId] = new ProjectState(InstallState.Installed, Path: enabled[0].Path);
            foreach (var (path, hash) in enabled)
            {
                if (!newest.TryGetValue(hash, out var version) || !IsBehind(version, hash)) continue;
                states[projectId] = new ProjectState(InstallState.UpdateAvailable, version, path);
                break;
            }
        }
        return states;
    }

    /// <summary>Whether the build the store calls newest is a different file from the installed one.</summary>
    private static bool IsBehind(VersionResult newest, string installedSha1)
    {
        var file = newest.Files.FirstOrDefault(f => f.Primary) ?? newest.Files.FirstOrDefault();
        return file?.Hashes?.Sha1 is { Length: > 0 } sha1
            && !string.Equals(sha1, installedSha1, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Answers are for one loader and one Minecraft version: convert the server or move it and every
    /// one of them is about something else.
    /// </summary>
    private void DropAnswersForAnotherServer()
    {
        if (_newestFor == (_config.Type, _config.GameVersion)) return;
        _newest.Clear();
        _newestFor = (_config.Type, _config.GameVersion);
    }

    /// <summary>
    /// The newest build for each hash, from memory when it is recent and from the store otherwise.
    /// A hash the store gave no answer for is simply missing from the result.
    /// </summary>
    private async Task<Dictionary<string, VersionResult>> NewestAsync(IEnumerable<string> hashes,
        CancellationToken ct)
    {
        DropAnswersForAnotherServer();

        var wanted = hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var now = _clock.GetUtcNow();
        var stale = wanted.Where(h => !_newest.TryGetValue(h, out var known)
            || now - known.At > NewestLifetime).ToList();

        if (stale.Count > 0
            && await _store.GetLatestVersionsByHashAsync(stale, _config.Type, _config.GameVersion, ct)
                is { } answer)
        {
            foreach (var (hash, version) in answer) _newest[hash] = (version, now);
        }

        // Whatever is still stale after asking is out of reach, and stays out: reusing an old answer
        // is exactly what the lifetime is there to stop.
        return wanted.Where(h => _newest.TryGetValue(h, out var known) && now - known.At <= NewestLifetime)
            .ToDictionary(h => h, h => _newest[h].Version, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records what was just installed, so the state is right without asking the store again.
    /// </summary>
    /// <remarks>
    /// The jar's hash is not in the index until somebody asks about it, and asking can fail or come
    /// back empty — which would put "Install" back on the card of a mod that is on disk.
    /// </remarks>
    /// <param name="sha1">The SHA-1 of the file that was written.</param>
    /// <param name="version">The version it is.</param>
    /// <param name="isNewest">
    /// Whether it is what the store calls the newest build for this server: true when the install
    /// picked it, false for a version chosen by hand.
    /// </param>
    public void NoteInstalled(string sha1, VersionResult version, bool isNewest)
    {
        _index.Remember(sha1, version);

        // Before writing: a first answer must not be wiped by the check that it is for this server.
        DropAnswersForAnotherServer();
        if (isNewest) _newest[sha1] = (version, _clock.GetUtcNow());
    }
}
