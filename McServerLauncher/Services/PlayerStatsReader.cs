using System.IO;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>A player's statistics as the server itself counts them.</summary>
/// <param name="PlayTicks">Time on the server, in game ticks (20 per second).</param>
/// <param name="Deaths">Times died.</param>
/// <param name="MobKills">Mobs killed.</param>
/// <param name="PlayerKills">Players killed.</param>
/// <param name="DistanceCm">Distance covered on foot, swimming, flying and riding, in centimetres.</param>
public sealed record PlayerStats(long PlayTicks, long Deaths, long MobKills, long PlayerKills, long DistanceCm)
{
    public TimeSpan PlayTime => TimeSpan.FromSeconds(PlayTicks / 20.0);
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
/// </remarks>
public static class PlayerStatsReader
{
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
            if (!stats.TryGetProperty("minecraft:custom", out var custom)) return new PlayerStats(0, 0, 0, 0, 0);

            long Get(string key) =>
                custom.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;

            var distance = custom.EnumerateObject()
                .Where(p => p.Name.EndsWith("_one_cm", StringComparison.Ordinal)
                            && !p.Name.Contains("fall", StringComparison.Ordinal))
                .Sum(p => p.Value.TryGetInt64(out var cm) ? cm : 0);

            // play_time from 1.17; play_one_minute before, which despite its name also counts ticks.
            var ticks = Get("minecraft:play_time");
            if (ticks == 0) ticks = Get("minecraft:play_one_minute");

            return new PlayerStats(ticks, Get("minecraft:deaths"), Get("minecraft:mob_kills"),
                Get("minecraft:player_kills"), distance);
        }
        catch
        {
            return null;
        }
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
