namespace McServerLauncher.Services;

/// <summary>
/// The places this app points people at, in one list.
/// </summary>
/// <remarks>
/// The repository address used to appear only inside request headers and the update check, so
/// there was nowhere for the About screen to take it from. Opened through
/// <see cref="BrowserLauncher"/>, which refuses anything that is not an absolute http(s) link.
/// </remarks>
public static class AppLinks
{
    public const string Repository = "https://github.com/JuanP-G/MC-ServerLauncher";
    public const string NewIssue = Repository + "/issues/new";
    public const string Releases = Repository + "/releases";
    public const string License = Repository + "/blob/main/LICENSE";
    public const string Notice = Repository + "/blob/main/NOTICE";
    public const string Website = "https://mc-server-launcher.vercel.app/";
    public const string PlayitTunnels = "https://playit.gg/account/tunnels";
}
