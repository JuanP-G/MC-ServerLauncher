namespace McServerLauncher.Services;

/// <summary>What a row of the versions list says about its release, besides its version.</summary>
public enum NewsTag
{
    None,

    /// <summary>The version running now.</summary>
    Installed,

    /// <summary>A version that arrived with the last update, not seen before.</summary>
    New,
}

/// <summary>One release in the versions list.</summary>
/// <param name="Release">The release and, when asked for, its notes.</param>
/// <param name="Tag">What to say about it next to the version.</param>
/// <param name="Open">Whether the row starts unfolded.</param>
public sealed record NewsVersionRow(Changelog.Release Release, NewsTag Tag, bool Open);

/// <summary>A version offered by the update check, which the user has not installed yet.</summary>
/// <param name="Version">As GitHub names it: "1.23.0".</param>
/// <param name="IsBeta">Whether it is a pre-release.</param>
/// <param name="Unread">Whether the section has not been opened since it appeared.</param>
public sealed record NewsOffer(string Version, bool IsBeta, bool Unread)
{
    /// <summary>The key under which it is remembered as read.</summary>
    public string Key => NewsBoard.UpdateKey(Version);
}

/// <summary>One notice in the list, with its words already chosen in the language of the app.</summary>
/// <param name="Item">The notice.</param>
/// <param name="Text">Its title and body in the app's language (or the fallback).</param>
/// <param name="Unread">Whether the user has not read it in its present status.</param>
public sealed record NewsNoticeRow(NewsItem Item, NewsText Text, bool Unread)
{
    /// <summary>The key under which it is remembered as read or announced.</summary>
    public string Key => Item.Key;

    public bool Resolved => Item.Status == NewsStatus.Resolved;
}

/// <summary>
/// What the board needs to put notices on it: the file's items, who is asking, and what the user
/// asked to see. Passed whole so a caller with announcements off passes nothing.
/// </summary>
/// <param name="Items">The notices of the file.</param>
/// <param name="Context">The machine and the language the notices are filtered and written for.</param>
/// <param name="Now">The time; a parameter so a test need not wait for a notice to expire.</param>
/// <param name="Incidents">Whether incidents are wanted. Security notices ignore this: only the master
/// switch for announcements silences them.</param>
/// <param name="Tips">Whether tips are wanted.</param>
/// <param name="IsClosed">Whether the strip of the notice with this id was closed (<see cref="NewsState.IsClosed"/>).</param>
/// <param name="WasToasted">Whether the notice under this key was announced (<see cref="NewsState.WasToasted"/>).</param>
public sealed record NewsNoticeInput(IReadOnlyList<NewsItem> Items, NewsContext Context, DateTimeOffset Now,
    bool Incidents, bool Tips, Func<string, bool> IsClosed, Func<string, bool> WasToasted);

/// <summary>What the Novedades screen shows: the pending update, the notices, the versions, and what is unread.</summary>
public sealed record NewsBoardModel(NewsOffer? Offer, IReadOnlyList<NewsVersionRow> Versions, IReadOnlyList<string> UnreadKeys)
{
    /// <summary>
    /// The notices that apply to this machine and are within their dates: what is still going on first
    /// (the urgent ones, then the tips, newest first), then what was resolved, newest first.
    /// </summary>
    public IReadOnlyList<NewsNoticeRow> Notices { get; init; } = [];

    /// <summary>
    /// The notice the strip is for: the newest urgent one still going on that the user has neither read
    /// nor closed. Reading it (the section was opened, or "Mark all as read") takes the strip away: the
    /// user has seen what it says. Null when there is none. Whether the strip shows at all, and where,
    /// is the user's setting.
    /// </summary>
    public NewsNoticeRow? Band { get; init; }

    /// <summary>
    /// The notices that should be announced with a notification now: urgent ones never announced, and
    /// an incident that ended after the user was told about it. A tip is never here.
    /// </summary>
    public IReadOnlyList<NewsNoticeRow> Toasts { get; init; } = [];

    public int Unread => UnreadKeys.Count;
}

/// <summary>
/// Decides what Novedades lists and what is unread, from facts: no screen, no clock and no disk.
/// </summary>
public static class NewsBoard
{
    public static string UpdateKey(string version) => "update:" + version;

    /// <summary>
    /// The key a notice is remembered under: its id and its status, so an incident that is marked
    /// resolved is a new thing to read (and to be told about) and not the one already read.
    /// </summary>
    public static string NoticeKey(string id, NewsStatus status) =>
        id + (status == NewsStatus.Resolved ? ":resolved" : ":active");

