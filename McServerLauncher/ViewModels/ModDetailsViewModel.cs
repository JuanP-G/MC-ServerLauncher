using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The details page of one store item: everything Modrinth knows about a project, plus the two
/// things Modrinth doesn't say — what it does in plain language, and whether it fits *this* server.
/// <para>
/// It paints in two passes. What the search result already carried (title, author, icon, summary)
/// is shown at once, so opening a mod is instant; the long description, gallery, versions,
/// dependencies and related items arrive as their requests come back. Nothing here decides whether
/// a version is compatible or how a file is installed: both come from the existing services.
/// </para>
/// </summary>
public partial class ModDetailsViewModel : ObservableObject, IDisposable
{
    private readonly ServerModsViewModel _parent;
    private readonly ModrinthService _modrinth;

    /// <summary>Cancels every pending request when the page is left, so nothing lands on a dead page.</summary>
    private readonly CancellationTokenSource _cts = new();

    private ProjectDetail? _project;
    private StoreItem _item;

    public string ProjectId { get; }

    // --- Header ---

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _authorText = string.Empty;

    /// <summary>The plain-language summary: the curated catalogue, or the built one.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>The author's own one-line description, shown under the summary when they differ.</summary>
    [ObservableProperty]
    private string _tagline = string.Empty;

    [ObservableProperty]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _errorText;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    // --- Numbers ---

    [ObservableProperty]
    private string _downloadsText = string.Empty;

    [ObservableProperty]
    private string _followersText = string.Empty;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    public bool HasUpdated => !string.IsNullOrEmpty(UpdatedText);

    [ObservableProperty]
    private string _publishedText = string.Empty;

    public bool HasPublished => !string.IsNullOrEmpty(PublishedText);

    [ObservableProperty]
    private string _licenseText = string.Empty;

    public bool HasLicense => !string.IsNullOrEmpty(LicenseText);

    private string? _licenseUrl;

    // --- Long description ---

    [ObservableProperty]
    private string? _body;

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    /// <summary>True when the description was cut, so the page can point at Modrinth for the rest.</summary>
    [ObservableProperty]
    private bool _bodyTruncated;

    // --- Compatibility with this server ---

    [ObservableProperty]
    private bool _isCompatible;

    [ObservableProperty]
    private bool _compatibilityKnown;

    [ObservableProperty]
    private string _compatibilityText = string.Empty;

    [ObservableProperty]
    private string _compatibilityDetail = string.Empty;

    /// <summary>The warning that matters most on a server: everyone has to install this one.</summary>
    [ObservableProperty]
    private bool _needsClient;

    [ObservableProperty]
    private string _sideText = string.Empty;

    public IBrush CompatibilityBrush => IsCompatible ? CompatibleBrush : IncompatibleBrush;

    private static readonly IBrush CompatibleBrush = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush IncompatibleBrush = new ImmutableSolidColorBrush(Color.Parse("#E3A82B"));

    // --- Collections ---

    public ObservableCollection<StoreTagViewModel> Tags { get; } = new();

    public ObservableCollection<GalleryImageViewModel> Gallery { get; } = new();

    public ObservableCollection<StoreLinkViewModel> Dependencies { get; } = new();

    public ObservableCollection<StoreLinkViewModel> Related { get; } = new();

    public ObservableCollection<ModVersionViewModel> Versions { get; } = new();

    public ObservableCollection<ExternalLinkViewModel> Links { get; } = new();

    [ObservableProperty]
    private GalleryImageViewModel? _selectedImage;

    // --- Section labels (also used to hide empty sections) ---

    public string RelatedTitle => Localizer.Get(_parent.IsPluginBased ? "Store_RelatedPlugins" : "Store_RelatedMods");

    public bool HasTags => Tags.Count > 0;
    public bool HasGallery => Gallery.Count > 0;
    public bool HasDependencies => Dependencies.Count > 0;
    public bool HasRelated => Related.Count > 0;
    public bool HasVersions => Versions.Count > 0;
    public bool HasLinks => Links.Count > 0;

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Modrinth's own page, offered as the escape hatch for anything not shown here.</summary>
    public string ModrinthUrl { get; private set; }

