using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// The first two steps every content installer takes: refuse a server type it cannot serve, then
/// make sure the content folder exists.
/// </summary>
/// <remarks>
/// <para>
/// Crossplay, Hydraulic and the multi-version plugins each opened with the same five lines, apart
/// from the message key. Three copies is where the fourth installer copies one of them and the
/// four start to differ.
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
            throw new InvalidOperationException(
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
    /// <param name="display">What to call it in the log.</param>
    /// <param name="log">Progress, for the console.</param>
    /// <param name="download">Writes the verified jar to the path it is given.</param>
    /// <exception cref="InvalidOperationException">An old copy is in use; nothing was changed.</exception>
    public static async Task InstallReplacingAsync(string folder, string fileName, string display,
        IProgress<string>? log, Func<string, Task> download)
    {
        var path = AtomicDownload.PathIn(folder, fileName);
        var replacesAFile = File.Exists(path);

        await download(path);

        ReplacePrevious(folder, path, display, log, canRollBack: !replacesAFile);
    }

    /// <summary>Takes out every other copy of <paramref name="newJar"/>, or throws having changed nothing.</summary>
    /// <param name="folder">The content folder.</param>
    /// <param name="newJar">The jar just installed.</param>
    /// <param name="display">What to call it in the log and in the error.</param>
    /// <param name="log">Progress, for the console.</param>
    /// <param name="canRollBack">
    /// Whether the new jar may be deleted if the old ones cannot. Not when it overwrote a file of the
    /// same name: then it is the only copy left.
    /// </param>
    internal static IReadOnlyList<string> ReplacePrevious(string folder, string newJar, string display,
        IProgress<string>? log, bool canRollBack = true)
    {
        var others = ContentReplacement.OthersOf(folder, newJar);
        if (others.Count == 0) return others;

        var (result, removed) = ContentReplacement.RemoveAllOrNothing(others, canRollBack ? newJar : null);
        if (result == ContentReplacement.RemoveResult.Locked)
            throw new InvalidOperationException(string.Format(Localizer.Get("Msg_ContentInUseFmt"), display));

        log?.Report(string.Format(Localizer.Get("Msg_ContentReplacedFmt"), display,
            string.Join(", ", removed.Select(ContentReplacement.DisplayName))));
        return removed;
    }
}
