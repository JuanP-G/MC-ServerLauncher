namespace McServerLauncher.Services;

/// <summary>
/// Turns a Minecraft id into something readable: <c>minecraft:deepslate_diamond_ore</c> becomes
/// "Deepslate diamond ore".
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not translated. There are well over a thousand blocks and items, more with every
/// version and every mod, and translating them would mean either shipping Minecraft's own language
/// files or leaving most of a player's list in English anyway with a handful of words in the middle
/// in Spanish. What is translated is the label above the list; what is in it is the game's own
/// vocabulary, which is what people read on the wiki and type into commands.
/// </para>
/// <para>
/// The namespace is dropped only when it is <c>minecraft</c>. A block from a mod keeps it, because
/// "Source gem" on its own is a mystery and "ars_nouveau: source gem" is not.
/// </para>
/// </remarks>
public static class MinecraftIds
{
    private const string Vanilla = "minecraft:";

    public static string Pretty(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";

        var text = id.Trim();
        var prefix = "";

        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            var space = text[..colon];
            var rest = text[(colon + 1)..];
            if (string.Equals(space, "minecraft", StringComparison.OrdinalIgnoreCase))
            {
                text = rest;
            }
            else
            {
                prefix = space + ": ";
                text = rest;
            }
        }

        text = text.Replace('_', ' ').Replace('/', ' ').Trim();
        if (text.Length == 0) return prefix.TrimEnd(' ', ':');

        return prefix + char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Whether an id looks like an ore, for the "ores per hour" figure.</summary>
    /// <remarks>
    /// By name, because the app has no block registry and is not going to ship one that goes stale
    /// every release. It catches <c>diamond_ore</c>, <c>deepslate_gold_ore</c>, <c>nether_quartz_ore</c>
    /// and the ancient debris that stands in for netherite, and it is a curiosity on a profile page
    /// rather than evidence of anything — which is the only reason a heuristic is good enough here.
    /// </remarks>
    public static bool LooksLikeOre(string id)
    {
        var name = id.StartsWith(Vanilla, StringComparison.OrdinalIgnoreCase) ? id[Vanilla.Length..] : id;
        return name.EndsWith("_ore", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ancient_debris", StringComparison.OrdinalIgnoreCase);
    }
}
