using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>Where a seed the app shows came from, which decides how it is described.</summary>
public enum SeedSource
{
    /// <summary>Nothing to show: no world yet and no seed asked for, or a world the app cannot read.</summary>
    Unknown,

    /// <summary>Read from the world itself. This is the seed the world was generated with.</summary>
    World,

    /// <summary>
    /// The one the server printed when asked with <c>seed</c>, remembered by the app. Used only
    /// when the world's files could not be read.
    /// </summary>
    Console,

    /// <summary>
    /// No world yet: the one <c>level-seed</c> asks for, which the first start will use.
    /// </summary>
    Pending
}

/// <summary>A world's seed, and where the app found it.</summary>
public sealed record SeedInfo(long? Seed, SeedSource Source)
{
    public static readonly SeedInfo None = new(null, SeedSource.Unknown);
}

/// <summary>
/// Finds the seed of a server's world.
/// </summary>
/// <remarks>
/// <para>
/// <c>level-seed</c> in server.properties is <em>not</em> that seed. It is read once, when the
/// world is generated. Empty means a random one was picked, and text means Java's
/// <c>String.hashCode</c> of it was used. After that, only the world knows its seed.
/// </para>
/// <para>
/// The world has kept it in three places over the years, and all three are tried:
/// <list type="bullet">
/// <item><c>data/minecraft/world_gen_settings.dat</c> → <c>data.seed</c>, from 26.1.</item>
/// <item><c>level.dat</c> → <c>Data.WorldGenSettings.seed</c>, from 1.16.</item>
/// <item><c>level.dat</c> → <c>Data.RandomSeed</c>, before 1.16.</item>
/// </list>
/// The newest came to light because a 26.2 world had no seed in <c>level.dat</c> at all. That
/// could happen again, which is why the answer to the <c>seed</c> command is also remembered.
/// </para>
/// </remarks>
public static partial class WorldSeed
{
    /// <summary>The seed of the world in <paramref name="serverFolder"/>, and where it came from.</summary>
    /// <param name="serverFolder">The server's folder.</param>
    /// <param name="lastKnown">The seed the server last printed for <c>seed</c>, if any.</param>
    public static SeedInfo Read(string serverFolder, long? lastKnown = null)
    {
        var props = new ServerPropertiesService().Read(Path.Combine(serverFolder, "server.properties"));
        var levelName = props.TryGetValue("level-name", out var n) && n.Length > 0 ? n : "world";
        var world = Path.Combine(serverFolder, levelName);

        var fromWorld = FromWorldFolder(world);
        if (fromWorld is not null) return new SeedInfo(fromWorld, SeedSource.World);

        if (lastKnown is not null) return new SeedInfo(lastKnown, SeedSource.Console);

        // Only a world that does not exist yet is waiting for level-seed. One that exists but could
        // not be read was generated with some seed already, and it may not be this one.
        if (!File.Exists(Path.Combine(world, "level.dat"))
            && props.TryGetValue("level-seed", out var asked)
            && ToNumeric(Unescape(asked)) is { } pending)
            return new SeedInfo(pending, SeedSource.Pending);

        return SeedInfo.None;
    }

    /// <summary>The seed stored in a world folder, trying each place it has lived; null if none.</summary>
    internal static long? FromWorldFolder(string world) =>
        NbtReader.ReadLong(Path.Combine(world, "data", "minecraft", "world_gen_settings.dat"), "data", "seed")
        ?? NbtReader.ReadLong(Path.Combine(world, "level.dat"), "Data", "WorldGenSettings", "seed")
        ?? NbtReader.ReadLong(Path.Combine(world, "level.dat"), "Data", "RandomSeed");

    /// <summary>
    /// The number Minecraft turns a typed seed into: the number itself, or else Java's
    /// <c>String.hashCode</c> of the text. Null for nothing typed, which means "random".
    /// </summary>
    public static long? ToNumeric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            return n;

        // String.hashCode over UTF-16 code units, with int overflow, exactly as Java does it.
        var hash = 0;
        foreach (var c in text) hash = unchecked(31 * hash + c);
        return hash;
    }

    /// <summary>
    /// A seed as it has to be written in server.properties, which is a Java properties file.
    /// </summary>
    /// <remarks>
    /// A backslash is that format's escape character, so one typed into a seed has to be doubled or
    /// the server would read a different seed. Anything outside ASCII is written as <c>\uXXXX</c>,
    /// which every version reads the same way whatever encoding it expects. Line breaks are dropped,
    /// so a pasted seed can never add a key of its own.
    /// </remarks>
    public static string EscapeForProperties(string seed)
    {
        var clean = ServerPropertiesService.SanitizeValue(seed.Trim());
        var sb = new StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            if (c == '\\') sb.Append(@"\\");
            else if (c > 127) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The inverse of <see cref="EscapeForProperties"/>, for reading level-seed back.</summary>
    internal static string Unescape(string value) =>
        EscapedChar().Replace(value, m => m.Groups[1].Success
            ? ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()
            : m.Groups[2].Value);

    /// <summary>
    /// The seed in the server's answer to the <c>seed</c> command, <c>Seed: [-123]</c>; null otherwise.
    /// </summary>
    public static long? FromConsoleLine(string line)
    {
        var m = SeedLine().Match(line);
        return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var seed) ? seed : null;
    }

    // After the log prefix, so a player typing "Seed: [1]" in chat does not count.
    [GeneratedRegex(@"\]: Seed: \[(-?\d{1,20})\]\s*$")]
    private static partial Regex SeedLine();

    [GeneratedRegex(@"\\(?:u([0-9a-fA-F]{4})|(.))")]
    private static partial Regex EscapedChar();
}
