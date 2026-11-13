using System.Diagnostics;

namespace McServerLauncher.Services;

/// <summary>
/// Opens a link in the user's browser.
/// <para>
/// Everywhere else in the app the URL is a constant we wrote. In the store it comes from
/// Modrinth — an author fills in "source", "wiki" or "discord", and the long description is full
/// of links — so it is untrusted input. <c>UseShellExecute</c> hands whatever it is given to the
/// shell, which would happily run a local executable or a registered custom scheme, so only
/// absolute http/https URLs are ever passed through.
/// </para>
/// </summary>
public static class BrowserLauncher
{
    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL with no credentials in it and is
    /// safe to open.
    /// </summary>
    /// <remarks>
    /// <c>https://modrinth.com@evil.example/</c> reads as Modrinth and goes to the other host: the
    /// part before the <c>@</c> is a user name. A real link to a project page never has one.
    /// </remarks>
    public static bool IsWebUrl(string? url) =>
        TryNormalize(url, out _);

    private static bool TryNormalize(string? url, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length > 0) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Opens the link if it is a web URL. Anything else is ignored, silently and on purpose.
    /// </summary>
    /// <remarks>
    /// What the shell receives is the parsed address in its escaped form
    /// (<see cref="Uri.AbsoluteUri"/>), not the string that was checked: the two can differ in a
    /// space, a control character or an unescaped quote, and the shell sees only the second.
    /// </remarks>
    public static void Open(string? url)
    {
        if (!TryNormalize(url, out var safe)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = safe, UseShellExecute = true });
        }
        catch
        {
            // No browser registered (or the shell refused): nothing useful to do about it.
        }
    }
}
