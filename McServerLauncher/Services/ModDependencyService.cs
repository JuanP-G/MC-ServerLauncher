using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;

namespace McServerLauncher.Services;

/// <summary>
/// The library mods almost every mod needs, and that nobody remembers to install by hand.
/// </summary>
/// <remarks>
/// <para>
/// The symptom is the Fabric loader refusing to start with a list like <em>"Explorify needs any
/// version of fabric-api, which you do not have"</em> and <em>"Towns and Towers needs cristallib
/// 3.1.3 or later"</em>. Nothing was broken: the app installed exactly the jar that was asked for
/// and none of the ones it depends on.
/// </para>
/// <para>
/// Modrinth publishes those dependencies per version, so they can be resolved rather than guessed.
/// Two things are worth knowing about that data. Dependencies carry <strong>no version range</strong>
/// — a dependency either pins one exact version id or names a project and takes whatever is
/// current — which is why "the project is already installed" is a complete answer here and not an
/// approximation. And <c>embedded</c> means the dependency is bundled inside the jar already, so
/// installing it again is how you get two copies of the same mod and a different startup failure.
/// </para>
/// <para>
/// Only <c>required</c> is installed. Optional dependencies are suggestions, and pulling them in
/// would quietly fill the folder with mods nobody chose.
/// </para>
/// <para>
/// Modrinth is not the only source, because it is not always right. Checked on 2026-08-30: Explorify
/// v1.6.5 declares <em>no dependencies at all</em> on Modrinth, while its own
/// <c>fabric.mod.json</c> says <c>"fabric-api": "*"</c> — and fabric-api is exactly what the loader
/// refused to start without. So the jar is read as well, and it is the authority on what it needs.
/// </para>
/// </remarks>
public class ModDependencyService
{
    /// <summary>
    /// A cap on the walk. No real mod is anywhere near this deep; it exists so that a cycle or a
    /// mistake in the data cannot turn one click into an unbounded download.
    /// </summary>
    private const int MaxInstalls = 50;

    private readonly ModrinthService _modrinth;

    public ModDependencyService(ModrinthService? modrinth = null) => _modrinth = modrinth ?? new ModrinthService();

    /// <summary>One dependency that has to be downloaded, already resolved to a concrete file.</summary>
    public record Needed(string ProjectId, VersionResult Version, VersionFile File)
    {
        /// <summary>What to show the user. The file name is the only name we have without another request.</summary>
        public string Label => System.IO.Path.GetFileName(File.Filename);
    }

    /// <summary>
    /// What is missing, and what could not be worked out.
    /// </summary>
    /// <param name="Install">Dependencies to download, parents before children.</param>
    /// <param name="Unresolved">
    /// Project ids that are required but have no version for this loader and Minecraft version.
    /// Reported rather than swallowed: the install will still be short of something, and a user who
    /// is told which mod is missing can go and look at it.
    /// </param>
    public record Plan(IReadOnlyList<Needed> Install, IReadOnlyList<string> Unresolved)
    {
        public bool IsEmpty => Install.Count == 0 && Unresolved.Count == 0;
    }

    /// <summary>
    /// The required dependencies of <paramref name="roots"/>, transitively, minus what is already
    /// installed.
    /// </summary>
    public Task<Plan> ResolveMissingAsync(IEnumerable<VersionResult> roots, ServerType loader, string mcVersion,
        IEnumerable<string> installedProjectIds, CancellationToken ct = default) =>
        WalkAsync(roots, installedProjectIds,
            (projectId, versionId, token) => versionId is { Length: > 0 }
                ? _modrinth.GetVersionAsync(versionId, token)
                : projectId is { Length: > 0 }
                    ? _modrinth.GetLatestProjectVersionAsync(projectId, loader, mcVersion, token)
                    : Task.FromResult<VersionResult?>(null),
            ct);

    /// <summary>
    /// The same, for mod ids read out of a jar rather than project ids given by Modrinth.
    /// </summary>
    /// <remarks>
    /// A mod id is not a Modrinth project id. Most library mods use the same word for both
    /// (<c>fabric-api</c> is the clear case), and the ones that do not — cristallib is published as
    /// <c>cristel-lib</c> — simply do not resolve. That is why nothing here is reported as
    /// unresolved: an id Modrinth has never heard of is not evidence that anything is missing, and
    /// saying so would turn a guess into a warning.
    /// </remarks>
    public async Task<Plan> ResolveByModIdAsync(IEnumerable<string> modIds, ServerType loader, string mcVersion,
        IEnumerable<string> installedProjectIds, CancellationToken ct = default)
    {
        var root = ModIdRoot(modIds);
        if (root.Dependencies.Count == 0) return new Plan(Array.Empty<Needed>(), Array.Empty<string>());

        return WithoutUnresolved(
            await ResolveMissingAsync(new[] { root }, loader, mcVersion, installedProjectIds, ct));
    }

