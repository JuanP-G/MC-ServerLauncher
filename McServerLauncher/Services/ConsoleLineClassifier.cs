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
    /// <summary>What a line is about, given where it came from and what came before it.</summary>
    /// <param name="text">The line, as the server wrote it.</param>
    /// <param name="source">Which stream it arrived on.</param>
    /// <param name="previous">
    /// What the last line on standard output was. A line with no log prefix of its own is the next
    /// line of that entry — a list, a multi-line warning, an exception message — and it belongs to
    /// the same entry rather than being judged on its own.
    /// </param>
    /// <param name="online">
    /// Who is connected right now, when the caller knows. Only needed to tell a player's
    /// <c>/say</c> (<c>[Alice] …</c>) from a plugin logging under its own name (<c>[LuckPerms] …</c>);
    /// without it those stay ordinary output. See <see cref="ChatOf"/>.
    /// </param>
    public static ConsoleLineKind Classify(string text, ConsoleSource source, ConsoleLineKind? previous = null,
        IReadOnlySet<string>? online = null)
    {
        if (source == ConsoleSource.Launcher) return ConsoleLineKind.Launcher;

        if (text.Length == 0) return ConsoleLineKind.Info;

        // Standard error is the server telling the operating system something went wrong. It is the
        // one severity signal that needs no parsing and cannot be reworded by a plugin or a locale —
        // with one exception whose format is fixed by the JVM itself: its own "WARNING:" lines,
        // about restricted or deprecated methods. They are warnings, and painting them red made a
        // healthy start look like a failing one.
        if (source == ConsoleSource.Stderr)
            return text.StartsWith("WARNING:", StringComparison.Ordinal) ? ConsoleLineKind.Warn : ConsoleLineKind.Error;

        // A stack frame is an error wherever it turns up. Recognised by its shape — "at x.y(File:1)",
        // "Caused by:", "... 12 more" — and not by indentation, which is what it used to go by: the
        // Fabric loader indents its whole list of mods with tabs, and every mod on the server came
        // out red as though it had crashed.
        if (IsStackFrame(text)) return ConsoleLineKind.Error;

        // No log prefix: the next line of the entry above. Fabric's mod list, the lines under
        // "Warnings were found!", Distant Horizons's five-line warning, the message of an exception.
        // Judged on its own each one read as plain output, so a warning's explanation came out grey
        // and an exception's message came out as though nothing had happened.
        if (!HasLogPrefix(text))
            return previous is ConsoleLineKind.Warn or ConsoleLineKind.Error ? previous.Value : ConsoleLineKind.Info;

        var level = LevelOf(text);
        if (level is ConsoleLineKind.Warn or ConsoleLineKind.Error) return level;

        // From here it is ordinary output, and worth splitting further. The order reads as though it
        // were what stops a quoted join message from counting as a join, and it is not: both
        // detectors anchor on the start of the message, so "<Bob> Alice joined the game" fails the
        // player check on its own — the name they find is "<Bob> Alice", which is not a name. Said
        // out loud because a comment claiming the order protects it would send the next person
        // looking in the wrong place the day it stops working.
        if (ChatOf(text, online) is not null) return ConsoleLineKind.Chat;
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
    internal static bool IsChat(string text) => ChatOf(text, online: null) is not null;

    /// <summary>Who said what, if the line is a message to the other players; null otherwise.</summary>
    /// <remarks>
    /// <para>
    /// Every shape the server logs a message in:
    /// <c>&lt;Bob&gt; hola</c> (chat), <c>[Not Secure] &lt;Bob&gt; hola</c> (chat without a signature,
    /// 1.19.1 onwards), <c>[Server] hola</c> and <c>[Rcon] hola</c> (<c>say</c> from the console or
    /// RCON), <c>[@] hola</c> (a command block), <c>[Alice] hola</c> (a player's <c>/say</c>) and
    /// <c>* Alice saluda</c> (<c>/me</c>).
    /// </para>
    /// <para>
    /// The last two only when <paramref name="online"/> has that player in it. On Paper every
    /// plugin logs as <c>[PluginName] …</c>, and a plugin name is a perfectly good player name:
    /// accepting any bracketed name would paint half of a Paper start as chat.
    /// </para>
    /// </remarks>
    internal static (string Sender, string Message)? ChatOf(string text, IReadOnlySet<string>? online)
    {
        var message = MessageBody(text);
        if (message is null) return null;

        if (message.StartsWith(NotSecure, StringComparison.Ordinal))
            message = message[NotSecure.Length..];

        if (message.Length < 3) return null;

        if (message[0] == '<')
        {
            var close = message.IndexOf('>');
            if (close <= 1) return null;
            var name = message[1..close];
            return PlayerName().IsMatch(name) ? (name, message[(close + 1)..].TrimStart()) : null;
        }

        if (message[0] == '[')
        {
            var close = message.IndexOf("] ", StringComparison.Ordinal);
            if (close <= 1) return null;
            var name = message[1..close];
            var said = message[(close + 2)..];
            if (ServerSenders.Contains(name)) return (name, said);
            return IsOnline(name, online) ? (name, said) : null;
        }

        if (message.StartsWith("* ", StringComparison.Ordinal))
        {
            var rest = message[2..];
            var space = rest.IndexOf(' ');
            if (space <= 0) return null;
            var name = rest[..space];
            var action = rest[(space + 1)..];
            if (ServerSenders.Contains(name)) return (name, action);
            return IsOnline(name, online) ? (name, action) : null;
        }

        return null;
    }

    private const string NotSecure = "[Not Secure] ";

    /// <summary>The senders the server itself uses: console, RCON, and a command block.</summary>
    private static readonly HashSet<string> ServerSenders = new(StringComparer.Ordinal) { "Server", "Rcon", "@" };

    /// <summary>Whether a chat sender is the server itself (console, RCON, a command block).</summary>
    internal static bool IsServerSender(string sender) => ServerSenders.Contains(sender);

    private static bool IsOnline(string name, IReadOnlySet<string>? online) =>
        online is not null && PlayerName().IsMatch(name) && online.Contains(name);

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

    /// <summary>Whether a line is one frame of a Java stack trace, by its shape.</summary>
    internal static bool IsStackFrame(string text) => StackFrame().IsMatch(text);

    /// <summary>Whether the line starts with a bracketed log prefix, "[...]: ".</summary>
    /// <remarks>
    /// Any bracketed prefix, with or without a level in it: a line like
    /// <c>[12:34:56] [Render thread]: …</c> is its own entry, not the continuation of the one above.
    /// </remarks>
    internal static bool HasLogPrefix(string text) =>
        text.StartsWith('[') && text.IndexOf("]: ", StringComparison.Ordinal) > 0;

    // "at a.b.C.method(File.java:12)", "Caused by: …", "Suppressed: …", "... 12 more".
    [GeneratedRegex(@"^\s*(at [^\s(]+\(.*\)\s*$|Caused by: |Suppressed: |\.\.\. \d+ more\s*$)")]
    private static partial Regex StackFrame();

    // [12:34:56] [Server thread/WARN]:  and  [12:34:56 WARN]:  — the two shapes in the wild.
    [GeneratedRegex(@"\[[^\]]*[\s/](INFO|WARN|WARNING|ERROR|SEVERE|FATAL)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LogLevel();

    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();
}
