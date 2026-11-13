using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// Where a server keeps its player lists: <c>ops.json</c>, <c>whitelist.json</c>,
/// <c>banned-players.json</c> and <c>usercache.json</c>.
/// </summary>
/// <remarks>
/// A Java server keeps them in its root. Pumpkin keeps the same four files, in the same format (an
/// array of <c>{ "uuid", "name", … }</c>), inside <c>data/</c>. Asked here once, because the other
/// way round is a quiet failure: the app would read four files that are not there, show every list
/// empty, and write a whitelist the server never opens.
/// </remarks>
public static class PlayerFiles
{
    /// <summary>The full path of one of the lists for the server in <paramref name="serverFolder"/>.</summary>
    public static string PathOf(string serverFolder, string fileName) =>
        Path.Combine(FolderOf(serverFolder), fileName);

    /// <summary>The folder the lists are in.</summary>
    public static string FolderOf(string serverFolder) =>
        PumpkinConfig.IsServerFolder(serverFolder) ? Path.Combine(serverFolder, "data") : serverFolder;
}
