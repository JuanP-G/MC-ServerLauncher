using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>One release in the versions list: a title that folds open onto its notes.</summary>
public partial class NewsVersionRowViewModel : ObservableObject
{
    private readonly Changelog.Release _release;

    public NewsVersionRowViewModel(NewsVersionRow row)
    {
        _release = row.Release;
        _tag = row.Tag;
        _isExpanded = row.Open;
    }

    private readonly NewsTag _tag;

    public string Title => Localizer.Format("News_VersionFmt", _release.Label);

    /// <summary>"Installed", "New" or nothing.</summary>
    public string TagText => _tag switch
    {
        NewsTag.Installed => Localizer.Get("News_TagInstalled"),
        NewsTag.New => Localizer.Get("News_TagNew"),
        _ => string.Empty,
    };

    public bool HasTag => _tag != NewsTag.None;
    public bool IsNew => _tag == NewsTag.New;
    public bool IsInstalled => _tag == NewsTag.Installed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notes))]
    private bool _isExpanded;

    /// <summary>
    /// The notes, looked up only while the row is open: the list has about fifty versions and
    /// nobody reads them all at once.
    /// </summary>
    public string Notes => IsExpanded ? _release.Notes : string.Empty;
}

/// <summary>One notice of the signed file, as the list shows it: a card with a coloured ring, a title and a few words.</summary>
public sealed partial class NewsNoticeViewModel : ObservableObject
{
    private readonly NewsNoticeRow _row;
    private readonly Action<string?> _openLink;

    public NewsNoticeViewModel(NewsNoticeRow row, bool marked, Action<string?> openLink)
    {
        _row = row;
        _openLink = openLink;
        Marked = marked;
    }

    public string Title => _row.Text.Title;
    public string Body => _row.Text.Body;
    public bool HasBody => _row.Text.Body.Length > 0;

    public bool IsIncident => _row.Item.Kind == NewsKind.Incident;
    public bool IsSecurity => _row.Item.Kind == NewsKind.Security;
    public bool IsTip => _row.Item.Kind == NewsKind.Tip;
    public bool IsResolved => _row.Resolved;

    // What the ring and the icon show: what is still going on takes the colour of its kind (amber for an
    // incident, red for a security matter, blue for a tip); what ended is green whatever it was.
    public bool IsWarn => IsIncident && !IsResolved;
    public bool IsDanger => IsSecurity && !IsResolved;
    public bool ShowTipIcon => IsTip;

    /// <summary>"Incident", "Security" or "Tip".</summary>
    public string KindText => _row.Item.Kind switch
    {
        NewsKind.Incident => Localizer.Get("News_KindIncident"),
        NewsKind.Security => Localizer.Get("News_KindSecurity"),
        _ => Localizer.Get("News_KindTip"),
    };

    /// <summary>"Resolved" for what ended, nothing for what is going on.</summary>
    public string StatusText => IsResolved ? Localizer.Get("News_StatusResolved") : string.Empty;

    /// <summary>Whether it still carries the mark of being unread (see the remarks of <see cref="NewsViewModel"/>).</summary>
    public bool Marked { get; }

    public bool HasLink => _row.Item.Link is not null;

    /// <summary>Opens the notice's own page in the browser.</summary>
    [RelayCommand]
    private void OpenLink() => _openLink(_row.Item.Link);
}

