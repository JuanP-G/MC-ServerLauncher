using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Detects Minecraft death/kill messages in a server console line, for the "deaths" notification.
/// Best-effort and English/vanilla-oriented: the server's locale or plugins may word some deaths
/// differently. Pairing a death-phrase match with a "subject is a valid player name" check keeps it
/// from firing on chat lines (which start with a "&lt;name&gt;" tag) or other log output.
/// </summary>
public static partial class DeathMessageDetector
{
    // Common vanilla death-message fragments (the victim is the subject at the start of the line).
    private static readonly string[] Phrases =
    {
        "was slain by", "was shot by", "was pummeled by", "was killed by", "was blown up by",
        "was fireballed by", "was stung to death", "was squashed by", "was impaled", "was skewered",
        "was poked to death", "was pricked to death", "was shot", "was killed while", "was killed trying",
        "hit the ground too hard", "fell from a high place", "fell off", "fell out of the world",
        "was doomed to fall", "fell too far", "was struck by lightning", "drowned", "blew up",
        "went up in flames", "burned to death", "was burnt to a crisp", "walked into fire",
        "walked into a cactus", "tried to swim in lava", "discovered the floor was lava",
        "starved to death", "suffocated", "was squished", "withered away", "was frozen to death",
        "was killed by magic", "was roasted in dragon's breath", "left the confines of this world",
        "didn't want to live", "experienced kinetic energy", "died"
    };

    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();

    /// <summary>
    /// Returns the death message (e.g. "Alice was slain by Bob") if <paramref name="line"/> is a
    /// vanilla death log entry, or null. The message part (after the "]: " log prefix) must start
    /// with a valid player name and contain a known death phrase.
    /// </summary>
    public static string? Detect(string line)
    {
        var i = line.IndexOf("]: ", StringComparison.Ordinal);
        if (i < 0) return null;
        var msg = line[(i + 3)..].Trim();
        if (msg.Length == 0) return null;

        var space = msg.IndexOf(' ');
        var subject = space > 0 ? msg[..space] : msg;
        if (!PlayerName().IsMatch(subject)) return null;

        foreach (var phrase in Phrases)
            if (msg.Contains(phrase, StringComparison.Ordinal))
                return msg;
        return null;
    }
}