    public ModDetailsViewModel(ServerModsViewModel parent, ModrinthService modrinth, StoreItem seed)
    {
        _parent = parent;
        _modrinth = modrinth;
        _item = seed;

        ProjectId = seed.Id;
        _title = seed.Title;
        ModrinthUrl = BuildModrinthUrl(seed);

        ApplyItem(seed);
    }

    /// <summary>What this page is showing, so the browser can push it onto its history.</summary>
    public StoreItem CurrentItem => _item;

    private static string BuildModrinthUrl(StoreItem item)
    {
        var kind = string.Equals(item.ProjectType, "plugin", StringComparison.OrdinalIgnoreCase) ? "plugin" : "mod";
        var key = string.IsNullOrEmpty(item.Slug) ? item.Id : item.Slug;
        return $"https://modrinth.com/{kind}/{Uri.EscapeDataString(key)}";
    }

    /// <summary>Fills everything that can be derived from a store item, from either source.</summary>
    private void ApplyItem(StoreItem item)
    {
        _item = item;
        Title = item.Title;

        // The project endpoint returns a team id instead of a name, so an empty author there must
        // not erase the one the search result already gave us.
        if (!string.IsNullOrWhiteSpace(item.Author))
            AuthorText = string.Format(Localizer.Get("Store_ByFmt"), item.Author);

        // Six is as many chips as the header row holds without wrapping into a wall; the install
        // side is left out because the compatibility section below states it in a full sentence.
        var tags = StoreTagService.Shared.Classify(item, max: 6, onlyKind: StoreTagService.TopicKind);
        var blurb = StoreSummaryService.Shared.Blurb(item, tags);
        Summary = blurb.Summary;
        Tagline = blurb.Tagline;

        Tags.Clear();
        foreach (var tag in tags) Tags.Add(new StoreTagViewModel(tag));
        OnPropertyChanged(nameof(HasTags));

        DownloadsText = FormatCount(item.Downloads);
        FollowersText = FormatCount(item.Followers);
        UpdatedText = FormatDate(item.Updated);
        OnPropertyChanged(nameof(HasUpdated));

        NeedsClient = item.NeedsClient;
        SideText = StoreSummaryService.SideSentence(item);
    }