/// <summary>
/// Novedades: the update waiting to be installed, the notices of the signed file and the history of
/// versions, plus the mark the rail wears while something in here is unread.
/// </summary>
/// <remarks>
/// <para>
/// What is listed and what counts as unread is decided in <see cref="NewsBoard"/>; this only holds it
/// for the screen and for the rail.
/// </para>
/// <para>
/// Opening the section reads what was unread, so the mark on the rail goes away at once — but the
/// entries keep their own mark until the section is left or "mark all as read" is pressed, or the
/// eye would have nothing to land on in a list that has just been opened.
/// </para>
/// </remarks>
public partial class NewsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly NewsState _state;
    private readonly Action _saveSettings;
    private readonly Action _openSettings;

    /// <summary>What the user had seen before this run: the versions above it are new.</summary>
    private readonly Version? _seenBefore;

    private readonly IReadOnlyList<Changelog.Release> _releases;

    private string? _offerVersion;
    private bool _offerIsBeta;
    private NewsBoardModel _board;

    private readonly Func<DateTimeOffset> _clock;

    /// <summary>The notices of the last good copy of the file, and who they are filtered for; null until there are any.</summary>
    private IReadOnlyList<NewsItem>? _items;
    private NewsContext? _context;

    private NewsFeedStatus? _feedStatus;
    private DateTime? _feedCheckedUtc;
    private bool _copyExpired;

    /// <summary>Opens a notice's link. A seam: a test must not start a browser.</summary>
    internal Action<string?> OpenLink { get; set; } = BrowserLauncher.Open;

    /// <summary>The keys that were unread when the section was opened, kept marked while it stays open.</summary>
    private readonly HashSet<string> _visitMarks = new(StringComparer.Ordinal);

    /// <param name="settings">The settings the app runs on; <see cref="AppSettings.LastVersionSeen"/> is
    /// read now, before the start-up flow updates it.</param>
    /// <param name="state">What has been read.</param>
    /// <param name="updateNow">The command that installs the offered version.</param>
    /// <param name="saveSettings">Writes the settings.</param>
    /// <param name="openSettings">Takes the user to the notices page of Settings.</param>
    /// <param name="current">The running version; null for this build. Tests pass their own.</param>
    /// <param name="clock">The time, for which notices are within their dates; null for the real one.</param>
    public NewsViewModel(AppSettings settings, NewsState state, ICommand updateNow, Action saveSettings,
        Action openSettings, Version? current = null, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _state = state;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        UpdateNowCommand = updateNow;
        _saveSettings = saveSettings;
        _openSettings = openSettings;
        _seenBefore = Changelog.ParseSeen(settings.LastVersionSeen);
        _releases = Changelog.All(current ?? Changelog.Running());

        _board = NewsBoard.Build(_releases, _seenBefore, null, false, _state.IsRead);
        foreach (var row in _board.Versions)
            Versions.Add(new NewsVersionRowViewModel(row));
    }

    /// <summary>The history, newest first. Built once: unfolding a row must survive everything else.</summary>
    public ObservableCollection<NewsVersionRowViewModel> Versions { get; } = new();

    /// <summary>Installs the version on offer (the same command as the banner's button).</summary>
    public ICommand UpdateNowCommand { get; }

    /// <summary>Whether this run is the first of a version the user had not seen.</summary>
    public bool HasFreshVersions => Versions.Any(v => v.IsNew);

    // ---- the update on offer ----

    public bool HasOffer => _board.Offer is not null;

    public string OfferText => _board.Offer is { } o
        ? Localizer.Format(o.IsBeta ? "Msg_UpdateBetaAvailableFmt" : "Msg_UpdateAvailableFmt", o.Version)
        : string.Empty;

    /// <summary>The offer still carries its mark (see the remarks of the class).</summary>
    public bool OfferMarked => _board.Offer is { } o && (o.Unread || _visitMarks.Contains(o.Key));

    /// <summary>The update check found a version (or no longer does).</summary>
    public void SetOffer(string? version, bool isBeta)
    {
        _offerVersion = version;
        _offerIsBeta = isBeta;
        Refresh();
    }

    // ---- the mark on the rail ----

    /// <summary>How many entries have not been read. Zero with the announcements off: versions never call out.</summary>
    public int UnreadCount => _settings.ShowAnnouncements ? _board.Unread : 0;

    public bool HasUnread => UnreadCount > 0;

    /// <summary>The style the rail uses; none while announcements are off.</summary>
    public NewsBadgeStyle BadgeStyle => _settings.ShowAnnouncements ? _settings.NewsBadge : NewsBadgeStyle.None;

    public string BadgeText => BadgeLabel(UnreadCount);

    /// <summary>What the number style says: the count, up to nine, and "9+" past it so the pill stays small.</summary>
    internal static string BadgeLabel(int count) => count > 9 ? "9+" : count.ToString();

    // ---- the notices ----

    /// <summary>Whether the notices are on: with them off the section is only the history.</summary>
    public bool NoticesEnabled => _settings.ShowAnnouncements;

    public bool NoticesDisabled => !_settings.ShowAnnouncements;

    /// <summary>The notices of the signed file that apply to this machine: what is going on first, then what ended.</summary>
    public ObservableCollection<NewsNoticeViewModel> Notices { get; } = new();

    public bool HasNotices => Notices.Count > 0;

    /// <summary>The empty state: notices on, a look at the file that worked, and nothing for this machine in it.</summary>
    public bool NoNotices => NoticesEnabled && Notices.Count == 0 && !HasFeedProblem;

    /// <summary>
    /// The last look at the file failed: no network, a server error, or a file that could not be trusted.
    /// Said on screen, because "no notices" and "could not look" are not the same thing and the second
    /// one is the one where an incident could be going on unseen.
    /// </summary>
    public bool HasFeedProblem => NoticesEnabled && (_feedStatus == NewsFeedStatus.Failed || IsFeedExpired);

    /// <summary>
    /// What the servers have, or the copy held, has run out (<c>expires</c> of the file): nobody renewed
    /// it, or an old one is being served. A different thing from failing to look, so it has its own words:
    /// the notices shown may be out of date for that reason alone.
    /// </summary>
    private bool IsFeedExpired => _feedStatus == NewsFeedStatus.Expired || _copyExpired;

    /// <summary>"Could not check for notices. Last copy: 3 h ago." or, for an expired file, "The list is out of date."</summary>
    public string FeedProblemText => IsFeedExpired
        ? (_feedCheckedUtc is { } seen
            ? Localizer.Format("News_FeedExpiredFmt", RelativeTime.Ago(seen, _clock().UtcDateTime))
            : Localizer.Get("News_FeedExpiredNoCopy"))
        : _feedCheckedUtc is { } at
            ? Localizer.Format("News_FeedFailedFmt", RelativeTime.Ago(at, _clock().UtcDateTime))
            : Localizer.Get("News_FeedFailedNoCopy");

    /// <summary>
    /// The notices of the last good copy of the file, and who they are for. Called when the copy is
    /// loaded and again whenever a look at the file ends.
    /// </summary>
    /// <param name="items">The notices; empty for none.</param>
    /// <param name="context">What decides which of them are for this machine.</param>
    public void SetNotices(IReadOnlyList<NewsItem> items, NewsContext context)
    {
        _items = items;
        _context = context;
        Refresh();
    }

    /// <summary>How the last look at the file ended, and when the copy held was last confirmed current.</summary>
    /// <param name="status">How the last look ended.</param>
    /// <param name="checkedUtc">When the copy held was last confirmed current.</param>
    /// <param name="copyExpired">Whether the copy held has run out now, whatever the last look said.</param>
    public void SetFeedState(NewsFeedStatus? status, DateTime? checkedUtc, bool copyExpired = false)
    {
        _feedStatus = status;
        _feedCheckedUtc = checkedUtc;
        _copyExpired = copyExpired;
        NotifyAll();
    }

    // ---- the strip ----

    /// <summary>Whether there is an urgent notice going on that the user has not closed. Whether and where the strip shows is the user's setting.</summary>
    public bool HasBand => NoticesEnabled && _board.Band is not null;

    /// <summary>The key of the notice the strip is for (<see cref="NewsNoticeRow.Key"/>); null when there is no strip.</summary>
    public string? BandKey => _board.Band?.Key;

    public string BandTitle => _board.Band?.Text.Title ?? string.Empty;

    public string BandBody => _board.Band?.Text.Body ?? string.Empty;

    /// <summary>A security notice, which the strip draws in red.</summary>
    public bool BandIsSecurity => _board.Band?.Item.Kind == NewsKind.Security;

    /// <summary>An incident, which the strip draws in amber.</summary>
    public bool BandIsIncident => _board.Band?.Item.Kind == NewsKind.Incident;

    /// <summary>The user closed the strip: it does not come back for this notice, which stays listed.</summary>
    [RelayCommand]
    private void CloseBand()
    {
        if (_board.Band is not { } band) return;
        _state.Close(band.Item.Id);
        Refresh();
    }

    /// <summary>The notices owed a notification now (see <see cref="NewsBoardModel.Toasts"/>).</summary>
    public IReadOnlyList<NewsNoticeRow> PendingToasts => NoticesEnabled ? _board.Toasts : [];

    /// <summary>
    /// Remembers that a notice was announced (or deliberately not), so it never is twice. False when it
    /// already was: whoever asked is the second to ask, and has nothing to announce.
    /// </summary>
    /// <remarks>
    /// The answer is what keeps two passes over the same <see cref="PendingToasts"/> from announcing a
    /// notice twice: marking it does not rebuild the board, so the list a second caller reads is the
    /// one the first one already handled.
    /// </remarks>
    public bool MarkToasted(string key) => _state.MarkToasted(key);

    // ---- the card about the section's new place ----

    public bool ShowMoveNotice => !_settings.NewsMoveNoticeSeen;

    [RelayCommand]
    private void DismissMoveNotice()
    {
        if (_settings.NewsMoveNoticeSeen) return;
        _settings.NewsMoveNoticeSeen = true;
        _saveSettings();
        OnPropertyChanged(nameof(ShowMoveNotice));
    }

    /// <summary>The card's "Go to Settings": answers it and goes.</summary>
    [RelayCommand]
    private void OpenNoticeSettings()
    {
        DismissMoveNotice();
        _openSettings();
    }

    /// <summary>The same way in, for where nothing is to be answered.</summary>
    [RelayCommand]
    private void OpenSettings() => _openSettings();

    /// <summary>Leaves the card as answered without the user having to: a new install has nothing that moved.</summary>
    internal void AnswerMoveNotice() => DismissMoveNotice();

    // ---- opening, leaving, marking ----

    /// <summary>"Mark all as read": the entries lose their mark without leaving the section.</summary>
    public bool HasMarks => OfferMarked || Notices.Any(n => n.Marked);

    [RelayCommand]
    private void MarkAllRead()
    {
        _visitMarks.Clear();
        Refresh();
    }

    /// <summary>Whether the section is on screen, so what arrives meanwhile is read as it comes.</summary>
    private bool _open;

    /// <summary>The section was opened: everything unread is read, and keeps its mark for this visit.</summary>
    public void Opened()
    {
        _open = true;
        Refresh();
    }

    /// <summary>The section was left: what was marked for the visit stops being.</summary>
    public void Closed()
    {
        _open = false;
        _visitMarks.Clear();
        Refresh();
    }

    /// <summary>A setting that changes what is shown moved: the board is worked out again.</summary>
    public void OnSettingsChanged() => Refresh();

    /// <summary>The board for what is known now. Notices only count with announcements on, and only once there are some.</summary>
    private NewsBoardModel BuildBoard()
    {
        var notices = _settings.ShowAnnouncements && _items is not null && _context is not null
            ? new NewsNoticeInput(_items, _context, _clock(), _settings.NewsIncidents, _settings.NewsTips,
                _state.IsClosed, _state.WasToasted)
            : null;
        return NewsBoard.Build(_releases, _seenBefore, _offerVersion, _offerIsBeta, _state.IsRead, notices);
    }

    private void Refresh()
    {
        _board = BuildBoard();

        // Nobody has anything to catch up on while looking at the list: what is unread is read at
        // once, and stays marked until the visit ends.
        if (_open && _board.UnreadKeys.Count > 0)
        {
            foreach (var key in _board.UnreadKeys) _visitMarks.Add(key);
            _state.MarkRead(_board.UnreadKeys);
            _board = BuildBoard();
        }

        Notices.Clear();
        foreach (var row in _board.Notices)
            Notices.Add(new NewsNoticeViewModel(row, row.Unread || _visitMarks.Contains(row.Key), OpenLink));

        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(HasOffer));
        OnPropertyChanged(nameof(OfferText));
        OnPropertyChanged(nameof(OfferMarked));
        OnPropertyChanged(nameof(HasMarks));
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(BadgeStyle));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(NoticesEnabled));
        OnPropertyChanged(nameof(NoticesDisabled));
        OnPropertyChanged(nameof(HasNotices));
        OnPropertyChanged(nameof(NoNotices));
        OnPropertyChanged(nameof(HasFeedProblem));
        OnPropertyChanged(nameof(FeedProblemText));
        OnPropertyChanged(nameof(HasBand));
        OnPropertyChanged(nameof(BandTitle));
        OnPropertyChanged(nameof(BandBody));
        OnPropertyChanged(nameof(BandIsSecurity));
        OnPropertyChanged(nameof(BandIsIncident));
        OnPropertyChanged(nameof(PendingToasts));
    }
}