    /// <summary>The board for what is known now.</summary>
    /// <param name="releases">Every release this build knows, newest first (<see cref="Changelog.All"/>).</param>
    /// <param name="lastSeen">The version the user had seen before this run: the ones above it are new.
    /// Null on a new install, where nothing is.</param>
    /// <param name="offeredVersion">A newer version the update check found; null when there is none.</param>
    /// <param name="offeredIsBeta">Whether that version is a pre-release.</param>
    /// <param name="isRead">Whether a key has been read (<see cref="NewsState.IsRead"/>).</param>
    /// <param name="notices">The notices of the signed file and what decides who sees them; null for
    /// none, which is what announcements switched off means.</param>
    public static NewsBoardModel Build(IReadOnlyList<Changelog.Release> releases, Version? lastSeen,
        string? offeredVersion, bool offeredIsBeta, Func<string, bool> isRead, NewsNoticeInput? notices = null)
    {
        var fresh = lastSeen is null
            ? new HashSet<Version>()
            : releases.Where(r => r.Version > lastSeen).Select(r => r.Version).ToHashSet();

        var versions = new List<NewsVersionRow>(releases.Count);
        for (var i = 0; i < releases.Count; i++)
        {
            var release = releases[i];
            var isFresh = fresh.Contains(release.Version);
            var tag = isFresh ? NewsTag.New : i == 0 ? NewsTag.Installed : NewsTag.None;

            // What arrived with the update is what the user came to read; with nothing new, the
            // version running is the one worth having open.
            var open = isFresh || (fresh.Count == 0 && i == 0);
            versions.Add(new NewsVersionRow(release, tag, open));
        }

        NewsOffer? offer = null;
        var unread = new List<string>();
        if (!string.IsNullOrWhiteSpace(offeredVersion))
        {
            var key = UpdateKey(offeredVersion);
            var isUnread = !isRead(key);
            offer = new NewsOffer(offeredVersion, offeredIsBeta, isUnread);
            if (isUnread) unread.Add(key);
        }

        if (notices is null) return new NewsBoardModel(offer, versions, unread);

        var rows = NoticeRows(notices, isRead);
        unread.AddRange(rows.Where(r => r.Unread).Select(r => r.Key));

        return new NewsBoardModel(offer, versions, unread)
        {
            Notices = rows,
            Band = rows.FirstOrDefault(r => r.Item.IsUrgent && !r.Resolved && r.Unread && !notices.IsClosed(r.Item.Id)),
            Toasts = rows.Where(r => ShouldAnnounce(r, notices)).ToList(),
        };
    }

    /// <summary>Whether a notification is owed for <paramref name="row"/>: it speaks, and nobody was told yet.</summary>
    private static bool ShouldAnnounce(NewsNoticeRow row, NewsNoticeInput input)
    {
        if (!row.Item.IsUrgent || input.WasToasted(row.Key)) return false;

        // A strip the user closed is something they know about: no notification about it afterwards...
        if (!row.Resolved) return !input.IsClosed(row.Item.Id);

        // ...but the end of an incident they were told about is news, and one they were never told about is not.
        return input.WasToasted(NoticeKey(row.Item.Id, NewsStatus.Active));
    }

    /// <summary>
    /// The notices to list: the ones within their dates, meant for this machine, of a kind the user
    /// wants and with words in some language; what is still going on first, then what ended.
    /// </summary>
    private static List<NewsNoticeRow> NoticeRows(NewsNoticeInput input, Func<string, bool> isRead)
    {
        var rows = new List<NewsNoticeRow>();
        foreach (var item in input.Items)
        {
            if (!item.IsListedAt(input.Now) || !item.Only.Matches(input.Context)) continue;
            if (item.Kind == NewsKind.Incident && !input.Incidents) continue;
            if (item.Kind == NewsKind.Tip && !input.Tips) continue;
            if (item.TextFor(input.Context.Language) is not { } text) continue;

            rows.Add(new NewsNoticeRow(item, text, !isRead(item.Key)));
        }

        // Still going on before ended; among those, the urgent ones before the tips; newest first.
        return rows
            .OrderBy(r => r.Resolved)
            .ThenBy(r => r.Item.IsUrgent ? 0 : 1)
            .ThenByDescending(r => r.Item.From)
            .ThenBy(r => r.Item.Id, StringComparer.Ordinal)
            .ToList();
    }
}