    /// <summary>
    /// A root that exists only to hang mod ids off.
    /// </summary>
    /// <remarks>
    /// Its own id is not a project id and cannot be one: Modrinth ids have no spaces in them, so
    /// this can never collide with a real project and be mistaken for something installed.
    /// </remarks>
    internal static VersionResult ModIdRoot(IEnumerable<string> modIds)
    {
        var root = new VersionResult { ProjectId = "jar metadata" };
        foreach (var id in modIds.Where(i => !string.IsNullOrWhiteSpace(i))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
            root.Dependencies.Add(new VersionDependency { ProjectId = id, DependencyType = "required" });

        return root;
    }

    /// <summary>The same plan with nothing reported as missing. See the remarks above for why.</summary>
    internal static Plan WithoutUnresolved(Plan plan) => new(plan.Install, Array.Empty<string>());

    /// <summary>
    /// The hard dependencies a Fabric mod declares in its own metadata. Empty for anything else.
    /// </summary>
    /// <remarks>
    /// <c>depends</c> only — <c>recommends</c> and <c>suggests</c> are the author's advice, not a
    /// requirement. The loader itself is dropped: <c>minecraft</c>, <c>java</c> and
    /// <c>fabricloader</c> are provided, and asking Modrinth for them would be nonsense.
    /// Plugins have no such file, so this reads nothing and costs one failed lookup inside the jar.
    /// </remarks>
    internal static IReadOnlyList<string> DeclaredModIds(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry is null) return Array.Empty<string>();

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("depends", out var depends)
                || depends.ValueKind != JsonValueKind.Object)
                return Array.Empty<string>();

            return depends.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => !LoaderProvided.Contains(name))
                .ToList();
        }
        catch
        {
            // Unreadable metadata is not a reason to refuse the install: the jar is already
            // verified and on disk, and Modrinth's own dependency list still applies.
            return Array.Empty<string>();
        }
    }

    /// <summary>Ids that the loader supplies itself and that no download could satisfy.</summary>
    private static readonly HashSet<string> LoaderProvided =
        new(StringComparer.OrdinalIgnoreCase) { "minecraft", "java", "fabricloader", "fabric" };

    /// <summary>
    /// The walk itself, with the lookup passed in.
    /// </summary>
    /// <remarks>
    /// Separated from the network so the part with the actual decisions in it — which dependency
    /// types count, what "already satisfied" means, and what stops a cycle — can be tested against
    /// a table instead of against Modrinth on the day the test runs.
    /// </remarks>
    internal static async Task<Plan> WalkAsync(
        IEnumerable<VersionResult> roots,
        IEnumerable<string> installedProjectIds,
        Func<string?, string?, CancellationToken, Task<VersionResult?>> resolve,
        CancellationToken ct = default)
    {
        var install = new List<Needed>();
        var unresolved = new List<string>();

        // Everything already accounted for: installed, being installed, or looked at and rejected.
        // A single set is what keeps a dependency cycle (A needs B, B needs A) from looping.
        var settled = new HashSet<string>(installedProjectIds, StringComparer.OrdinalIgnoreCase);

        var pending = new Queue<VersionResult>();
        foreach (var root in roots)
        {
            settled.Add(root.ProjectId);
            pending.Enqueue(root);
        }

        while (pending.Count > 0 && install.Count < MaxInstalls)
        {
            ct.ThrowIfCancellationRequested();
            var version = pending.Dequeue();

            foreach (var dep in version.Dependencies)
            {
                // "optional" is a suggestion; "incompatible" is a warning; "embedded" is already
                // inside the jar we just downloaded, and installing it again is its own bug.
                if (!string.Equals(dep.DependencyType, "required", StringComparison.OrdinalIgnoreCase)) continue;

                if (dep.ProjectId is { Length: > 0 } && settled.Contains(dep.ProjectId)) continue;

                var resolved = await resolve(dep.ProjectId, dep.VersionId, ct);
                var file = resolved?.Files.FirstOrDefault(f => f.Primary) ?? resolved?.Files.FirstOrDefault();

                if (resolved is null || file is null)
                {
                    // Mark it settled anyway: asking again on the next parent that needs it would
                    // repeat a request that has already failed, and report it twice.
                    var name = dep.ProjectId ?? dep.FileName ?? dep.VersionId;
                    if (name is { Length: > 0 } && settled.Add(name)) unresolved.Add(name);
                    continue;
                }

                // The id is taken from the resolved version, not from the dependency: a pinned
                // version id carries no project id of its own, and without this the same project
                // could be queued twice under two different keys.
                if (!settled.Add(resolved.ProjectId)) continue;
                if (dep.ProjectId is { Length: > 0 }) settled.Add(dep.ProjectId);

                install.Add(new Needed(resolved.ProjectId, resolved, file));
                pending.Enqueue(resolved);

                if (install.Count >= MaxInstalls) break;
            }
        }

        return new Plan(install, unresolved);
    }
}
