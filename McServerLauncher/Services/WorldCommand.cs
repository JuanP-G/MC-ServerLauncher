namespace McServerLauncher.Services;

/// <summary>
/// Whether a command typed into the console can change the world's files.
/// </summary>
/// <remarks>
/// <para>
/// A server that ran with nobody on it does not need its world backed up again on the way down —
/// unless somebody changed it from the console: a <c>/fill</c>, a <c>/time set</c>, a plugin's
/// command. Typing <c>list</c> to see who is on is not that, and should not cost a backup.
/// </para>
/// <para>
/// So the list here is of what is known <em>not</em> to touch the world — talking, looking, and
/// the bans, ops and whitelist, which live in the server folder rather than in the world.
/// Everything else counts, including any command this app has never heard of: a plugin's command
/// can do anything, and a copy too many is cheaper than a change left out of the backups.
/// </para>
/// </remarks>
public static class WorldCommand
{
    private static readonly HashSet<string> LeavesWorldAlone = new(StringComparer.OrdinalIgnoreCase)
    {
        // Asking
        "help", "?", "list", "seed", "banlist", "tps", "mspt", "version", "ver", "about",
        "plugins", "pl", "perf", "debug", "jfr",
        // Talking
        "say", "tell", "msg", "w", "me", "tellraw", "teammsg", "tm", "title",
        // Who may play: files in the server folder, not the world
        "ban", "ban-ip", "pardon", "pardon-ip", "kick", "op", "deop", "whitelist",
        "transfer", "setidletimeout",
        // Saving writes what is already there; stopping is the stop's own business
        "save-all", "save-on", "save-off", "stop",
    };

    /// <summary>
    /// Commands that only change something with a subcommand; these are the ones that only ask.
    /// </summary>
    private static readonly HashSet<string> OnlyAsks = new(StringComparer.OrdinalIgnoreCase)
    {
        "time query", "datapack list",
    };

    /// <summary>
    /// True when <paramref name="command"/> may have changed the world, as far as can be told.
    /// </summary>
    public static bool Touches(string command)
    {
        var words = command.Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;   // nothing was sent

        var name = words[0];
        if (name.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
            name = name["minecraft:".Length..];

        if (LeavesWorldAlone.Contains(name)) return false;
        return !(words.Length > 1 && OnlyAsks.Contains(name + " " + words[1]));
    }
}
