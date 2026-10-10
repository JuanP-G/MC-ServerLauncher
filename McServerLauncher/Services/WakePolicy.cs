using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Who may wake a sleeping server by trying to join it.
/// </summary>
/// <remarks>
/// <para>
/// Pressing Join used to be enough for anyone at all. Behind a Playit tunnel that includes every
/// scanner on the internet that looks for Minecraft servers, and they try to log in as routine: each
/// one started the server, with a full backup of the world in front of the start.
/// </para>
/// <para>
/// The rule follows what the server itself would do. With its whitelist on, somebody not on it is
/// turned away the moment the server is up, so waking it for them buys nothing; operators get in
/// regardless in vanilla, so they count too. With the whitelist off the server is open to anyone,
/// and so is waking it. The owner can switch the filter off.
/// </para>
/// <para>
/// Pure on purpose: the listener, the files and the config are all supplied, so the decision can
/// be checked without a socket or a server folder.
/// </para>
/// </remarks>
public static partial class WakePolicy
{
    /// <summary>Whether a join attempt by <paramref name="player"/> should start the server.</summary>
    /// <param name="player">The name the client sent in Login Start, or null when it sent none.</param>
    /// <param name="whitelistOn">Whether <c>white-list=true</c> in server.properties.</param>
    /// <param name="onlyWhitelisted">The server's <see cref="Models.ServerConfig.WakeOnlyForWhitelist"/>.</param>
    /// <param name="whitelist">Names in whitelist.json.</param>
    /// <param name="ops">Names in ops.json.</param>
    public static bool Allows(string? player, bool whitelistOn, bool onlyWhitelisted,
        IEnumerable<string> whitelist, IEnumerable<string> ops)
    {
        if (!whitelistOn || !onlyWhitelisted) return true;

        // A client that sent no name, or one Minecraft would never accept, is not on any list.
        if (player is null || !PlayerName().IsMatch(player)) return false;

        return whitelist.Concat(ops).Any(n => string.Equals(n, player, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();
}
