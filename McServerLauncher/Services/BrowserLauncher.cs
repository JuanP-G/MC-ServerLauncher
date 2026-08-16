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
    /// <summary>True when <paramref name="url"/> is an absolute http(s) URL and is safe to open.</summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Opens the link if it is a web URL. Anything else is ignored, silently and on purpose.</summary>
    public static void Open(string? url)
    {
        if (!IsWebUrl(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // No browser registered (or the shell refused): nothing useful to do about it.
        }
    }
}
