namespace McServerLauncher.Services;

/// <summary>What a notice is about, which decides how loudly it speaks.</summary>
public enum NewsKind
{
    /// <summary>Something outside the app is failing (Playit's edge, say). Speaks: banner and notification.</summary>
    Incident,

    /// <summary>A security matter. Speaks like an incident.</summary>
    Security,

    /// <summary>A hint or a piece of news. Only listed in Novedades: never a banner, never a notification.</summary>
    Tip,
}

/// <summary>Whether the thing a notice reports is still going on.</summary>
public enum NewsStatus
{
    Active,

    /// <summary>Over. Stays listed in green for a week, then is retired.</summary>
    Resolved,
}

/// <summary>A notice's words in one language.</summary>
public sealed record NewsText(string Title, string Body);

/// <summary>
/// Who a notice is for. Every condition left out is one that does not narrow it, so an item with no
/// <c>only</c> block reaches everybody.
/// </summary>
/// <param name="UsesTunnels">True: only whoever uses Playit tunnels. False: only whoever does not. Null: both.</param>
/// <param name="Os">The systems it is for, lower case (<c>windows</c>, <c>linux</c>, <c>macos</c>); empty for all.</param>
/// <param name="MinVersion">The oldest app version it applies to, inclusive; null for no limit.</param>
/// <param name="MaxVersion">The newest app version it applies to, inclusive; null for no limit.</param>
public sealed record NewsAudience(bool? UsesTunnels, IReadOnlyList<string> Os, Version? MinVersion, Version? MaxVersion)
{
    public static NewsAudience Everyone { get; } = new(null, [], null, null);

    /// <summary>Whether a machine like <paramref name="context"/> is among the people the notice is for.</summary>
    public bool Matches(NewsContext context)
    {
        if (UsesTunnels is { } tunnels && tunnels != context.UsesTunnels) return false;
        if (Os.Count > 0 && !Os.Contains(context.Os, StringComparer.OrdinalIgnoreCase)) return false;
        if (MinVersion is not null && context.AppVersion < MinVersion) return false;
        if (MaxVersion is not null && context.AppVersion > MaxVersion) return false;
        return true;
    }
}

/// <summary>
/// What the app knows about the machine it runs on, for deciding who a notice is for. Worked out
/// here and never sent anywhere: the file is the same for everybody and the choosing happens on the
/// reader's side.
/// </summary>
/// <param name="Os"><c>windows</c>, <c>linux</c> or <c>macos</c>.</param>
/// <param name="AppVersion">The version running.</param>
/// <param name="UsesTunnels">Whether this machine has a Playit account or a server with a tunnel.</param>
/// <param name="Language">Two-letter code of the language the app is shown in.</param>
public sealed record NewsContext(string Os, Version AppVersion, bool UsesTunnels, string Language)
{
    /// <summary>The system this process runs on, as the file spells it.</summary>
    public static string CurrentOs() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}

/// <summary>One notice of the file, already cleaned and bounded: nothing here is as the server sent it.</summary>
/// <param name="Id">Names the notice for ever: what is read, closed and notified is remembered by it.</param>
/// <param name="Kind">What it is about.</param>
/// <param name="Status">Whether it is still going on.</param>
/// <param name="From">Not shown before this moment.</param>
/// <param name="Until">Not shown after it. Always set for an incident or a security notice; a tip may have none.</param>
/// <param name="ResolvedAt">When it was resolved. Set exactly when <paramref name="Status"/> is <see cref="NewsStatus.Resolved"/>.</param>
/// <param name="Only">Who it is for.</param>
/// <param name="Link">Where "more information" goes: https, on a short list of hosts; null for none.</param>
/// <param name="Text">Its words, by two-letter language code.</param>
public sealed record NewsItem(string Id, NewsKind Kind, NewsStatus Status, DateTimeOffset From,
    DateTimeOffset? Until, DateTimeOffset? ResolvedAt, NewsAudience Only, string? Link,
    IReadOnlyDictionary<string, NewsText> Text)
{
    /// <summary>How long a resolved incident stays listed before it is retired.</summary>
    public static readonly TimeSpan ResolvedStays = TimeSpan.FromDays(7);

    /// <summary>Incidents and security notices: the two that may speak up.</summary>
    public bool IsUrgent => Kind is NewsKind.Incident or NewsKind.Security;

    /// <summary>The key under which it is remembered as read or notified: the status is part of it, so a resolved incident is news again.</summary>
    public string Key => NewsBoard.NoticeKey(Id, Status);

    /// <summary>When it stops being listed; null for never.</summary>
    public DateTimeOffset? EndsAt
    {
        get
        {
            if (Status == NewsStatus.Resolved && ResolvedAt is { } resolved)
            {
                var retired = resolved + ResolvedStays;
                return Until is { } until && until < retired ? until : retired;
            }
            return Until;
        }
    }

    /// <summary>Whether it is within its dates at <paramref name="now"/>.</summary>
    public bool IsListedAt(DateTimeOffset now) => From <= now && (EndsAt is not { } end || now < end);

    /// <summary>
    /// The words in <paramref name="language"/>; failing that in English, failing that in whichever
    /// language the file has. Null only for an item with no text at all, which the parser never lets through.
    /// </summary>
    public NewsText? TextFor(string language)
    {
        if (Text.TryGetValue(language, out var exact)) return exact;
        if (Text.TryGetValue("en", out var english)) return english;
        foreach (var code in NewsFeedParser.Languages)
            if (Text.TryGetValue(code, out var any)) return any;
        return Text.Values.FirstOrDefault();
    }
}

/// <summary>The notices file, as read.</summary>
/// <param name="Seq">Grows with every version of the file; a smaller one than already accepted is a replay.</param>
/// <param name="Issued">When this version of the file was written.</param>
/// <param name="Items">The notices that were valid.</param>
public sealed record NewsFeed(int Seq, DateTimeOffset Issued, IReadOnlyList<NewsItem> Items);
