namespace McServerLauncher.Services;

/// <summary>
/// Asks the running server for its commands (<c>help</c>, <c>help &lt;command&gt;</c>) without its
/// answer filling the console.
/// </summary>
/// <remarks>
/// <para>
/// The question goes in as two lines: the <c>help</c> itself, then a command that does not exist
/// (<see cref="Fence"/>). Every server answers an unknown command with a line that names it, so
/// that line marks the end of the answer even when a plugin replaces <c>help</c> with its own and
/// prints something unexpected. A timeout closes it too, in case nothing ever comes.
/// </para>
/// <para>
/// While a question is open only the lines that look like help are kept back: a slash command, a
/// Bukkit index header, the fence's own reply. Someone joining or chatting at that moment goes to
/// the console as always. The server still writes the answer to its own <c>logs/latest.log</c>;
/// this only keeps the app's console clean.
/// </para>
/// </remarks>
public sealed class CommandHelpProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private readonly object _lock = new();
    private Pending? _pending;
    private int _sequence;

    private sealed class Pending(string topic, string fence, DateTime asked)
    {
        public string Topic { get; } = topic;
        public string Fence { get; } = fence;
        public DateTime Asked { get; } = asked;
        public List<string> Lines { get; } = [];
    }

    /// <summary>The command name used as the end marker.</summary>
    public string? Fence { get { lock (_lock) return _pending?.Fence; } }

    /// <summary>True while a question is waiting for its answer.</summary>
    public bool IsAsking { get { lock (_lock) return _pending is not null; } }

    /// <summary>
    /// Starts a question about <paramref name="topic"/> (null for the whole list) and returns the
    /// two lines to write to the console, or null when one is already open.
    /// </summary>
    public string[]? Ask(string? topic, DateTime now)
    {
        lock (_lock)
        {
            if (_pending is { } open && now - open.Asked < Timeout) return null;
            var fence = $"mcsl-end-{++_sequence:x4}";
            _pending = new Pending(topic ?? "", fence, now);
            return [topic is null ? "help" : "help " + topic, fence];
        }
    }

    /// <summary>A finished answer: what was asked and the lines that came back.</summary>
    public readonly record struct Answer(string? Topic, IReadOnlyList<string> Lines);

    /// <summary>
    /// Offers a console line to the open question. True when the line belongs to the answer and
    /// must not be shown; <paramref name="answer"/> is set once the answer is complete.
    /// </summary>
    public bool TryConsume(string line, DateTime now, out Answer? answer)
    {
        answer = null;
        lock (_lock)
        {
            if (_pending is not { } p) return false;
            if (now - p.Asked > Timeout)
            {
                answer = Finish(p);
                return false;
            }

            var text = CommandCatalog.StripLogPrefix(line).Trim();
            if (text.Contains(p.Fence, StringComparison.OrdinalIgnoreCase))
            {
                answer = Finish(p);
                return true;
            }
            if (IsUnknownCommandReply(text)) return true;   // the line before the fence's "<--[HERE]"
            if (CommandCatalog.ParseHelpLine(line) is not null || IsHelpDecoration(text))
            {
                p.Lines.Add(line);
                return true;
            }
            return false;
        }
    }

    /// <summary>Closes a question that has waited too long, with whatever arrived.</summary>
    public Answer? Expire(DateTime now)
    {
        lock (_lock)
            return _pending is { } p && now - p.Asked > Timeout ? Finish(p) : null;
    }

    private Answer Finish(Pending p)
    {
        _pending = null;
        return new Answer(p.Topic.Length == 0 ? null : p.Topic, p.Lines);
    }

    // Vanilla: "Unknown or incomplete command. See below for error"; Bukkit: "Unknown command. Type "/help" for help."
    private static bool IsUnknownCommandReply(string text) =>
        text.StartsWith("Unknown or incomplete command", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase);

    // Bukkit's index: "--------- Help: Index ---------", "Aliases: Lists command aliases".
    private static bool IsHelpDecoration(string text) =>
        text.StartsWith("---", StringComparison.Ordinal) && text.Contains("Help", StringComparison.OrdinalIgnoreCase);
}
