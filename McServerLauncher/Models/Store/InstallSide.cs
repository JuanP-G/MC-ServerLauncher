namespace McServerLauncher.Models.Store;

/// <summary>
/// Where a project does something, as far as the store says.
/// </summary>
/// <remarks>
/// Four answers instead of Modrinth's two-by-four table of <c>client_side</c> and
/// <c>server_side</c>, because what the app does with a project only depends on these: a
/// <see cref="ClientOnly"/> mod does nothing on a server, so it goes to the players' pack rather
/// than into the folder the server loads; everything else is installed on the server as always.
/// </remarks>
public enum InstallSide
{
    /// <summary>The store does not say, or says only half of it. Nothing is claimed.</summary>
    Unknown,

    /// <summary>Runs on the server and on the client (or on either).</summary>
    Both,

    /// <summary>Runs on the server only; players need nothing.</summary>
    ServerOnly,

    /// <summary>Runs on the client only: Sodium, MaLiLib, a minimap. Does nothing on a server.</summary>
    ClientOnly
}

/// <summary>
/// Which of the store's projects the browser lists.
/// </summary>
public enum StoreSideFilter
{
    /// <summary>Everything that fits the loader and the version, client-only mods included.</summary>
    All,

    /// <summary>What can run on the server: everything but the client-only mods.</summary>
    ServerSide,

    /// <summary>Only the client-only mods, for building the players' pack.</summary>
    ClientOnly
}
