using System.IO;
using System.Text.RegularExpressions;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What a server's Floodgate puts in front of a Bedrock player's name, read from its own config.
/// </summary>
/// <remarks>
/// <para>
/// Floodgate's <c>username-prefix</c> is a dot unless the owner changed it, and the app used to
/// accept the dot and nothing else. On a server set to <c>*</c> — a common choice — a Bedrock player
/// was <c>*Steve</c> in the log, which is not a name the app knew: they never appeared in the player
/// list or the history.
/// </para>
/// <para>
/// The value is read, never written: it is the owner's setting. It is read from where each kind of
/// Floodgate keeps it — <c>plugins/floodgate/config.yml</c> for the plugin,
/// <c>config/floodgate/config.yml</c> for the mod — and only that one key, with a line match rather
/// than a YAML parser, which is all a single scalar needs.
/// </para>
/// </remarks>
public static partial class FloodgateConfig
{
    /// <summary>
    /// The prefix this server's Bedrock players carry in the log. The default dot when there is no
    /// Floodgate config to read; empty when their names need no special treatment or the configured
    /// prefix is one the app does not accept (see <see cref="PlayerNames.IsUsablePrefix"/>).
    /// </summary>
    public static string ReadUsernamePrefix(ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.FolderPath)) return PlayerNames.DefaultFloodgatePrefix;

        foreach (var relative in ConfigLocations)
        {
            var path = Path.Combine(config.FolderPath, relative);
            try
            {
                if (!File.Exists(path)) continue;
                if (PrefixIn(File.ReadAllText(path)) is { } prefix) return prefix;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held by the server this instant: the default is the best guess there is.
            }
        }

        return PlayerNames.DefaultFloodgatePrefix;
    }

    private static readonly string[] ConfigLocations =
    [
        Path.Combine("plugins", "floodgate", "config.yml"),
        Path.Combine("config", "floodgate", "config.yml")
    ];

    /// <summary>
    /// The prefix written in a Floodgate config, as the app can use it; null when the key is absent.
    /// </summary>
    /// <remarks>
    /// Empty for a prefix made of letters, digits or underscores (<c>BE_</c>): the name that results
    /// is already an ordinary one. Empty too for one the app will not accept — the names are then
    /// simply not recognised, which is what happened before this existed.
    /// </remarks>
    internal static string? PrefixIn(string yaml)
    {
        var match = PrefixLine().Match(yaml);
        if (!match.Success) return null;

        var value = match.Groups["dq"].Success ? match.Groups["dq"].Value
            : match.Groups["sq"].Success ? match.Groups["sq"].Value
            : match.Groups["bare"].Value.Trim();

        return PlayerNames.IsUsablePrefix(value) ? value : string.Empty;
    }

    // username-prefix: "."   |   username-prefix: '*'   |   username-prefix: .   (with an optional comment)
    [GeneratedRegex("""^[ \t]*username-prefix[ \t]*:[ \t]*(?:"(?<dq>[^"\r\n]*)"|'(?<sq>[^'\r\n]*)'|(?<bare>[^#\r\n]*))""",
        RegexOptions.Multiline)]
    private static partial Regex PrefixLine();
}
