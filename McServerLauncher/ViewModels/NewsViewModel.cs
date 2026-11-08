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

/// <summary>
/// Novedades: the update waiting to be installed, the notices (once they arrive) and the history of
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

    /// <summary>The keys that were unread when the section was opened, kept marked while it stays open.</summary>
    private readonly HashSet<string> _visitMarks = new(StringComparer.Ordinal);

    /// <param name="settings">The settings the app runs on; <see cref="AppSettings.LastVersionSeen"/> is
    /// read now, before the start-up flow updates it.</param>
    /// <param name="state">What has been read.</param>
    /// <param name="updateNow">The command that installs the offered version.</param>
    /// <param name="saveSettings">Writes the settings.</param>
    /// <param name="openSettings">Takes the user to the notices page of Settings.</param>
    /// <param name="current">The running version; null for this build. Tests pass their own.</param>
    public NewsViewModel(AppSettings settings, NewsState state, ICommand updateNow, Action saveSettings,
        Action openSettings, Version? current = null)
    {
        _settings = settings;
        _state = state;
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
    public bool HasMarks => OfferMarked;

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

    /// <summary>A setting that changes what is shown moved.</summary>
    public void OnSettingsChanged() => NotifyAll();

    private void Refresh()
    {
        _board = NewsBoard.Build(_releases, _seenBefore, _offerVersion, _offerIsBeta, _state.IsRead);

        // Nobody has anything to catch up on while looking at the list: what is unread is read at
        // once, and stays marked until the visit ends.
        if (_open && _board.UnreadKeys.Count > 0)
        {
            foreach (var key in _board.UnreadKeys) _visitMarks.Add(key);
            _state.MarkRead(_board.UnreadKeys);
            _board = NewsBoard.Build(_releases, _seenBefore, _offerVersion, _offerIsBeta, _state.IsRead);
        }
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
    }
}
