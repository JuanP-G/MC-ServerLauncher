using System.IO;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>One id and how many times it was counted: <c>minecraft:stone</c>, 1420.</summary>
public sealed record StatEntry(string Id, long Count);

/// <summary>A player's statistics as the server itself counts them.</summary>
/// <param name="PlayTicks">Time on the server, in game ticks (20 per second).</param>
/// <param name="Deaths">Times died.</param>
/// <param name="MobKills">Mobs killed.</param>
/// <param name="PlayerKills">Players killed.</param>
/// <param name="DistanceCm">Distance covered on foot, swimming, flying and riding, in centimetres.</param>
public sealed record PlayerStats(long PlayTicks, long Deaths, long MobKills, long PlayerKills, long DistanceCm)
{
    public TimeSpan PlayTime => TimeSpan.FromSeconds(PlayTicks / 20.0);

    /// <summary>Blocks broken, by block, most first. Minecraft counts these exactly.</summary>
    public IReadOnlyList<StatEntry> Mined { get; init; } = Array.Empty<StatEntry>();

    /// <summary>
    /// Items used, by item, most first — which for a block means placing it.
    /// </summary>
    /// <remarks>
    /// Minecraft has no "blocks placed" counter. What it keeps is <c>minecraft:used</c>, "times you
    /// used this item", and for a block that is a placement — but for a shovel it is a swing and for
    /// bread it is a meal. The list is shown as it is, with a line saying so, rather than filtered
    /// against a hand-kept list of block ids that would go stale every release and quietly hide
    /// whatever was added last.
    /// </remarks>
    public IReadOnlyList<StatEntry> Used { get; init; } = Array.Empty<StatEntry>();

    /// <summary>Items crafted, by item, most first.</summary>
    public IReadOnlyList<StatEntry> Crafted { get; init; } = Array.Empty<StatEntry>();

    /// <summary>Mobs killed, by kind, most first.</summary>
    public IReadOnlyList<StatEntry> Killed { get; init; } = Array.Empty<StatEntry>();

    /// <summary>What killed this player, by kind, most first.</summary>
    public IReadOnlyList<StatEntry> KilledBy { get; init; } = Array.Empty<StatEntry>();

    public long Jumps { get; init; }
    public long DamageDealt { get; init; }
    public long DamageTaken { get; init; }

    /// <summary>Distance in creative flight, in centimetres.</summary>
    public long FlownCm { get; init; }

    /// <summary>Distance under an elytra, in centimetres.</summary>
    public long ElytraCm { get; init; }

    /// <summary>Distance walked, sprinted and crouched, in centimetres.</summary>
    public long WalkedCm { get; init; }

    /// <summary>Total blocks broken, whatever they were.</summary>
    public long BlocksMined => Mined.Sum(e => e.Count);

    /// <summary>Ore blocks broken; see <see cref="MinecraftIds.LooksLikeOre"/> for how they are spotted.</summary>
    public long OresMined => Mined.Where(e => MinecraftIds.LooksLikeOre(e.Id)).Sum(e => e.Count);
}

/// <summary>
/// Reads the statistics the server keeps per player, for the player's profile.
/// </summary>
/// <remarks>
/// <para>
/// Read when a profile is opened and never copied: Minecraft already stores them, so they cost the
/// history nothing. They are filed by UUID, in <c>&lt;world&gt;/stats/</c> up to 1.21 and in
/// <c>&lt;world&gt;/players/stats/</c> from 26.1, where the player data moved under <c>players/</c>.
/// </para>
/// <para>
/// The format is the one from 1.13 on. Older servers wrote a different one; for them this answers
/// "none", like for a player who never joined, rather than a wrong number.
/// </para>
/// <para>
/// The file is written by another process while this reads it, so nothing here throws: a category
/// that is missing, truncated or not the shape it should be gives an empty list.
/// </para>
/// </remarks>
public static class PlayerStatsReader
{
    /// <summary>
    /// How many entries of each category are kept.
    /// </summary>
    /// <remarks>
    /// A long-running player touches hundreds of block types, and nobody reads past the first few.
    /// Cutting here rather than in the view means the rest is never turned into objects at all.
    /// </remarks>
    private const int MaxEntries = 200;

    public static PlayerStats? Read(string serverFolder, string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid) || !Guid.TryParse(uuid, out var id)) return null;

        var props = new ServerPropertiesService().Read(Path.Combine(serverFolder, "server.properties"));
        var level = props.TryGetValue("level-name", out var n) && n.Length > 0 ? n : "world";
        var world = Path.Combine(serverFolder, level);
        var file = id.ToString("D") + ".json";

        foreach (var path in new[] { Path.Combine(world, "players", "stats", file), Path.Combine(world, "stats", file) })
        {
            if (File.Exists(path) && Parse(ReadShared(path)) is { } stats) return stats;
        }
        return null;
    }

    internal static PlayerStats? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("stats", out var stats)) return null;
            if (!stats.TryGetProperty("minecraft:custom", out var custom))
                return new PlayerStats(0, 0, 0, 0, 0)
                {
                    Mined = Category(stats, "minecraft:mined"),
                    Used = Category(stats, "minecraft:used"),
                    Crafted = Category(stats, "minecraft:crafted"),
                    Killed = Category(stats, "minecraft:killed"),
                    KilledBy = Category(stats, "minecraft:killed_by")
                };

            long Get(string key) =>
                custom.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;

            long Distance(Func<string, bool> matches) => custom.EnumerateObject()
                .Where(p => p.Name.EndsWith("_one_cm", StringComparison.Ordinal) && matches(p.Name))
                .Sum(p => p.Value.TryGetInt64(out var cm) ? cm : 0);

            var distance = Distance(name => !name.Contains("fall", StringComparison.Ordinal));

            // play_time from 1.17; play_one_minute before, which despite its name also counts ticks.
            var ticks = Get("minecraft:play_time");
            if (ticks == 0) ticks = Get("minecraft:play_one_minute");

            return new PlayerStats(ticks, Get("minecraft:deaths"), Get("minecraft:mob_kills"),
                Get("minecraft:player_kills"), distance)
            {
                Mined = Category(stats, "minecraft:mined"),
                Used = Category(stats, "minecraft:used"),
                Crafted = Category(stats, "minecraft:crafted"),
                Killed = Category(stats, "minecraft:killed"),
                KilledBy = Category(stats, "minecraft:killed_by"),
                Jumps = Get("minecraft:jump"),
                DamageDealt = Get("minecraft:damage_dealt"),
                DamageTaken = Get("minecraft:damage_taken"),
                FlownCm = Get("minecraft:fly_one_cm"),
                ElytraCm = Get("minecraft:aviate_one_cm"),
                WalkedCm = Distance(name =>
                    name.Contains("walk", StringComparison.Ordinal)
                    || name.Contains("sprint", StringComparison.Ordinal)
                    || name.Contains("crouch", StringComparison.Ordinal))
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One category of the stats file as a list, biggest first.</summary>
    private static IReadOnlyList<StatEntry> Category(JsonElement stats, string name)
    {
        if (!stats.TryGetProperty(name, out var category) || category.ValueKind != JsonValueKind.Object)
            return Array.Empty<StatEntry>();

        return category.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Number)
            .Select(p => new StatEntry(p.Name, p.Value.TryGetInt64(out var count) ? count : 0))
            .Where(e => e.Count > 0)
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Take(MaxEntries)
            .ToList();
    }

    private static string? ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
