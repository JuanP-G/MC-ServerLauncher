using System.Collections.Generic;
using System.Linq;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What a server type accepts: plugins, mods, or neither.</summary>
/// <remarks>
/// The distinction users actually care about, and the one the old picker never showed. It also
/// happens to be the right question for half the code that used to ask "is this Paper?" — the mod
/// store, the content folder, and whether mods can shut Bedrock players out all key off the family
/// rather than off one particular member of it.
/// </remarks>
public enum ServerFamily
{
    /// <summary>Neither: unmodified Minecraft.</summary>
    None,

    /// <summary>The Bukkit family — plugins in <c>plugins/</c>, running only on the server.</summary>
    Plugins,

    /// <summary>Mod loaders — mods in <c>mods/</c>, usually needed on the client too.</summary>
    Mods
}

/// <summary>
/// One row per server type: what it is called, what it takes, and whether Bedrock can reach it.
/// </summary>
/// <remarks>
/// <para>
/// This used to be six separate <c>switch</c> statements — badge colours in one file, the plugin/mod
/// question in another, the Geyser config path in a third, and the list of types spelled out again
/// as strings in two XAML files. Adding a type meant finding all of them, and missing one produced a
/// server that looked right and behaved as something else.
/// </para>
/// <para>
/// Same instinct as <see cref="LoaderPaths"/>: one table, and everything reads from it.
/// </para>
/// </remarks>
public static class ServerTypeCatalog
{
    /// <summary>Everything the app knows about one server type.</summary>
    /// <param name="Type">The enum value, carried around instead of its name as a string.</param>
    /// <param name="DisplayName">The brand name. Not translated — these are proper nouns.</param>
    /// <param name="Family">Plugins, mods, or neither.</param>
    /// <param name="BadgeColor">Hex, so this table stays free of any UI framework.</param>
    /// <param name="SupportsCrossplay">Whether Geyser publishes a build that runs on it.</param>
    /// <param name="DescriptionKey">resx key for the one line shown under the name in the picker.</param>
    public record Entry(
        ServerType Type,
        string DisplayName,
        ServerFamily Family,
        string BadgeColor,
        bool SupportsCrossplay,
        string DescriptionKey);

    /// <summary>
    /// The types, in the order the picker shows them.
    /// </summary>
    /// <remarks>
    /// Display order only — it is deliberately not the enum order, which is a file format and cannot
    /// be rearranged. Vanilla leads because it is the default; the plugin family is grouped together
    /// and put before the mod loaders, because it is the one that takes Bedrock players.
    /// </remarks>
    public static readonly Entry[] All =
    {
        new(ServerType.Vanilla, "Vanilla", ServerFamily.None, "#6E9E52", false, "TypeDesc_Vanilla"),
        new(ServerType.Paper, "Paper", ServerFamily.Plugins, "#C0563E", true, "TypeDesc_Paper"),
        new(ServerType.Purpur, "Purpur", ServerFamily.Plugins, "#9B6BC7", true, "TypeDesc_Purpur"),
        new(ServerType.Fabric, "Fabric", ServerFamily.Mods, "#B58D5A", true, "TypeDesc_Fabric"),
        new(ServerType.NeoForge, "NeoForge", ServerFamily.Mods, "#D08A3E", true, "TypeDesc_NeoForge"),
        new(ServerType.Forge, "Forge", ServerFamily.Mods, "#5A8AB5", false, "TypeDesc_Forge"),
    };

    /// <summary>The row for a type. Never null for a value that exists in the enum.</summary>
    public static Entry For(ServerType type) =>
        All.FirstOrDefault(e => e.Type == type)
        ?? new Entry(type, type.ToString(), ServerFamily.None, "#6E7681", false, string.Empty);

    /// <summary>Whether this type keeps its content in <c>plugins/</c> rather than <c>mods/</c>.</summary>
    public static bool IsPluginBased(ServerType type) => For(type).Family == ServerFamily.Plugins;

    /// <summary>The content folder name, which follows directly from the family.</summary>
    public static string ContentFolder(ServerType type) => IsPluginBased(type) ? "plugins" : "mods";

    /// <summary>The types of one family, in display order.</summary>
    public static IEnumerable<Entry> InFamily(ServerFamily family) => All.Where(e => e.Family == family);

    /// <summary>The badge shown on the card: "Plugins", "Mods", or nothing for Vanilla.</summary>
    public static string FamilyLabel(ServerFamily family) => family switch
    {
        ServerFamily.Plugins => Localizer.Get("Family_Plugins"),
        ServerFamily.Mods => Localizer.Get("Family_Mods"),
        _ => string.Empty
    };

    /// <summary>
    /// The badge's emoji. Plugins get a jigsaw piece, mods a cog.
    /// </summary>
    /// <remarks>
    /// Emoji rather than an icon font because the two badges have to be told apart at a glance and
    /// in colour-blind-safe terms — shape does that where colour alone would not.
    /// </remarks>
    public static string FamilyEmoji(ServerFamily family) => family switch
    {
        ServerFamily.Plugins => "\U0001F9E9",
        ServerFamily.Mods => "\u2699",
        _ => string.Empty
    };
}