    /// <summary>
    /// Loads the rest of the page. Every step is independent: a failure in one (say, the related
    /// strip) leaves the others intact, because a details page with less on it beats an error.
    /// </summary>
    public async Task LoadAsync()
    {
        var ct = _cts.Token;
        IsLoading = true;
        ErrorText = null;
        OnPropertyChanged(nameof(HasError));

        try
        {
            _ = LoadIconAsync(_item.IconUrl, ct);

            var projectTask = _modrinth.GetProjectAsync(ProjectId, ct);
            var versionsTask = _modrinth.GetProjectVersionsAsync(
                ProjectId, _parent.ServerType, _parent.GameVersion, ct);

            var project = await projectTask;
            if (ct.IsCancellationRequested) return;

            if (project is null)
            {
                ErrorText = Localizer.Get("Store_LoadError");
                OnPropertyChanged(nameof(HasError));
            }
            else
            {
                _project = project;
                ApplyProject(project);
            }

            var versions = await versionsTask;
            if (ct.IsCancellationRequested) return;
            ApplyVersions(versions);

            // These three are extras: they run after the page is already usable.
            if (project is not null)
            {
                if (string.IsNullOrEmpty(AuthorText)) await LoadAuthorAsync(project, ct);
                await LoadDependenciesAsync(versions, ct);
                await LoadRelatedAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left while loading; nothing to report.
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }

    private void ApplyProject(ProjectDetail project)
    {
        ApplyItem(StoreItem.From(project));
        ModrinthUrl = BuildModrinthUrl(_item);
        OnPropertyChanged(nameof(ModrinthUrl));

        // The page can be opened from something that didn't carry an icon (a dependency chip, or a
        // project id on its own), in which case this is the first time we know where the icon is.
        if (Icon is null) _ = LoadIconAsync(project.IconUrl, _cts.Token);

        Body = project.Body;
        BodyTruncated = project.Body is { Length: > MarkdownParser.MaxCharacters };
        OnPropertyChanged(nameof(HasBody));

        PublishedText = FormatDate(project.Published);
        OnPropertyChanged(nameof(HasPublished));

        // Modrinth leaves the licence name empty for non-SPDX licences, where the id is the name.
        LicenseText = !string.IsNullOrWhiteSpace(project.License?.Name)
            ? project.License!.Name!
            : project.License?.Id ?? string.Empty;
        _licenseUrl = project.License?.Url;
        OnPropertyChanged(nameof(HasLicense));

        BuildLinks(project);
        BuildGallery(project);
    }

    private void BuildLinks(ProjectDetail project)
    {
        Links.Clear();
        Links.Add(new ExternalLinkViewModel(Localizer.Get("Store_OpenOnModrinth"), ModrinthUrl, Symbol.Open));
        Add(project.SourceUrl, "Store_Source", Symbol.Code);
        Add(project.IssuesUrl, "Store_Issues", Symbol.Bug);
        Add(project.WikiUrl, "Store_Wiki", Symbol.BookOpen);
        Add(project.DiscordUrl, "Store_Discord", Symbol.Chat);
        if (HasLicense && BrowserLauncher.IsWebUrl(_licenseUrl))
            Links.Add(new ExternalLinkViewModel(LicenseText, _licenseUrl!, Symbol.Document));
        foreach (var donation in project.DonationUrls)
            Add(donation.Url, "Store_Donate", Symbol.Heart, donation.Platform);

        OnPropertyChanged(nameof(HasLinks));

        void Add(string? url, string key, Symbol icon, string? suffix = null)
        {
            if (!BrowserLauncher.IsWebUrl(url)) return;
            var label = Localizer.Get(key);
            if (!string.IsNullOrWhiteSpace(suffix)) label = $"{label} ({suffix})";
            Links.Add(new ExternalLinkViewModel(label, url!, icon));
        }
    }

    private void BuildGallery(ProjectDetail project)
    {
        Gallery.Clear();
        foreach (var image in project.Gallery.OrderByDescending(g => g.Featured).ThenBy(g => g.Ordering))
        {
            if (!BrowserLauncher.IsWebUrl(image.Url)) continue;
            var vm = new GalleryImageViewModel(image);
            Gallery.Add(vm);
            // Thumbnails are small and cached; loading them together keeps the strip from
            // filling in one image at a time as the user scrolls past it.
            _ = vm.LoadThumbnailAsync(_cts.Token);
        }
        OnPropertyChanged(nameof(HasGallery));

        if (Gallery.Count > 0) SelectedImage = Gallery[0];
    }

    partial void OnSelectedImageChanged(GalleryImageViewModel? value)
    {
        foreach (var image in Gallery) image.IsSelected = ReferenceEquals(image, value);
        if (value is not null) _ = value.LoadFullAsync(_cts.Token);
    }

    private void ApplyVersions(List<VersionResult>? versions)
    {
        Versions.Clear();

        // Compatibility is not decided here: the list is exactly what Modrinth returns for this
        // server's loader and Minecraft version — the same query the installer resolves against.
        CompatibilityKnown = versions is not null;
        IsCompatible = versions is { Count: > 0 };

        if (IsCompatible)
        {
            CompatibilityText = Localizer.Get("Store_Compatible");
            CompatibilityDetail = string.Format(Localizer.Get("Store_CompatibleDetailFmt"),
                _parent.ServerType, _parent.GameVersion);

            foreach (var version in versions!.Take(10))
                Versions.Add(new ModVersionViewModel(this, version));
        }
        else
        {
            CompatibilityText = Localizer.Get("Store_NotCompatible");
            CompatibilityDetail = string.Format(Localizer.Get("Store_NotCompatibleDetailFmt"),
                _parent.ServerType, _parent.GameVersion);
        }

        OnPropertyChanged(nameof(CompatibilityBrush));
        OnPropertyChanged(nameof(HasVersions));
        InstallCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadAuthorAsync(ProjectDetail project, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(project.Team)) return;
        var members = await _modrinth.GetTeamMembersAsync(project.Team, ct);
        if (ct.IsCancellationRequested || members is null || members.Count == 0) return;

        var names = members
            .Select(m => m.User?.Name is { Length: > 0 } name ? name : m.User?.Username)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(3)
            .ToList();

        if (names.Count > 0)
            AuthorText = string.Format(Localizer.Get("Store_ByFmt"), string.Join(", ", names));
    }

    /// <summary>
    /// Dependencies come from the newest compatible version rather than from the project as a
    /// whole: what an older version needed is not what will be installed today.
    /// </summary>
    private async Task LoadDependenciesAsync(List<VersionResult>? versions, CancellationToken ct)
    {
        Dependencies.Clear();
        OnPropertyChanged(nameof(HasDependencies));

        var newest = versions?.FirstOrDefault();
        if (newest is null) return;

        var wanted = newest.Dependencies
            .Where(d => d.DependencyType is "required" or "optional")
            .Where(d => !string.IsNullOrEmpty(d.ProjectId))
            .GroupBy(d => d.ProjectId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DependencyType!, StringComparer.Ordinal);

        if (wanted.Count == 0) return;

        var projects = await _modrinth.GetProjectsAsync(wanted.Keys, ct);
        if (ct.IsCancellationRequested || projects is null) return;

        foreach (var project in projects)
        {
            var kind = wanted.TryGetValue(project.Id, out var type) && type == "required"
                ? Localizer.Get("Store_DepRequired")
                : Localizer.Get("Store_DepOptional");
            // A dependency used to say only "required": now it also says what it is, which is the
            // difference between "install this too" and "install this too, it's the config library".
            Dependencies.Add(new StoreLinkViewModel(_parent, StoreItem.From(project), badge: kind));
        }
        OnPropertyChanged(nameof(HasDependencies));
    }

    /// <summary>
    /// Related items are found through the tags: the first tag that knows how to search Modrinth
    /// becomes the query. This is the seed of the discovery the tags exist for — no recommendation
    /// engine, just "more of the same kind, that also runs on this server".
    /// </summary>
    private async Task LoadRelatedAsync(CancellationToken ct)
    {
        Related.Clear();
        OnPropertyChanged(nameof(HasRelated));

        var tag = Tags.Select(t => t.Definition)
                      .FirstOrDefault(t => t.Facets.Count > 0 || !string.IsNullOrWhiteSpace(t.Query));
        if (tag is null) return;

        var response = await _modrinth.SearchModsAsync(
            tag.Facets.Count > 0 ? string.Empty : tag.Query!,
            _parent.ServerType, _parent.GameVersion, "downloads", 0, 12,
            tag.Facets.Count > 0 ? tag.Facets : null, ct);

        if (ct.IsCancellationRequested || response is null) return;

        foreach (var hit in response.Hits)
        {
            if (string.Equals(hit.ProjectId, ProjectId, StringComparison.Ordinal)) continue;
            Related.Add(new StoreLinkViewModel(_parent, StoreItem.From(hit), withTagline: true));
            if (Related.Count == 6) break;
        }
        OnPropertyChanged(nameof(HasRelated));
    }

    private async Task LoadIconAsync(string? url, CancellationToken ct)
    {
        var bitmap = await ImageCache.GetAsync(url, ImageCache.MaxIconBytes, ct);
        if (bitmap is null || ct.IsCancellationRequested) return;
        Icon = bitmap;
    }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsCompatibleChanged(bool value) => OnPropertyChanged(nameof(CompatibilityBrush));

    // --- Commands ---

    /// <summary>Installs the newest compatible version, through the existing installer.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        IsInstalling = true;
        try { await _parent.InstallModAsync(ProjectId); }
        finally { IsInstalling = false; }
    }

    private bool CanInstall => IsCompatible && !IsInstalling;

    partial void OnIsInstallingChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();

    /// <summary>Installs one specific version, again through the existing installer.</summary>
    internal async Task InstallVersionAsync(VersionResult version)
    {
        IsInstalling = true;
        try { await _parent.InstallModAsync(ProjectId, version); }
        finally { IsInstalling = false; }
    }

    /// <summary>Shows a gallery image in the large view (the thumbnails call this).</summary>
    [RelayCommand]
    private void SelectImage(GalleryImageViewModel? image)
    {
        if (image is not null) SelectedImage = image;
    }

    [RelayCommand]
    private void Back() => _parent.GoBack();

    [RelayCommand]
    private void OpenLink(string? url) => BrowserLauncher.Open(url);

    // --- Formatting ---

    /// <summary>Compact count, e.g. 1234 → "1.2K", 3500000 → "3.5M".</summary>
    internal static string FormatCount(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 1_000 => (value / 1_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "K",
        _ => value.ToString(CultureInfo.CurrentCulture)
    };

    internal static string FormatDate(DateTimeOffset? value) =>
        value is null ? string.Empty : value.Value.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _cts.Dispose();
    }
}

/// <summary>A tag as the interface shows it: translated name and its colour.</summary>
public partial class StoreTagViewModel : ObservableObject
{
    public StoreTagDefinition Definition { get; }

