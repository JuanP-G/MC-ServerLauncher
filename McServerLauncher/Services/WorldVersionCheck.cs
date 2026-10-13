using System.IO;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Whether a world was last saved by a newer Minecraft than the server about to open it.
/// </summary>
/// <remarks>
/// <para>
/// Minecraft upgrades worlds, never the other way. Opened by an older version, every chunk whose
/// format it does not know loads as air ("Recoverable errors when loading section … Not a map"),
/// and the next save writes that air over the blocks. A world moved from 26.3 back to 26.1 lost its
/// spawn that way, with the app letting the start go ahead.
/// </para>
/// <para>
/// The version that last saved it is in <c>level.dat</c> (<c>Data.Version.Name</c>). It is compared
/// with the server's only when both are plain release numbers; a snapshot, a pre-release or a name
/// that cannot be read says "can't tell", and that never stops a start.
/// </para>
/// </remarks>
public static class WorldVersionCheck
{
    /// <summary>The version that last saved the world in <paramref name="worldDir"/>, or null.</summary>
    public static string? SavedBy(string worldDir) =>
        NbtReader.ReadString(Path.Combine(worldDir, "level.dat"), "Data", "Version", "Name");

    /// <summary>True only when both are release numbers and <paramref name="world"/> is the later one.</summary>
    public static bool IsNewer(string? world, string? server) =>
        Release(world) is { } w && Release(server) is { } s && w > s;

    /// <summary>
    /// The version that saved <paramref name="config"/>'s world, when it is newer than the server's;
    /// null otherwise, including when there is no world yet or it cannot be read.
    /// </summary>
    public static string? NewerThanServer(ServerConfig config)
    {
        try
        {
            var backups = new WorldBackupService();
            if (WorldBackupService.WorldFolderFor(config.FolderPath, backups.GetLevelName(config)) is not { } world)
                return null;

            var savedBy = SavedBy(world);
            return IsNewer(savedBy, config.GameVersion) ? savedBy : null;
        }
        catch
        {
            return null;   // can't tell: never a reason to keep a server down
        }
    }

    /// <summary>"26.3" or "1.21.4" as a comparable version; null for anything else.</summary>
    private static Version? Release(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (!trimmed.All(c => char.IsAsciiDigit(c) || c == '.')) return null;
        if (!Version.TryParse(trimmed.Contains('.') ? trimmed : trimmed + ".0", out var v)) return null;

        // "1.21" and "1.21.0" are the same release; Version would put the first one before.
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
