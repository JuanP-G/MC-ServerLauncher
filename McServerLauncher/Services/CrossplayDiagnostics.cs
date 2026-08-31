namespace McServerLauncher.Services;

/// <summary>
/// Recognising, in the server's own console, the one crossplay failure the launcher cannot prevent.
/// </summary>
/// <remarks>
/// <para>
/// Geyser joins the Java server as an ordinary client with no mods. Geyser's own documentation puts
/// the rule plainly — if a vanilla client can join the server, so can Geyser — and the reverse is
/// what bites: a NeoForge server carrying mods the client is required to have rejects that
/// connection during the configuration phase, before the player ever reaches the world.
/// </para>
/// <para>
/// Nothing in the launcher can fix that. What it can do is stop the failure from being a mystery.
/// The kick arrives as one English line buried in a few thousand others, saying the player should
/// install NeoForge — advice that makes no sense to somebody holding a phone. Catching that line
/// and answering it in the app's own language is the whole purpose of this class.
/// </para>
/// </remarks>
internal static class CrossplayDiagnostics
{
    /// <summary>
    /// Fragments of the loader's rejection message, lower-cased.
    /// </summary>
    /// <remarks>
    /// Matched as fragments rather than whole lines because the version number sits in the middle
    /// of the sentence, and the log prefix and player name sit in front of it. These stay in
    /// English: the server writes disconnect reasons from its own language file, which is en_us
    /// regardless of the locale the rest of the console appears in.
    /// </remarks>
    private static readonly string[] Rejections =
    {
        "running neoforge, but you are not",
        "please install neoforge",
        "require forge to be installed on the client",
    };

    /// <summary>
    /// Whether <paramref name="line"/> is the server turning away a client for having no mods.
    /// </summary>
    /// <remarks>
    /// "lost connection" is required as well as the loader's wording so that a line merely quoting
    /// the message — someone pasting the error into chat, or the launcher's own explanation of it —
    /// is not mistaken for the event itself.
    /// </remarks>
    internal static bool IsModdedClientRejection(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;

        var lower = line.ToLowerInvariant();
        if (!lower.Contains("lost connection")) return false;

        foreach (var marker in Rejections)
            if (lower.Contains(marker)) return true;

        return false;
    }
}
