using System.Globalization;
using Avalonia.Threading;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The signed notices file, from the window's point of view: when it is looked at, who it is read
/// for, the strip under the title bar and the notification an urgent notice may raise.
/// </summary>
/// <remarks>
/// <para>
/// What is downloaded, checked and kept is <see cref="NewsFeedService"/>; what is listed and what
/// counts as unread is <see cref="NewsBoard"/>; this is only the glue that needs the rest of the
/// window (the tunnels, the section on screen, the toasts).
/// </para>
/// <para>
/// Nothing here reaches the network before <see cref="Activate"/>, and nothing does at all while
/// "Show announcements" is off: no timer, no request. The request carries no version, language or
/// identifier; which notices are for this machine is worked out below, on this side.
/// </para>
/// </remarks>
public partial class MainViewModel
{
    /// <summary>
    /// How often the file is looked at while the app stays open. A look that finds nothing new is a
    /// 304 with no body (the file's ETag), a few hundred bytes, so five minutes is cheap and an urgent
    /// notice reaches an open app within that time.
    /// </summary>
    private static readonly TimeSpan NewsCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The timer skips its look when the copy was confirmed less than this ago, which is what happens
    /// when the window was brought back a minute before: two looks in a row would tell nothing new.
    /// </summary>
    internal static readonly TimeSpan NewsTickAge = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Opening the section, or coming back to the window, looks again only if the copy was last
    /// confirmed longer ago than this: switching between windows must not make a request each time.
    /// </summary>
    internal static readonly TimeSpan NewsFreshAge = TimeSpan.FromMinutes(2);

    /// <summary>The longest a toast's message runs; the rest is in Novedades.</summary>
    private const int NewsToastMaxChars = 160;

    /// <summary>Replaces the check "does this machine use tunnels". A seam: the real one reads the machine's Playit agent file.</summary>
    internal Func<bool>? UsesTunnelsOverride { get; set; }

    /// <summary>Whether the window is out of the user's sight. A seam: a test has no window to ask.</summary>
    internal Func<bool> WindowInactive { get; set; } = () => ToastService.MainWindowInactive;

    /// <summary>Raises a notification: heading, source, message, icon and level. A seam: a test must not open a toast.</summary>
    internal Action<string, string, string, FluentIcons.Common.Icon, NotificationLevel> ToastSink { get; set; } =
        (heading, source, message, icon, level) =>
            ToastService.Shared.Notify(heading, source, message, icon, level, NotificationPreferences.Global);

    /// <summary>True once <see cref="StartNews"/> has run: nothing about the notices reaches the network before.</summary>
    private bool _newsStarted;

    /// <summary>
    /// Whether the strip of an urgent notice is on screen now: there is a notice going on that the user
    /// has not closed, and the place they chose for the strip is the one they are in.
    /// </summary>
    public bool ShowNewsBand => News.HasBand && _appSettings.NewsStrip switch
    {
        NewsStripStyle.Everywhere => true,
        NewsStripStyle.TunnelsOnly => IsTunnelsSection,
        _ => false,
    };

    /// <summary>
    /// Starts the looking at the notices file (when announcements are on). Called from
    /// <see cref="Activate"/>, never from the constructor.
    /// </summary>
    internal void StartNews()
    {
        if (_newsStarted) return;
        _newsStarted = true;
        ApplyNewsFeedSetting();
    }

    /// <summary>A setting of the announcements moved: what the section shows follows it, and so does the looking.</summary>
    internal void OnNewsSettingsChanged()
    {
        News.OnSettingsChanged();
        OnPropertyChanged(nameof(ShowNewsBand));
        ApplyNewsFeedSetting();
    }

    /// <summary>
    /// Switches the looking on or off to match "Show announcements". Called when the window is up and
    /// every time the switch moves; before <see cref="StartNews"/> it does nothing.
    /// </summary>
    private void ApplyNewsFeedSetting()
    {
        if (!_newsStarted) return;

        if (!_appSettings.ShowAnnouncements)
        {
            _newsTimer.Stop();
            return;
        }

        // The copy on disk first, so the section and the strip have something the moment the window is up.
        ApplyNewsFeed();
        if (_newsTimer.IsEnabled) return;
        _newsTimer.Start();
        _ = CheckNewsAsync();
    }

    /// <summary>One look at the file. Never asks with the announcements off.</summary>
    /// <param name="onlyIfOlderThan">See <see cref="NewsFeedService.RefreshAsync"/>; null always asks.</param>
    internal async Task CheckNewsAsync(TimeSpan? onlyIfOlderThan = null)
    {
        if (!_appSettings.ShowAnnouncements) return;
        await _newsFeed.RefreshAsync(onlyIfOlderThan);
        ApplyNewsFeed();
    }