    public string Label { get; }

    public IBrush Brush { get; }

    /// <summary>
    /// Set on the chip currently filtering the browser. Unused on the read-only chips of a card or
    /// a details page, which are never selectable.
    /// </summary>
    [ObservableProperty]
    private bool _isActive;

    public StoreTagViewModel(StoreTagDefinition definition)
    {
        Definition = definition;
        Label = StoreTagService.LabelFor(definition);
        Brush = ParseBrush(definition.Color);
    }

    private static readonly IBrush Neutral = new ImmutableSolidColorBrush(Color.Parse("#6E7681"));

    /// <summary>Colours come from an editable catalogue, so an invalid one must not throw.</summary>
    private static IBrush ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Neutral;
        return Color.TryParse(hex, out var color) ? new ImmutableSolidColorBrush(color) : Neutral;
    }
}

/// <summary>One gallery image: a thumbnail for the strip and the full picture for the viewer.</summary>
public partial class GalleryImageViewModel : ObservableObject
{
    private readonly string _thumbnailUrl;
    private readonly string _fullUrl;

    public string? Caption { get; }

    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private Bitmap? _full;

    [ObservableProperty]
    private bool _isSelected;

    public GalleryImageViewModel(GalleryImage image)
    {
        _thumbnailUrl = image.Url;
        // Modrinth's "url" is a resized thumbnail; the raw one is what deserves the large view.
        _fullUrl = BrowserLauncher.IsWebUrl(image.RawUrl) ? image.RawUrl! : image.Url;
        Caption = !string.IsNullOrWhiteSpace(image.Title) ? image.Title : image.Description;
    }

