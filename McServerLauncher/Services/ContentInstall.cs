using System.IO;
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
}
