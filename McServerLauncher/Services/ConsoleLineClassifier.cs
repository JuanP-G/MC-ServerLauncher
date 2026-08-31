using System;
using System.Text.RegularExpressions;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What a console line is about: its severity, and whether it is chat, a player event, or the app.
/// </summary>
/// <remarks>
/// <para>
/// The order matters, and it runs from what is certain to what is inferred. The source is certain —
/// the app knows when it is the one talking, and the operating system knows what came out of
/// standard error. Only the server's standard output has to be read, and only then is a mistake
/// possible.
/// </para>
/// <para>
/// This is why the app's own messages are tagged where they are raised instead of being recognised
/// here: their text is localized, prefixes and all — <c>[Launcher]</c>, <c>[Error]</c> and
/// <c>[Players]</c> live inside the resx values and are translated with them. A classifier keyed on
/// those markers would work in Spanish and quietly stop working in German.
/// </para>
/// </remarks>
public static partial class ConsoleLineClassifier
{
    /// <summary>What a line is about, given where it came from.</summary>
    public static ConsoleLineKind Classify(string text, ConsoleSource source)
    {
        if (source == ConsoleSource.Launcher) return ConsoleLineKind.Launcher;

        // Standard error is the server telling the operating system something went wrong. It is the
        // one severity signal that needs no parsing and cannot be reworded by a plugin or a locale.
        if (source == ConsoleSource.Stderr) return ConsoleLineKind.Error;

        if (text.Length == 0) return ConsoleLineKind.Info;

        // A stack trace's continuation lines carry no level of their own — they are indented, or
        // start with "at " or "Caused by:". Left alone they read as ordinary output, so the one
        // line that says what broke ends up buried in forty that look routine.
        if (IsStackTrace(text)) return ConsoleLineKind.Error;

        var level = LevelOf(text);
        if (level is ConsoleLineKind.Warn or ConsoleLineKind.Error) return level;

        // From here it is ordinary output, and worth splitting further. The order reads as though it
        // were what stops a quoted join message from counting as a join, and it is not: both
        // detectors anchor on the start of the message, so "<Bob> Alice joined the game" fails the
        // player check on its own — the name they find is "<Bob> Alice", which is not a name. Said
        // out loud because a comment claiming the order protects it would send the next person
        // looking in the wrong place the day it stops working.
        if (IsChat(text)) return ConsoleLineKind.Chat;
        if (IsPlayerEvent(text)) return ConsoleLineKind.Players;

        return ConsoleLineKind.Info;
    }

    /// <summary>
    /// The level in the log prefix, or <see cref="ConsoleLineKind.Info"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Two shapes, because the servers people actually run write two. Vanilla and the mod loaders
    /// use <c>[HH:mm:ss] [Server thread/INFO]:</c>; Paper and its descendants use
    /// <c>[HH:mm:ss INFO]:</c>. Reading only one of them would leave every Paper warning grey, which
    /// is the type most people would be looking at.
    /// </remarks>
    internal static ConsoleLineKind LevelOf(string text)
    {
        // Searched in the prefix, not in the message. Most of the time this changes nothing — the
        // real prefix carries a level and is the first thing the search finds — but when it does not
        // carry one, an "[…/ERROR]" quoted further along the line would be picked up instead and an
        // ordinary message would be painted as a failure.
        //
        // Bounded rather than anchored: vanilla puts the level in the *second* bracket, so requiring
        // it at the start of the line would miss it entirely and leave every vanilla warning grey.
        var end = text.IndexOf("]: ", StringComparison.Ordinal);
        var prefix = end < 0 ? text : text[..(end + 1)];

        var match = LogLevel().Match(prefix);
        if (!match.Success) return ConsoleLineKind.Info;

        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "WARN" or "WARNING" => ConsoleLineKind.Warn,
            "ERROR" or "SEVERE" or "FATAL" => ConsoleLineKind.Error,
            _ => ConsoleLineKind.Info
        };
    }

    /// <summary>Whether the line is a player talking to the other players.</summary>
    /// <remarks>
    /// The <c>&lt;name&gt;</c> tag has to come immediately after the log prefix, and the name has to
    /// be a real one. That anchoring is what does the work: chat is the one thing on a server that
    /// can contain <em>any</em> text at all, including a perfect copy of a join message or a stack
    /// trace, and quoting something must never make it look like that something happened.
    /// </remarks>
    internal static bool IsChat(string text)
    {
        var message = MessageBody(text);
        if (message is null || message.Length < 3 || message[0] != '<') return false;

        var close = message.IndexOf('>');
        if (close <= 1) return false;

        return PlayerName().IsMatch(message[1..close]);
    }

    /// <summary>Whether somebody joined, left or died.</summary>
    internal static bool IsPlayerEvent(string text) =>
        NameBefore(text, " joined the game") is not null
        || NameBefore(text, " left the game") is not null
        || DeathMessageDetector.Detect(text) is not null;

    /// <summary>
    /// Extracts the player name right before a marker, from a real log entry only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name must be the <em>only</em> text between the log prefix and the marker, and a valid
    /// Minecraft name. That is what stops <c>&lt;Bob&gt; Alice joined the game</c> — Bob typing the
    /// sentence in chat — from counting as Alice joining.
    /// </para>
    /// <para>
    /// Lives here rather than in the view model that used to own it, because two things now need it
    /// and both were carrying their own copy of the same regex: the player list, and this. Fixing a
    /// misread name in one of them and not the other is exactly the kind of drift that is invisible
    /// until somebody's name stops working.
    /// </para>
    /// </remarks>
    public static string? NameBefore(string line, string marker)
    {
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx <= 0) return null;

        var head = line[..idx];
        var colon = head.LastIndexOf(": ", StringComparison.Ordinal);
        var name = colon >= 0 ? head[(colon + 2)..] : head;

        return PlayerName().IsMatch(name) ? name : null;
    }

    /// <summary>The message after the log prefix, or null when there is no prefix.</summary>
    private static string? MessageBody(string text)
    {
        var i = text.IndexOf("]: ", StringComparison.Ordinal);
        return i < 0 ? null : text[(i + 3)..].TrimStart();
    }

    private static bool IsStackTrace(string text) =>
        text.StartsWith('\t')
        || text.StartsWith("    at ", StringComparison.Ordinal)
        || text.TrimStart().StartsWith("at ", StringComparison.Ordinal)
        || text.TrimStart().StartsWith("Caused by:", StringComparison.Ordinal)
        || text.TrimStart().StartsWith("... ", StringComparison.Ordinal);

    // [12:34:56] [Server thread/WARN]:  and  [12:34:56 WARN]:  — the two shapes in the wild.
    [GeneratedRegex(@"\[[^\]]*[\s/](INFO|WARN|WARNING|ERROR|SEVERE|FATAL)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LogLevel();

    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();
}