    public async Task LoadThumbnailAsync(CancellationToken ct)
    {
        if (Thumbnail is not null) return;
        var bitmap = await ImageCache.GetAsync(_thumbnailUrl, ImageCache.MaxIconBytes, ct);
        if (bitmap is not null && !ct.IsCancellationRequested) Thumbnail = bitmap;
    }

    public async Task LoadFullAsync(CancellationToken ct)
    {
        if (Full is not null) return;
        var bitmap = await ImageCache.GetAsync(_fullUrl, ImageCache.MaxGalleryBytes, ct);
        if (bitmap is not null && !ct.IsCancellationRequested) Full = bitmap;
        // A full image that won't load (too big, odd format) still shows as its thumbnail.
        else if (!ct.IsCancellationRequested) await LoadThumbnailAsync(ct);
    }
}

/// <summary>A dependency or a related project: a card that opens its own details page.</summary>
public partial class StoreLinkViewModel : ObservableObject
{
    private readonly ServerModsViewModel _parent;
    private readonly StoreItem _item;

    /// <summary>The project's own name. Never translated: it is how the user will find it on
    /// Modrinth, and it is the name of the .jar that ends up in the server's folder.</summary>
    public string Title => _item.Title;

    /// <summary>Optional label above the summary, e.g. whether a dependency is required.</summary>
    public string Badge { get; }

    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>What the project does, in the user's language.</summary>
    public string Summary { get; }

