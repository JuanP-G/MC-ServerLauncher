namespace McServerLauncher.Services;

/// <summary>What the completion knows about the server besides its commands.</summary>
/// <param name="Online">Players connected right now.</param>
/// <param name="Known">Players the server has seen (whitelist, usercache), online or not.</param>
/// <param name="Operators">The operators, to say so beside their names.</param>
public sealed record CompletionContext(
    IReadOnlyCollection<string> Online, IReadOnlyCollection<string> Known, IReadOnlyCollection<string> Operators)
{
    public static readonly CompletionContext Empty = new([], [], []);
}

/// <summary>What kind of thing a suggestion is, so the view can say it in the user's language.</summary>
public enum SuggestionKind { Command, Word, OnlinePlayer, OnlineOperator, KnownPlayer, Selector, Value }

/// <summary>One suggestion.</summary>
/// <param name="Text">What goes in the box.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Detail">The usage line of a command, or a description the server gave; null otherwise.</param>
public sealed record Suggestion(string Text, SuggestionKind Kind, string? Detail = null);

/// <summary>The answer for what is in the box.</summary>
/// <param name="ReplaceFrom">Where the word being typed starts: a suggestion replaces from here to the end.</param>
/// <param name="Typed">The part of that word already typed.</param>
/// <param name="Suggestions">What fits, the likeliest first.</param>
/// <param name="Usage">The usage line of the command being typed, or null.</param>
/// <param name="UsageWord">Which word of <paramref name="Usage"/> is being typed (0 is the first after the name), or -1.</param>
/// <param name="DetailWanted">The command whose <c>help &lt;command&gt;</c> would say more than is known.</param>
public sealed record Completion(int ReplaceFrom, string Typed, IReadOnlyList<Suggestion> Suggestions,
    string? Usage, int UsageWord, string? DetailWanted)
{
    public static readonly Completion None = new(0, "", [], null, -1, null);
}

/// <summary>
/// Works out what can be typed next in the console, from the usage lines the server gives.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here knows a command by heart: the words come from the usage (<c>(set|add|query)</c>),
/// and the values from what the placeholder is called. A <c>&lt;targets&gt;</c> or
/// <c>&lt;player&gt;</c> offers the players and the selectors, a <c>&lt;gamemode&gt;</c> the four
/// modes, a game rule's <c>&lt;value&gt;</c> true and false. A placeholder this does not recognise
/// (an item, a block, a position) suggests nothing and leaves the hint to say what goes there.
/// </para>
/// <para>
/// Completion is always for the last word, at the end of the line, which is where people type in a
/// console. A placeholder that is the rest of the line (a message, a reason) ends the suggestions.
/// </para>
/// </remarks>
public static class CommandCompleter
{
    private const int MaxSuggestions = 40;

    /// <summary>The commands people type most, first among those that fit.</summary>
    private static readonly string[] Common =
    [
        "say", "list", "op", "deop", "kick", "ban", "pardon", "whitelist", "gamemode", "tp", "teleport",
        "give", "time", "weather", "difficulty", "gamerule", "seed", "save-all", "stop", "msg", "kill",
        "effect", "experience", "xp", "defaultgamemode", "spawnpoint", "setworldspawn",
    ];

    private static readonly string[] Selectors = ["@a", "@p", "@r", "@s", "@e"];
    private static readonly string[] Gamemodes = ["survival", "creative", "adventure", "spectator"];

    // The value is the rest of the line: nothing after it can be suggested.
    private static readonly HashSet<string> RestOfLine = new(StringComparer.OrdinalIgnoreCase)
        { "message", "action", "reason", "text", "json", "arguments" };

    // Three numbers, x y z.
    private static readonly HashSet<string> Positions = new(StringComparer.OrdinalIgnoreCase)
        { "pos", "from", "to", "location", "center", "begin", "end", "destinationpos" };

    private static readonly HashSet<string> PlayerArgs = new(StringComparer.OrdinalIgnoreCase)
        { "targets", "target", "player", "players", "victim", "destination", "entity", "targetentity" };

