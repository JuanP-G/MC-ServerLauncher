namespace McServerLauncher.Services;

/// <summary>
/// The hosts each kind of download is allowed to come from.
/// </summary>
/// <remarks>
/// <para>
/// A download link that arrives inside an API response says where the file is, and nothing in a
/// response should be able to say somewhere other than the publisher's own servers: the update and
/// the JRE already refused that (<see cref="AppHttp.RequireAllowedUrl"/>), while a mod, a Paper
/// or Mojang server jar and Geyser's artifacts followed whatever address they were given. The lists
/// live here, in one file, so that what each source is trusted for can be read in one place and a
/// new source cannot be added without deciding it.
/// </para>
/// <para>
/// A host entry also covers its subdomains (<c>modrinth.com</c> is <c>cdn.modrinth.com</c>). The check
/// is made on the address that was asked for and again on the one the answer came from, after any
/// redirects (<see cref="AppHttp.RequireAllowedFinalUrl"/>): a redirect is how a trusted host
/// would hand the app to an untrusted one.
/// </para>
/// </remarks>
public static class TrustedHosts
{
    /// <summary>Modrinth's API and its CDN, which serves every mod and plugin file and every image.</summary>
    public static readonly string[] Modrinth = ["modrinth.com"];

    /// <summary>
    /// Mojang's launcher metadata and the server jars (<c>piston-meta</c>, <c>piston-data</c>, and the
    /// older <c>launchermeta</c> and <c>launcher</c> hosts that old version entries still name).
    /// </summary>
    public static readonly string[] Mojang = ["mojang.com"];

    /// <summary>PaperMC's API and the host its build files are served from.</summary>
    public static readonly string[] Paper = ["papermc.io"];

    /// <summary>Purpur's API, which also serves the jars.</summary>
    public static readonly string[] Purpur = ["purpurmc.org"];

    /// <summary>GeyserMC's downloads API, which also serves Geyser, Floodgate and Hydraulic.</summary>
    public static readonly string[] GeyserMc = ["geysermc.org"];

    /// <summary>
    /// Where store images may come from: Modrinth's CDN for icons and galleries, and the avatar
    /// service for player heads. A description can name any address, and nothing a description says
    /// is fetched.
    /// </summary>
    public static readonly string[] Images = ["modrinth.com", "mc-heads.net"];

    /// <summary>
    /// Where a GitHub release's file is served from. The asset link is on <c>github.com</c> (checked
    /// when the release is read) and GitHub always answers it with a redirect to its own download
    /// host, so the host the answer comes from has to be allowed as well.
    /// </summary>
    public static readonly string[] GitHubReleaseAssets = ["github.com", "githubusercontent.com"];
}
