using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>What a player did, as far as the history is concerned.</summary>
public enum PlayerEventKind
{
    Join,
    Leave,
    Chat,
    Death,
    Advancement
}

/// <summary>One thing a player did, read from a console line.</summary>
/// <param name="Kind">What it was.</param>
/// <param name="Player">Who did it.</param>
/// <param name="Text">What they said, how they died, or which advancement; null for joins and leaves.</param>
public sealed record PlayerEvent(PlayerEventKind Kind, string Player, string? Text);

/// <summary>
/// Turns a server log line into something a player did.
/// </summary>
/// <remarks>
/// <para>
/// One reader for the live console and for importing old logs, so the two cannot come to
/// different conclusions about the same line. It is built from the console's own detectors —
/// <see cref="ConsoleLineClassifier.NameBefore"/>, <see cref="ConsoleLineClassifier.ChatOf"/> and
/// <see cref="DeathMessageDetector"/> — and so inherits their rule: quoting something in chat never
/// makes it look like that something happened.
/// </para>
/// <para>
/// The login line, <c>Bob[/1.2.3.4:5678] logged in with entity id…</c>, is never read. It is the one
/// that carries the player's IP address, and the history does not keep IP addresses.
/// </para>
/// </remarks>
public static partial class PlayerEventParser
{
    /// <summary>What a player did in <paramref name="line"/>, or null if it is not about a player.</summary>
    /// <param name="line">The line as the server wrote it.</param>
    /// <param name="online">Who is connected, to recognise a player's <c>/say</c> and <c>/me</c>.</param>
    public static PlayerEvent? Parse(string line, IReadOnlySet<string>? online)
    {
        if (ConsoleLineClassifier.ChatOf(line, online) is { } chat)
        {
            // "[Server] …" is the console talking, not a player. (Someone actually called Server
            // would lose their chat from the history; a price worth paying not to invent a player.)
            return !ConsoleLineClassifier.IsServerSender(chat.Sender)
                ? new PlayerEvent(PlayerEventKind.Chat, chat.Sender, chat.Message)
                : null;
        }

        if (ConsoleLineClassifier.NameBefore(line, " joined the game") is { } joined)
            return new PlayerEvent(PlayerEventKind.Join, joined, null);

        if (ConsoleLineClassifier.NameBefore(line, " left the game") is { } left)
            return new PlayerEvent(PlayerEventKind.Leave, left, null);

        if (Advancement().Match(Body(line) ?? "") is { Success: true } adv)
            return new PlayerEvent(PlayerEventKind.Advancement, adv.Groups[1].Value, adv.Groups[2].Value);

        if (DeathMessageDetector.Detect(line) is { } death)
        {
            var space = death.IndexOf(' ');
            return space > 0 ? new PlayerEvent(PlayerEventKind.Death, death[..space], death) : null;
        }

        return null;
    }

    /// <summary>The player and UUID in <c>UUID of player Bob is …</c>, or null.</summary>
    public static (string Player, string Uuid)? UuidOf(string line)
    {
        var m = UuidLine().Match(line);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant()) : null;
    }

    private static string? Body(string line)
    {
        var i = line.IndexOf("]: ", StringComparison.Ordinal);
        return i < 0 ? null : line[(i + 3)..];
    }

    // Anchored to the start of the message, like every other detector: a chat line quoting it has
    // "<Bob> " in front and does not match.
    [GeneratedRegex(@"^([A-Za-z0-9_]{1,16}) has (?:made the advancement|completed the challenge|reached the goal) \[(.+)\]\s*$")]
    private static partial Regex Advancement();

    [GeneratedRegex(@"\]: UUID of player ([A-Za-z0-9_]{1,16}) is ([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*$")]
    private static partial Regex UuidLine();
}