    /// <summary>The completion for <paramref name="text"/>, typed with the caret at its end.</summary>
    public static Completion Complete(string text, CommandCatalog catalog, CompletionContext context)
    {
        if (string.IsNullOrEmpty(text)) return Completion.None;
        var body = text.StartsWith('/') ? text[1..] : text;   // "/gamemode" works as it does in game

        var words = body.Split(' ');
        var typed = words[^1];
        var replaceFrom = text.Length - typed.Length;

        if (words.Length == 1)
            return new Completion(replaceFrom, typed, CommandNames(typed, catalog), null, -1, null);

        var command = catalog.Resolve(words[0]);
        if (!catalog.Knows(words[0]))
            return new Completion(replaceFrom, typed, [], null, -1, null);

        var done = words[1..^1].Where(w => w.Length > 0).ToArray();
        var usages = catalog.UsagesOf(command);

        var suggestions = new List<Suggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? usage = null;
        var usageWord = -1;

        foreach (var line in usages)
        {
            var tokens = CommandUsage.Parse(line);
            if (Walk(tokens, done) is not { } at) continue;

            usage ??= line;
            if (usage == line) usageWord = at;
            if (at >= tokens.Count) continue;

            var token = tokens[at];
            if (token.Args.Any(a => RestOfLine.Contains(a))) continue;
            foreach (var literal in token.Literals)
                if (Fits(literal, typed) && seen.Add(literal))
                    suggestions.Add(new Suggestion(literal, SuggestionKind.Word));
            foreach (var arg in token.Args)
                foreach (var value in ValuesFor(arg, command, context))
                    if (Fits(value.Text, typed) && seen.Add(value.Text))
                        suggestions.Add(value);
        }

        // The top-level help folds each command to one line; its branches are one question away.
        return new Completion(replaceFrom, typed, Rank(suggestions).Take(MaxSuggestions).ToList(),
            usage, usageWord, catalog.HasDetail(command) ? null : command);
    }

    /// <summary>
    /// Follows the words already typed through a usage line. Returns the index of the usage word
    /// the next typed word fills, or null when the typed words do not fit this line.
    /// </summary>
    private static int? Walk(IReadOnlyList<UsageToken> tokens, string[] done)
    {
        var at = 0;
        var i = 0;
        while (i < done.Length)
        {
            if (at >= tokens.Count) return null;
            var token = tokens[at];
            if (token.Args.Any(a => RestOfLine.Contains(a))) return at;   // swallowed by a message
            var word = done[i];
            if (token.Literals.Contains(word, StringComparer.OrdinalIgnoreCase)) { i++; at++; continue; }
            if (token.Args.Count > 0)
            {
                var span = token.Args.Any(a => Positions.Contains(a)) ? 3 : 1;
                i += span;
                at++;
                continue;
            }
            if (token.Optional) { at++; continue; }   // an optional word left out
            return null;
        }
        return at;
    }

    private static IEnumerable<Suggestion> ValuesFor(string arg, string command, CompletionContext context)
    {
        if (PlayerArgs.Contains(arg))
        {
            var ops = new HashSet<string>(context.Operators, StringComparer.OrdinalIgnoreCase);
            foreach (var name in context.Online.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                yield return new Suggestion(name, ops.Contains(name) ? SuggestionKind.OnlineOperator : SuggestionKind.OnlinePlayer);
            foreach (var name in context.Known.Except(context.Online, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                yield return new Suggestion(name, SuggestionKind.KnownPlayer);
            // pardon, whitelist add... take names, not selectors
            if (!arg.Equals("player", StringComparison.OrdinalIgnoreCase))
                foreach (var s in Selectors) yield return new Suggestion(s, SuggestionKind.Selector);
            yield break;
        }
        if (arg.Equals("gamemode", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var g in Gamemodes) yield return new Suggestion(g, SuggestionKind.Value);
            yield break;
        }
        if (arg.Equals("value", StringComparison.OrdinalIgnoreCase) && command.Equals("gamerule", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Suggestion("true", SuggestionKind.Value);
            yield return new Suggestion("false", SuggestionKind.Value);
        }
    }

    private static List<Suggestion> CommandNames(string typed, CommandCatalog catalog)
    {
        var names = catalog.Names.Where(n => Fits(n, typed));
        // Namespaced duplicates ("minecraft:gamemode") only when that is what is being typed.
        if (!typed.Contains(':')) names = names.Where(n => !n.Contains(':'));
        return Rank(names.Select(n => new Suggestion(n, SuggestionKind.Command,
                catalog.DescriptionOf(n) ?? catalog.UsagesOf(n).FirstOrDefault())))
            .Take(MaxSuggestions).ToList();
    }

    /// <summary>Starts with what is typed, and is more than it: a word typed in full needs no suggestion.</summary>
    private static bool Fits(string candidate, string typed) =>
        candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
        && candidate.Length > typed.Length;

    /// <summary>The likeliest first: commands people use, then online players, then alphabetical.</summary>
    private static IEnumerable<Suggestion> Rank(IEnumerable<Suggestion> items) =>
        items.OrderBy(s => s.Kind switch
            {
                SuggestionKind.Word => 0,
                SuggestionKind.Value => 0,
                SuggestionKind.OnlineOperator or SuggestionKind.OnlinePlayer => 1,
                SuggestionKind.KnownPlayer => 2,
                SuggestionKind.Selector => 3,
                _ => 0,
            })
            .ThenBy(s => s.Kind == SuggestionKind.Command && Array.IndexOf(Common, s.Text.ToLowerInvariant()) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(s => s.Kind == SuggestionKind.Command ? s.Text : "", StringComparer.OrdinalIgnoreCase);
}
