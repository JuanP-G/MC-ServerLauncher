using System;
using System.Collections.Generic;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Whether every installed mod and plugin has what it needs, answered from the jars alone.
/// </summary>
/// <remarks>
/// <para>
/// This runs before every start, so it has to be fast and it has to be right when offline. Both
/// follow from asking the jars instead of the store: opening N zip files takes milliseconds, needs
/// no network, and cannot be wrong because a store page forgot to list a dependency — which is not
/// hypothetical, it is the exact shape of the crash this was written for.
/// </para>
/// <para>
/// Satisfied means "something installed provides that name", not "some version range is met".
/// Ranges are deliberately not checked: the declared ranges are frequently wrong in both directions,
/// and a start blocked by a range nobody can verify would be a check people learn to click through.
/// A name that is simply not there is unambiguous, and it is what the loader itself refuses to start
/// over.
/// </para>
/// </remarks>
public static class ContentDependencyCheck
{
    /// <summary>Something needed that nothing installed provides.</summary>
    /// <param name="Id">The id or plugin name that is missing.</param>
    /// <param name="NeededBy">The jars waiting on it, by file name.</param>
    public record Missing(string Id, IReadOnlyList<string> NeededBy);

    /// <summary>What a folder full of jars is missing. Empty when everything is satisfied.</summary>
    public static IReadOnlyList<Missing> Check(IEnumerable<ContentManifest.Manifest> installed)
    {
        var manifests = installed as IList<ContentManifest.Manifest> ?? installed.ToList();

        var provided = new HashSet<string>(
            manifests.SelectMany(m => m.Provides),
            StringComparer.OrdinalIgnoreCase);

        // Grouped by what is missing rather than by who is missing it: three mods all waiting on
        // Fabric API is one thing to install, and listing it three times would make a short problem
        // look like a long one.
        return manifests
            .SelectMany(m => m.Requires.Select(id => (Id: id, By: m.FileName)))
            .Where(x => !provided.Contains(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Missing(g.Key, g.Select(x => x.By).Distinct().OrderBy(n => n).ToList()))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>What a server's content folder is missing.</summary>
    public static IReadOnlyList<Missing> CheckServer(ServerConfig config) =>
        Check(ContentManifest.ReadFolder(ContentManifest.FolderOf(config)));

    /// <summary>
    /// Whether to stop and ask the user about what is missing.
    /// </summary>
    /// <remarks>
    /// An automatic start is never interrupted. Auto-restart after a crash and wake-on-demand both
    /// happen with nobody in front of the app, so a dialog would leave the server down until
    /// somebody came back — which is the exact opposite of what those two features are for. The
    /// same guard the port and path checks already use, written where it can be tested.
    /// </remarks>
    public static bool ShouldAsk(int missingCount, bool isAutoRestart) =>
        missingCount > 0 && !isAutoRestart;
}
