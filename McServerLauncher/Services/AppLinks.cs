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
    public const string Releases = Repository + "/releases";
    public const string License = Repository + "/blob/main/LICENSE";
    public const string Notice = Repository + "/blob/main/NOTICE";
    public const string Website = "https://mc-server-launcher.vercel.app/";
    public const string PlayitTunnels = "https://playit.gg/account/tunnels";

    /// <summary>Mojang's own address for the Minecraft EULA, the one the server names in eula.txt.</summary>
    public const string MinecraftEula = "https://aka.ms/MinecraftEULA";

    /// <summary>The bug form in Spanish, in <c>.github/ISSUE_TEMPLATE/</c>.</summary>
    internal const string BugFormSpanish = "1-fallo.yml";

    /// <summary>The bug form in English, in <c>.github/ISSUE_TEMPLATE/</c>.</summary>
    internal const string BugFormEnglish = "3-bug.yml";

    /// <summary>
    /// The address of the bug form with the app's version and the operating system already filled in.
    /// </summary>
    /// <param name="version">The version running, as the release calls it ("1.21.6").</param>
    /// <param name="uiLanguage">Two-letter code of the app's language ("es", "en", "de"...).</param>
    /// <remarks>
    /// Spanish gets the Spanish form and every other language the English one: those are the two
    /// the repository has. <c>version</c> and <c>os</c> are the ids of the form's fields, which
    /// GitHub fills from the query string. Nothing leaves the machine until the person presses
    /// "Submit new issue" on a page they can read first, and these two values are all that is added.
    /// </remarks>
    public static string NewBugReport(string version, string uiLanguage)
    {
        var form = string.Equals(uiLanguage, "es", StringComparison.OrdinalIgnoreCase)
            ? BugFormSpanish
            : BugFormEnglish;
        return Repository + "/issues/new?template=" + form
            + "&version=" + Uri.EscapeDataString(version)
            + "&os=" + Uri.EscapeDataString(OperatingSystemName());
    }

    /// <summary>The name the bug form's operating-system list uses for this machine.</summary>
    internal static string OperatingSystemName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : "Linux";
}
