using System.Collections.Concurrent;
using System.Reflection;

namespace McServerLauncher.Services;

/// <summary>
/// The commands a server knows and how each is written, for the console's completion.
/// </summary>
/// <remarks>
/// <para>
/// It starts from the vanilla list that ships with the app (what a 26.2 server prints for
/// <c>help</c>), so completion works before the server has been asked and on a server that cannot
/// be (Pumpkin). Once the server answers its own <c>help</c>, its answer replaces that list: it
/// brings the commands of its mods and plugins and the exact names of this version, which change
/// (game rules went from <c>keepInventory</c> to <c>keep_inventory</c>).
/// </para>
/// <para>
/// The top-level <c>help</c> folds every command to one line, so <c>/time (set|add|query)</c>
/// says nothing of what follows <c>set</c>. <c>help time</c> has a line per branch; those are kept
/// per command as <see cref="AddDetail"/> brings them, and win over the folded line.
/// </para>
/// </remarks>
public sealed class CommandCatalog
{
    private readonly ConcurrentDictionary<string, string> _top = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _detail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once the server's own answer has replaced the built-in list.</summary>
    public bool FromServer { get; private set; }

    /// <summary>The vanilla commands that ship with the app.</summary>
    public static CommandCatalog BuiltIn()
    {
        var catalog = new CommandCatalog();
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("McServerLauncher.Resources.vanilla-commands.txt");
        if (stream is null) return catalog;
        using var reader = new StreamReader(stream);
        catalog.AddLines(reader.ReadToEnd().Split('\n'));
        return catalog;
    }

    /// <summary>Every command name, aliases included.</summary>
    public IEnumerable<string> Names => _top.Keys.Concat(_aliases.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The command <paramref name="name"/> stands for: itself, or what it is an alias of.</summary>
    public string Resolve(string name) => _aliases.TryGetValue(name, out var target) ? target : name;

    /// <summary>Whether the command is known, by name or alias.</summary>
    public bool Knows(string name) => _top.ContainsKey(name) || _aliases.ContainsKey(name);

    /// <summary>Whether <c>help &lt;name&gt;</c> has been read already.</summary>
    public bool HasDetail(string name) => _detail.ContainsKey(Resolve(name));

    /// <summary>The usage lines of a command: its branches when known, or its folded line.</summary>
    public IReadOnlyList<string> UsagesOf(string name)
    {
        var command = Resolve(name);
        if (_detail.TryGetValue(command, out var lines) && lines.Count > 0) return lines;
        return _top.TryGetValue(command, out var line) ? [line] : [];
    }

    /// <summary>What a Bukkit server said the command does, when it said (its help is <c>/name: text</c>).</summary>
    public string? DescriptionOf(string name) =>
        _descriptions.TryGetValue(Resolve(name), out var text) ? text : null;

    /// <summary>Replaces the list with the server's answer to <c>help</c>, if it said anything usable.</summary>
    public void ReplaceWithServer(IEnumerable<string> helpLines)
    {
        var fresh = new CommandCatalog();
        fresh.AddLines(helpLines);
        if (fresh._top.IsEmpty) return;
        _top.Clear(); _aliases.Clear(); _detail.Clear(); _descriptions.Clear();
        foreach (var (k, v) in fresh._top) _top[k] = v;
        foreach (var (k, v) in fresh._aliases) _aliases[k] = v;
        foreach (var (k, v) in fresh._descriptions) _descriptions[k] = v;
        FromServer = true;
    }

    /// <summary>Keeps the branches the server printed for <c>help &lt;name&gt;</c>.</summary>
    public void AddDetail(string name, IEnumerable<string> helpLines)
    {
        var command = Resolve(name);
        var lines = helpLines.Select(ParseHelpLine)
            .Where(h => h is { Description: null } && string.Equals(h.Value.Name, command, StringComparison.OrdinalIgnoreCase))
            .Select(h => h!.Value.Usage).ToList();
        // Even an empty answer is remembered, so the same question is not asked again.
        _detail[command] = lines;
    }

    private void AddLines(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            if (ParseHelpLine(raw) is not { } help) continue;
            if (help.AliasOf is { } target) { _aliases[help.Name] = target; continue; }
            if (!_top.ContainsKey(help.Name)) _top[help.Name] = help.Usage;
            if (help.Description is { Length: > 0 } d) _descriptions[help.Name] = d;
        }
    }

    /// <summary>One line of the server's help, read.</summary>
    /// <param name="Name">The command's name.</param>
    /// <param name="Usage">The whole usage, name included (<c>gamemode &lt;gamemode&gt; [&lt;target&gt;]</c>).</param>
    /// <param name="AliasOf">For <c>/tp -&gt; teleport</c>, the command it stands for.</param>
    /// <param name="Description">For Bukkit's <c>/ban: Adds player to banlist</c>, the text after the colon.</param>
    public readonly record struct HelpLine(string Name, string Usage, string? AliasOf, string? Description);

    /// <summary>
    /// Reads a console line as a line of <c>help</c>, or returns null when it is not one. Takes the
    /// line as the console shows it, with or without the <c>[time] [thread/INFO]: </c> in front.
    /// </summary>
    public static HelpLine? ParseHelpLine(string line)
    {
        var text = StripLogPrefix(line).Trim();
        if (text.Length < 2 || text[0] != '/') return null;
        text = text[1..];

        var nameEnd = 0;
        while (nameEnd < text.Length && IsNameChar(text[nameEnd])) nameEnd++;
        // A colon is part of a namespaced name (essentials:home) but ends Bukkit's "/ban: ..."
        if (nameEnd > 0 && text[nameEnd - 1] == ':') nameEnd--;
        if (nameEnd == 0) return null;
        var name = text[..nameEnd];
        var rest = text[nameEnd..];

        if (rest.StartsWith(':'))   // Bukkit: "/ban: Adds player to banlist"
            return new HelpLine(name, name, null, rest[1..].Trim());
        if (rest.Length > 0 && rest[0] != ' ') return null;
        rest = rest.Trim();
        if (rest.StartsWith("->", StringComparison.Ordinal))
        {
            var target = rest[2..].Trim();
            return target.Length > 0 ? new HelpLine(name, name, target, null) : null;
        }
        return new HelpLine(name, rest.Length == 0 ? name : name + " " + rest, null, null);
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.';

    /// <summary>The text after Minecraft's log prefix (<c>[13:26:30] [Server thread/INFO]: </c>).</summary>
    internal static string StripLogPrefix(string line)
    {
        var marker = line.IndexOf("]: ", StringComparison.Ordinal);
        return marker >= 0 && line.StartsWith('[') ? line[(marker + 3)..] : line;
    }
}
