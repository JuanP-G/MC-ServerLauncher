using System.Net.Http;

namespace McServerLauncher.Services;

/// <summary>
/// The one place an <see cref="HttpClient"/> of this app is made.
/// </summary>
/// <remarks>
/// <para>
/// Seventeen services each built their own, and they had drifted: six sent
/// <c>JuanP-G/MC-ServerLauncher</c> as their User-Agent, <see cref="UpdateService"/> sent
/// <c>MC-ServerLauncher</c>, and the rest sent none. Some CDNs (Forge's maven among them) refuse a
/// request without one, and Modrinth asks for one that says who is calling.
/// </para>
/// <para>
/// Keeping the creation here is also what gives later rules a single home: a ceiling on how much a
/// response may weigh, or the hosts a download is allowed to come from, belong to every client or
/// to none.
/// </para>
/// <para>
/// Each service still keeps its own static client (one per service, with its own timeout), so
/// nothing is shared that did not need to be: a slow download cannot hold up the version check.
/// </para>
/// </remarks>
public static class AppHttp
{
    /// <summary>What every request says it is, so the services we call can tell who is asking.</summary>
    public const string UserAgent = "JuanP-G/MC-ServerLauncher";

    /// <summary>A client with this app's User-Agent and the given overall timeout.</summary>
    /// <param name="timeout">How long one request may take in all. Use
    /// <see cref="Timeout.InfiniteTimeSpan"/> when the caller sets its own deadline.</param>
    public static HttpClient Create(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }
}
