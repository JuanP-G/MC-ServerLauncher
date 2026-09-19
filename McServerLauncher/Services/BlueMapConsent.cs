using System;
using System.IO;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// BlueMap's "you must accept the download" step, answered from the app instead of a config file.
/// </summary>
/// <remarks>
/// <para>
/// BlueMap draws its map with textures from the official Minecraft client, which Mojang distributes
/// and BlueMap will not fetch without explicit consent. Until then it prints a warning on every
/// start, draws nothing, and points at a line in <c>core.conf</c> to change by hand — which is the
/// warning most people would find in their console and not know what to do about.
/// </para>
/// <para>
/// The app notices the warning and asks. It does not accept on anybody's behalf: this is an
/// agreement to download Mojang's files, and saying yes to it belongs to the person running the
/// server. What the app saves them is finding the file, the line, the right spelling, and the reload.
/// </para>
/// </remarks>
internal static partial class BlueMapConsent
{
    /// <summary>The sentence BlueMap prints while it is waiting for consent.</summary>
    private const string Marker = "You must accept the required file download in order for BlueMap to work";

    /// <summary>Whether this console line is BlueMap waiting for consent.</summary>
    internal static bool IsAskingForConsent(string line) =>
        line.Contains(Marker, StringComparison.Ordinal);

    /// <summary>Where BlueMap keeps the setting, or null when neither place has the file.</summary>
    /// <remarks>
    /// Two places, because BlueMap ships two ways: as a mod it reads <c>config/bluemap/</c>, as a
    /// plugin <c>plugins/BlueMap/</c>.
    /// </remarks>
    internal static string? FindConfig(string serverFolder)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(serverFolder, "config", "bluemap", "core.conf"),
                     Path.Combine(serverFolder, "plugins", "BlueMap", "core.conf")
                 })
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    /// <summary>
    /// The config with the download accepted, or null when there is no "false" to turn into "true".
    /// </summary>
    /// <remarks>
    /// Only the one value changes; every comment and every other setting stays exactly as it was.
    /// Null rather than a guess when the line is not there in the expected shape — a file somebody
    /// has edited by hand is theirs, and writing a new line into HOCON the app cannot fully read
    /// could leave BlueMap refusing to load at all.
    /// </remarks>
    internal static string? Accept(string coreConf)
    {
        var accepted = AcceptDownload().Replace(coreConf, "${1}true", 1);
        return accepted == coreConf ? null : accepted;
    }

    // accept-download: false   (HOCON also allows "=", and any spacing around it)
    [GeneratedRegex(@"^(\s*accept-download\s*[:=]\s*)false\b", RegexOptions.Multiline)]
    private static partial Regex AcceptDownload();
}