    /// <summary>The section was opened: the list is worked out again and the file looked at if it is not recent.</summary>
    private void OnNewsSectionOpened()
    {
        if (!_newsStarted || !_appSettings.ShowAnnouncements) return;
        ApplyNewsFeed();
        _ = CheckNewsAsync(NewsFreshAge);
    }

    /// <summary>
    /// The window came back to the front (from another app, the taskbar or the tray): the file is looked
    /// at if the copy is not recent, so a notice that came while the user was away shows at once.
    /// </summary>
    internal void OnWindowActivated()
    {
        if (!_newsStarted || !_appSettings.ShowAnnouncements) return;
        _ = CheckNewsAsync(NewsFreshAge);
    }

    /// <summary>Hands the screen what the copy held says, and how the last look ended.</summary>
    private void ApplyNewsFeed()
    {
        if (!_appSettings.ShowAnnouncements) return;

        News.SetNotices(_newsFeed.Feed?.Items ?? [], NewsContextNow());
        News.SetFeedState(_newsFeed.LastStatus, _newsFeed.CheckedAt?.UtcDateTime);
        NotifyNewsOnce();
    }

    /// <summary>
    /// What decides which notices are for this machine. Worked out here, never sent: the file is the
    /// same for everybody and the choosing happens on the reader's side.
    /// </summary>
    private NewsContext NewsContextNow() => new(NewsContext.CurrentOs(), Changelog.Running(), UsesTunnels(),
        NewsLanguageFor(CultureInfo.CurrentUICulture));

    /// <summary>
    /// The language a notice is read in: the one the window itself is in. A system language the app has
    /// no translation for shows the window in Spanish (the neutral resources), so the notices must be in
    /// Spanish too, not in the English the file falls back to when it has no text for the asked language.
    /// </summary>
    internal static string NewsLanguageFor(CultureInfo culture)
    {
        var code = culture.TwoLetterISOLanguageName;
        return NewsFeedParser.Languages.Contains(code) ? code : "es";
    }

    /// <summary>
    /// Whether this machine uses Playit tunnels: it has an account (the app's or the installed agent's)
    /// or a server that is set to use one. The agent being connected right now does not matter: a notice
    /// about an outage is for whoever depends on the service, up or down.
    /// </summary>
    private bool UsesTunnels() => UsesTunnelsOverride?.Invoke()
        ?? (PlayitConnection.Sources(_appSettings).Count > 0 || Servers.Any(s => s.Config.PlayitEnabled));

    /// <summary>
    /// Raises a desktop notification for each urgent notice that is new (and for an incident the user was
    /// told about that has now ended), and records that it was handled so it never happens twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notice is recorded as handled whether or not a toast comes of it: one the user chose not to
    /// get, or one the strip already put in front of them, must not turn up later when the window goes
    /// out of sight. A resolved notice has no strip, so it is always worth saying.
    /// </para>
    /// <para>
    /// The master switch for notifications silences this like everything else; so does the user's own
    /// setting for these. Security notices have no switch of their own beyond "Show announcements".
    /// </para>
    /// </remarks>
    private void NotifyNewsOnce()
    {
        foreach (var row in News.PendingToasts.ToList())
        {
            // False when this very notice was handled a moment ago: the list being walked is a snapshot,
            // and another pass (a setting that moved while a look was ending) may have got here first.
            if (!News.MarkToasted(row.Key)) continue;

            if (!_appSettings.NewsToast || !NotificationPreferences.Global.Enabled) continue;

            var stripSaysIt = ShowNewsBand && !row.Resolved && News.BandKey == row.Key;
            if (stripSaysIt && !WindowInactive()) continue;

            var (icon, level) = row.Resolved ? (FluentIcons.Common.Icon.CheckmarkCircle, NotificationLevel.Success)
                : row.Item.Kind == NewsKind.Security ? (FluentIcons.Common.Icon.ShieldError, NotificationLevel.Error)
                : (FluentIcons.Common.Icon.Warning, NotificationLevel.Warning);

            var message = row.Resolved ? Localizer.Get("News_ToastResolved")
                : row.Text.Body.Length > 0 ? Shorten(row.Text.Body, NewsToastMaxChars)
                : Localizer.Get("News_ToastSeeNews");

            // The same source as the update notification: it belongs to the app, not to a server.
            ToastSink(row.Text.Title, Localizer.Get("Notif_UpdateTitle"), message, icon, level);
        }
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