    /// <summary>The author's own line, when it adds something. Empty otherwise.</summary>
    public string Tagline { get; }

    public bool HasTagline => !string.IsNullOrEmpty(Tagline);

    [ObservableProperty]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    public StoreLinkViewModel(ServerModsViewModel parent, StoreItem item,
        string badge = "", bool withTagline = false)
    {
        _parent = parent;
        _item = item;
        Badge = badge;

        // Same rule as everywhere else in the store: our catalogue first, then a sentence built
        // and translated from Modrinth's data — never the author's English as the main text.
        var blurb = StoreSummaryService.Shared.Blurb(item);
        Summary = blurb.Summary;
        Tagline = withTagline ? blurb.Tagline : string.Empty;

        _ = LoadIconAsync(item.IconUrl);
    }

    private async Task LoadIconAsync(string? url)
    {
        var bitmap = await ImageCache.GetAsync(url);
        if (bitmap is not null) Icon = bitmap;
    }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    [RelayCommand]
    private void Open() => _parent.ShowDetails(_item);
}

/// <summary>One published version, with the button that installs exactly that one.</summary>
public partial class ModVersionViewModel : ObservableObject
{
    private readonly ModDetailsViewModel _parent;
    private readonly VersionResult _version;

    public string Name { get; }

    public string VersionNumber => _version.VersionNumber;

    /// <summary>"release", "beta" or "alpha", shown as a coloured chip.</summary>
    public string TypeText { get; }

    public IBrush TypeBrush { get; }

    public string DateText { get; }

    public string DownloadsText { get; }

    public string SizeText { get; }

    [ObservableProperty]
    private bool _isInstalling;

    private static readonly IBrush ReleaseBrush = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush BetaBrush = new ImmutableSolidColorBrush(Color.Parse("#E3A82B"));
    private static readonly IBrush AlphaBrush = new ImmutableSolidColorBrush(Color.Parse("#E05561"));

    public ModVersionViewModel(ModDetailsViewModel parent, VersionResult version)
    {
        _parent = parent;
        _version = version;

        Name = string.IsNullOrWhiteSpace(version.Name) ? version.VersionNumber : version.Name!;
        TypeText = (version.VersionType ?? "release").ToLowerInvariant();
        TypeBrush = TypeText switch
        {
            "beta" => BetaBrush,
            "alpha" => AlphaBrush,
            _ => ReleaseBrush
        };
        DateText = ModDetailsViewModel.FormatDate(version.DatePublished);
        DownloadsText = ModDetailsViewModel.FormatCount(version.Downloads);

        var primary = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        SizeText = primary is { Size: > 0 }
            ? (primary.Size / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
            : string.Empty;
    }

    public bool HasSize => !string.IsNullOrEmpty(SizeText);

    [RelayCommand]
    private async Task Install()
    {
        IsInstalling = true;
        try { await _parent.InstallVersionAsync(_version); }
        finally { IsInstalling = false; }
    }
}

/// <summary>An external link shown in the details page (source, wiki, Discord…).</summary>
public class ExternalLinkViewModel
{
    public string Label { get; }

    public string Url { get; }

    /// <summary>Icon drawn next to the label, so the links are scannable at a glance.</summary>
    public Symbol Icon { get; }

    public ExternalLinkViewModel(string label, string url, Symbol icon)
    {
        Label = label;
        Url = url;
        Icon = icon;
    }
}
