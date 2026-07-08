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
    /// vanilla death log entry, or null. The message part (after the "]: " log prefix) must be
    /// "&lt;valid player name&gt; &lt;death phrase&gt;…": the phrase has to come <em>immediately</em>
    /// after the name, not merely appear somewhere in the line — so a plugin/chat line that only
    /// mentions a keyword ("the player Steve died in tutorial") doesn't fire a false death.
    /// </summary>
    public static string? Detect(string line)
    {
        var i = line.IndexOf("]: ", StringComparison.Ordinal);
        if (i < 0) return null;
        var msg = line[(i + 3)..].Trim();

        var space = msg.IndexOf(' ');
        if (space <= 0) return null; // needs "<name> <phrase>"
        var subject = msg[..space];
        if (!PlayerName().IsMatch(subject)) return null;

        var rest = msg[(space + 1)..];
        foreach (var phrase in Phrases)
            if (StartsWithPhrase(rest, phrase))
                return msg;
        return null;
    }

    // The phrase must be a whole leading token of <paramref name="rest"/>: immediately after it comes
    // a space or the end of the line. So "died" matches "Alice died" but not "died-worlds plugin".
    private static bool StartsWithPhrase(string rest, string phrase) =>
        rest.StartsWith(phrase, StringComparison.Ordinal) &&
        (rest.Length == phrase.Length || rest[phrase.Length] == ' ');
}
