using System.Collections.Generic;
using System.IO;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// The jars a server's players need and the server must not load: Sodium, MaLiLib, a minimap.
/// </summary>
/// <remarks>
/// <para>
/// A client-only mod does nothing on a server. Fabric skips it, but Forge and NeoForge can refuse to
/// start over it, and either way it is a file the server carries for no reason. So the store keeps
/// them apart: they are downloaded, verified and listed like any other mod, into a folder of their
/// own, and the export puts them in the pack the players are handed.
/// </para>
/// <para>
/// The folder lives under the app's data, <c>content\&lt;serverId&gt;\client</c>, next to the
/// server's ledger and for the same reason: it is the app's view of the server, so it neither
/// travels with the world nor fills its backups, the server never reads it, and removing the server
/// removes it too (<see cref="ContentLedgerStore.PruneOrphans"/> clears the whole id folder).
/// </para>
/// </remarks>
public static class ClientPack
{
    /// <summary>Where this server's players' jars are kept.</summary>
    public static string FolderOf(ServerConfig config) =>
        Path.Combine(ContentLedgerStore.RootDirectory, config.Id, "client");

    /// <summary>Every jar in the pack, by file name. Empty when there is no folder yet.</summary>
    public static IReadOnlyList<string> Jars(ServerConfig config)
    {
        var folder = FolderOf(config);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.jar")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
    }
}
