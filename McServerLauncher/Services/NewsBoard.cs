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

/// <summary>What the Novedades screen shows: the pending update, the versions, and what is unread.</summary>
public sealed record NewsBoardModel(NewsOffer? Offer, IReadOnlyList<NewsVersionRow> Versions, IReadOnlyList<string> UnreadKeys)
{
    public int Unread => UnreadKeys.Count;
}

/// <summary>
/// Decides what Novedades lists and what is unread, from facts: no screen, no clock and no disk.
/// </summary>
public static class NewsBoard
{
    public static string UpdateKey(string version) => "update:" + version;

    /// <summary>The board for what is known now.</summary>
    /// <param name="releases">Every release this build knows, newest first (<see cref="Changelog.All"/>).</param>
    /// <param name="lastSeen">The version the user had seen before this run: the ones above it are new.
    /// Null on a new install, where nothing is.</param>
    /// <param name="offeredVersion">A newer version the update check found; null when there is none.</param>
    /// <param name="offeredIsBeta">Whether that version is a pre-release.</param>
    /// <param name="isRead">Whether a key has been read (<see cref="NewsState.IsRead"/>).</param>
    public static NewsBoardModel Build(IReadOnlyList<Changelog.Release> releases, Version? lastSeen,
        string? offeredVersion, bool offeredIsBeta, Func<string, bool> isRead)
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

        return new NewsBoardModel(offer, versions, unread);
    }
}
