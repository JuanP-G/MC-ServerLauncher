using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The Mods (or Plugins) tab of one server: what is installed, and the store it is installed from.
/// </summary>
/// <remarks>
/// <para>
/// Which of the two it is comes from the server's family, not from its type — Paper and Purpur
/// browse plugins into <c>plugins/</c>, the loaders browse mods into <c>mods/</c> — and the same
/// answer picks the folder, the search filter and the wording.
/// </para>
/// <para>
/// Three things run off one scan of the installed jars, which is why they share a
/// <see cref="FileHashCache"/>: which project each file is, which of them have a newer version, and
/// which library mods are required but absent. Installing anything resolves its required
/// dependencies in the same click (<see cref="ModDependencyService"/>), because a Fabric server
/// refusing to start over a <c>fabric-api</c> nobody was told about is the failure this tab exists
/// to prevent.
/// </para>
/// <para>
/// Search results and the details page are the store's; nothing here decides whether a version is
/// compatible or how a file is verified, which stays in <see cref="ModrinthService"/>.
/// </para>
/// </remarks>
public partial class ServerModsViewModel : ObservableObject
{
    private readonly ServerConfig _config;
    private readonly ModrinthService _modrinthService = new();
    private readonly ModDependencyService _dependencies;

    /// <summary>Identifying a jar means hashing it, and both scans want the same answers.</summary>
    private readonly FileHashCache _hashes = new();

    /// <summary>
    /// Which Modrinth project each jar's SHA-1 belongs to, remembered for the session.
    /// </summary>
    /// <remarks>
    /// The update check already asks this and throws the answer away. Keeping it means that
    /// exporting after checking for updates — which is the order anybody does it in — costs the
    /// store lookup nothing at all.
    /// </remarks>
    private readonly Dictionary<string, string> _projectIdByHash = new(StringComparer.OrdinalIgnoreCase);
    
    // --- Local Mods State ---
    
    public ObservableCollection<ModItem> InstalledMods { get; } = new();

    // --- Marketplace State ---
    
    [ObservableProperty]
    private string _searchQuery = string.Empty;
    
    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchStatus = string.Empty;

    // --- Installed mods update-check state ---

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    // --- Missing library mods ---
    //
    // Found by the same scan that looks for updates, because it already knows what every installed
    // jar is. Kept as a list rather than a count so the offer to install them needs no second walk.

    private IReadOnlyList<ModDependencyService.Needed> _missingDependencies = Array.Empty<ModDependencyService.Needed>();

    [ObservableProperty]
    private string? _missingDependencyText;

    [ObservableProperty]
    private bool _isInstallingDependencies;

    public bool HasMissingDependencies => _missingDependencies.Count > 0;

    /// <summary>Sort options shown in the UI. Index 0 = relevance, 1 = downloads.</summary>
    public IReadOnlyList<string> SortOptions { get; } = new[]
    {
        Localizer.Get("Sort_Relevance"),
        Localizer.Get("Sort_Downloads"),
    };

    [ObservableProperty]
    private int _selectedSortIndex;

    [ObservableProperty]
    private bool _canLoadMore;

    private int _offset;
    private int _totalHits;
    private bool _hasLoadedOnce;
    private const int PageSize = 20;

    /// <summary>
    /// The results, flat. Replaced in one go rather than emptied and refilled: a list that sits
    /// empty across a network round trip makes the whole panel blink out and back.
    /// </summary>
    public BulkObservableCollection<ModrinthProjectViewModel> SearchResults { get; } = new();

    /// <summary>Cancels the search in flight when a newer one starts.</summary>
    private CancellationTokenSource? _reload;

    // --- Browsing by category ---

    /// <summary>
    /// The category chips, built from the tag catalogue and filtered to what makes sense for this
    /// kind of server. Tags that map to a Modrinth facet filter exactly; the rest fall back to a
    /// search term.
    /// </summary>
    /// <remarks>
    /// Rebuilt, not fixed at construction: converting a server between the two families changes
    /// which catalogue it browses, and a Paper server offering "Adventure &amp; RPG" because it used
    /// to be Fabric would search for something Modrinth has no plugins under.
    /// </remarks>
    public IReadOnlyList<StoreTagViewModel> BrowseTags { get; private set; }

    /// <summary>
    /// Categories Modrinth can filter on exactly. These stack: each adds its own facet group, and
    /// Modrinth ANDs the groups.
    /// </summary>
    public IReadOnlyList<StoreTagViewModel> FacetTags { get; private set; }

    /// <summary>
    /// Categories Modrinth has no facet for, which can only contribute words to the query. Picking
    /// a second one would blur the search instead of narrowing it, so they behave as a radio group.
    /// </summary>
    public IReadOnlyList<StoreTagViewModel> QueryTags { get; private set; }

    /// <summary>The family the chips above were built for, so a conversion knows to rebuild them.</summary>
    private bool _tagsAreForPlugins;

    /// <summary>
    /// The categories currently filtering the browse, in the order the user picked them. Shown as
    /// removable chips, so a filter can never be selected but scrolled out of sight.
    /// </summary>
    public ObservableCollection<StoreTagViewModel> SelectedTags { get; } = new();

    public bool HasSelectedTags => SelectedTags.Count > 0;

    /// <summary>
    /// Everything narrowing the results right now, as one row of chips: the server's own loader and
    /// Minecraft version (always on, not removable) followed by the categories the user picked.
    /// Putting them together answers "why am I seeing these results?" in one glance.
    /// </summary>
    public ObservableCollection<ActiveFilterViewModel> ActiveFilters { get; } = new();

    private void RebuildActiveFilters()
    {
        ActiveFilters.Clear();
        ActiveFilters.Add(new ActiveFilterViewModel(FilterTypeText, FilterTypeBrush, FilterTip));
        ActiveFilters.Add(new ActiveFilterViewModel(FilterVersionText, VersionChipBrush, FilterTip));
        foreach (var tag in SelectedTags)
            ActiveFilters.Add(new ActiveFilterViewModel(tag));
    }

    private static readonly IBrush VersionChipBrush = new ImmutableSolidColorBrush(Color.Parse("#40FFFFFF"));

    /// <summary>
    /// Adds or removes a category from the filter.
    /// <para>
    /// Tags backed by a Modrinth facet stack: each one adds its own facet group, and groups are
    /// ANDed, so picking two means "both", the same as Modrinth's own site. Tags with no facet
    /// only have search words to offer, and concatenating several of those blurs the query instead
    /// of narrowing it — so those behave like a radio button among themselves.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ToggleTag(StoreTagViewModel? tag)
    {
        if (tag is null) return;

        if (SelectedTags.Contains(tag))
        {
            SelectedTags.Remove(tag);
        }
        else
        {
            if (tag.Definition.Facets.Count == 0)
                foreach (var other in SelectedTags.Where(t => t.Definition.Facets.Count == 0).ToList())
                    SelectedTags.Remove(other);
            SelectedTags.Add(tag);
        }

        OnSelectionChanged();
    }

    /// <summary>Removes one category from the filter (the ✕ on its chip).</summary>
    [RelayCommand]
    private void RemoveTag(StoreTagViewModel? tag)
    {
        if (tag is null || !SelectedTags.Remove(tag)) return;
        OnSelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTags))]
    private void ClearFilters()
    {
        if (SelectedTags.Count == 0) return;
        SelectedTags.Clear();
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        foreach (var chip in BrowseTags) chip.IsActive = SelectedTags.Contains(chip);
        OnPropertyChanged(nameof(HasSelectedTags));
        ClearFiltersCommand.NotifyCanExecuteChanged();
        RebuildActiveFilters();
        _ = LoadPageAsync(append: false, CancellationToken.None);
    }

    /// <summary>
    /// Whether the installed panel is showing. Collapsing it is the cheapest way to give the
    /// results room: it is 300px that mostly holds a short list, and on a 1150px window it is the
    /// difference between one column of results and two.
    /// </summary>
    [ObservableProperty]
    private bool _isInstalledPanelOpen = true;

    /// <summary>Whether the filters popup is open (its toggle button binds both ways).</summary>
    [ObservableProperty]
    private bool _isFiltersOpen;

    /// <summary>Whether the "how to play" popup is open.</summary>
    [ObservableProperty]
    private bool _isHowToPlayOpen;

    // --- Details page ---

    /// <summary>
    /// The open details page, or null while the browser list is showing. The list is deliberately
    /// left untouched underneath, so coming back is instant and keeps the scroll position.
    /// </summary>
    [ObservableProperty]
    private ModDetailsViewModel? _details;

    public bool IsBrowsing => Details is null;

    /// <summary>Where the user came from, so a dependency four levels deep can walk back out.</summary>
    private readonly Stack<StoreItem> _history = new();

    /// <summary>Opens the details page for a store item, remembering the current one.</summary>
    public void ShowDetails(StoreItem item)
    {
        if (Details is { } current)
            _history.Push(current.CurrentItem);

        OpenDetails(item);
    }

    /// <summary>Goes back to the previous details page, or to the list when there isn't one.</summary>
    public void GoBack()
    {
        if (_history.Count > 0)
        {
            OpenDetails(_history.Pop());
            return;
        }

        var closing = Details;
        Details = null;
        closing?.Dispose();
    }

    /// <summary>
    /// Drops the open details page, cancelling whatever it is still fetching. Called when the app
    /// closes so pending requests don't outlive the window they were going to fill.
    /// </summary>
    public void Shutdown()
    {
        CloseDetails();

        try { _reload?.Cancel(); } catch { /* already disposed */ }
        _reload?.Dispose();
        _reload = null;

        _noticeTimer?.Stop();
        _noticeTimer = null;
    }

    /// <summary>Closes the details page and forgets the trail back, dropping what it was fetching.</summary>
    private void CloseDetails()
    {
        var open = Details;
        Details = null;
        open?.Dispose();
        _history.Clear();
    }

    private void OpenDetails(StoreItem item)
    {
        var previous = Details;
        var page = new ModDetailsViewModel(this, _modrinthService, item);
        Details = page;
        // Disposed after the swap so its cancellation can't race the new page's requests.
        previous?.Dispose();
        _ = page.LoadAsync();
    }

    partial void OnDetailsChanged(ModDetailsViewModel? value) => OnPropertyChanged(nameof(IsBrowsing));

    // --- Plugins vs mods (Paper uses plugins in plugins/; the loaders use mods in mods/) ---

    public bool IsPluginBased => ServerTypeCatalog.IsPluginBased(_config.Type);

    /// <summary>This server's loader, used by the details page to resolve compatible versions.</summary>
    internal ServerType ServerType => _config.Type;

    /// <summary>This server's Minecraft version.</summary>
    internal string GameVersion => _config.GameVersion;

    /// <summary>What Modrinth calls this kind of content, for the tag catalogue's chip filter.</summary>
    private string ProjectTypeName => IsPluginBased ? "plugin" : "mod";

    /// <summary>Folder where content is installed: "plugins" for Paper, "mods" otherwise.</summary>
    private string ContentFolder => ServerTypeCatalog.ContentFolder(_config.Type);

    // Labels shown in the view, adapted to mods vs plugins.
    public string ContentTabTitle => Localizer.Get(IsPluginBased ? "Plugins" : "Mods");
    public string BrowseTitle => Localizer.Get(IsPluginBased ? "Browse_Plugins" : "Browse_Mods");
    public string InstalledTitle => Localizer.Get(IsPluginBased ? "Installed_Plugins" : "Installed_Mods");
    public string SearchPlaceholder => Localizer.Get(IsPluginBased ? "SearchPlugins_Placeholder" : "SearchMods_Placeholder");
    public string NoInstalledText => Localizer.Get(IsPluginBased ? "No_Installed_Plugins" : "No_Installed_Mods");

    // Active filter (results are always limited to this server's type + version).
    public string FilterTypeText => _config.Type.ToString();
    public string FilterVersionText => _config.GameVersion;
    public string FilterTip => Localizer.Get("Filter_Tip");
    public IBrush FilterTypeBrush => ServerTypeBrushes.For(_config.Type);

    // --- "How to play" instructions (depend on the server type) ---

    public string HowToPlayTitle => Localizer.Get("HowToPlay_Title");

    /// <summary>Instructions to play: install the loader client (mods) or just join (plugins).</summary>
    public string HowToPlaySteps => _config.Type switch
    {
        ServerType.Fabric => string.Format(Localizer.Get("HowToPlay_FabricFmt"), _config.GameVersion),
        ServerType.Forge => string.Format(Localizer.Get("HowToPlay_ForgeFmt"), _config.GameVersion),
        ServerType.NeoForge => string.Format(Localizer.Get("HowToPlay_NeoForgeFmt"), _config.GameVersion),
        ServerType.Paper or ServerType.Purpur =>
            string.Format(Localizer.Get("HowToPlay_PaperFmt"), _config.GameVersion),
        _ => string.Empty
    };

    public ServerModsViewModel(ServerConfig config)
    {
        _config = config;
        // Shares the HTTP client and the store cache with everything else the panel asks for.
        _dependencies = new ModDependencyService(_modrinthService);
        BuildTags();
        RebuildActiveFilters();
        RefreshInstalledMods();
    }

    /// <summary>Builds the category chips for the family this server currently belongs to.</summary>
    [MemberNotNull(nameof(BrowseTags), nameof(FacetTags), nameof(QueryTags))]
    private void BuildTags()
    {
        _tagsAreForPlugins = IsPluginBased;
        BrowseTags = StoreTagService.Shared.BrowseTags(ProjectTypeName)
            .Select(t => new StoreTagViewModel(t))
            .ToList();
        // The two halves behave differently when several are picked, so the panel shows them apart.
        FacetTags = BrowseTags.Where(t => t.Definition.Facets.Count > 0).ToList();
        QueryTags = BrowseTags.Where(t => t.Definition.Facets.Count == 0).ToList();
    }

    /// <summary>
    /// Re-reads everything in this panel that comes from the server's type and Minecraft version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel reads both straight off the <see cref="ServerConfig"/> it was handed, and that
    /// object is mutated in place when a server is converted or moved to another version. None of
    /// the properties derived from it announce anything on their own, so without this call the tab
    /// kept the old family's name, the old version's filter chip and the old folder's jars until
    /// the app was restarted — while every search it ran used the new values. The two disagreeing
    /// is what made an install fail later: the results on screen had been chosen for a version the
    /// server no longer runs.
    /// </para>
    /// <para>
    /// Everything here is idempotent, so callers that are not sure whether anything moved can just
    /// call it.
    /// </para>
    /// </remarks>
    public void RefreshFromConfig() =>
        ApplyConfigChange(ServerConfigEffects.Everything.ModsProperties,
                          ServerConfigEffects.Everything.Effects);

    /// <summary>
    /// Applies one row of <see cref="ServerConfigEffects"/> to this panel: announces the properties
    /// it names, then does the work a notification cannot express.
    /// </summary>
    /// <remarks>
    /// The single entry point, so that the targeted path — one field of the config changed — and
    /// the wholesale one cannot drift: <see cref="RefreshFromConfig"/> is this, called with the
    /// union of every row.
    /// </remarks>
    internal void ApplyConfigChange(IReadOnlyList<string> properties, ConfigEffect effects)
    {
        // First, because it is the page that would otherwise install a file chosen for a server
        // this no longer is: it resolved its versions against the old type and version.
        if (effects.HasFlag(ConfigEffect.CloseDetails)) CloseDetails();

        // Guarded twice: the flag says a conversion could have changed the family, and the check
        // says whether it did. Rebuilding for a Paper-to-Purpur move would drop the user's chosen
        // categories for nothing.
        if (effects.HasFlag(ConfigEffect.RebuildTags) && _tagsAreForPlugins != IsPluginBased)
        {
            SelectedTags.Clear();
            BuildTags();
            OnPropertyChanged(nameof(BrowseTags));
            OnPropertyChanged(nameof(FacetTags));
            OnPropertyChanged(nameof(QueryTags));
            OnPropertyChanged(nameof(HasSelectedTags));
            ClearFiltersCommand.NotifyCanExecuteChanged();
        }

        foreach (var name in properties)
            OnPropertyChanged(name);

        // The chips spell out the type and the version, so they follow both.
        RebuildActiveFilters();

        // The content folder is named after the family, and converting between families archives
        // the old one, so the installed list is about a directory that has moved.
        if (effects.HasFlag(ConfigEffect.RescanContent)) RefreshInstalledMods();

        // Results already on screen were filtered by the old type and version. Leaving them would
        // offer mods that do not fit, under chips that now say something else.
        if (effects.HasFlag(ConfigEffect.ReSearchStore) && _hasLoadedOnce)
            _ = LoadPageAsync(append: false, CancellationToken.None);
    }

    /// <summary>
    /// Re-reads the content folder, for jars that arrived without the user putting them there.
    /// </summary>
    /// <remarks>
    /// The panel takes its snapshot when it is built, which is the same moment the server is
    /// created — before crossplay has downloaded Geyser and Floodgate into it. Without this the two
    /// jars are on disk and loaded by the server while the Mods tab still shows nothing, and the
    /// only way to see them is the refresh button, which nobody thinks to press for mods they did
    /// not install.
    /// </remarks>
    public void ReloadInstalled() => RefreshInstalledMods();

    [RelayCommand]
    private void RefreshInstalledMods()
    {
        InstalledMods.Clear();
        // The rebuilt items carry no update flag, so drop any stale "N updates available" text.
        UpdateStatus = string.Empty;
        // Same scan, same staleness: the missing-library offer names jars found last time round,
        // and after a refresh those may be installed, deleted, or for the family this no longer is.
        ClearMissingDependencies();
        var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);
        if (Directory.Exists(modsFolder))
        {
            // Include disabled mods (.jar.disabled) so they can be re-enabled from the app.
            var files = Directory.EnumerateFiles(modsFolder)
                .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var isEnabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var display = isEnabled ? name : name[..^".disabled".Length];
                InstalledMods.Add(new ModItem(file, display, isEnabled));
            }
        }
    }

    /// <summary>
    /// Identifies each installed jar by its SHA-1 through Modrinth and flags the ones with a newer
    /// compatible version. Jars Modrinth doesn't know (CurseForge, hand-built) are left untouched.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdates(CancellationToken ct)
    {
        if (InstalledMods.Count == 0) return;
        if (_config.Type == ServerType.Vanilla || string.IsNullOrEmpty(_config.GameVersion))
        {
            UpdateStatus = Localizer.Get("Msg_ModBrowserNeedsLoader");
            return;
        }

        IsCheckingUpdates = true;
        UpdateStatus = Localizer.Get("Msg_CheckingUpdates");
        try
        {
            // Map each jar's SHA-1 to its ModItem (disabled ones included) and clear any prior flag.
            var byHash = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in InstalledMods)
            {
                mod.Update = null;
                try
                {
                    byHash[await _hashes.Sha1Async(mod.FilePath, ct)] = mod;
                }
                catch { /* unreadable/locked file: skip it */ }
            }

            var latest = await _modrinthService.GetLatestVersionsByHashAsync(byHash.Keys, _config.Type, _config.GameVersion, ct);

            var updates = 0;
            foreach (var (installedHash, version) in latest)
            {
                if (!byHash.TryGetValue(installedHash, out var mod)) continue;
                var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
                if (file is null) continue;

                // If the latest compatible version's file differs from the installed one, it's an update.
                if (!string.Equals(file.Hashes?.Sha1, installedHash, StringComparison.OrdinalIgnoreCase))
                {
                    mod.Update = new ModUpdateInfo(version.VersionNumber, file.Url,
                        Path.GetFileName(file.Filename), file.Hashes?.Sha512, file.Hashes?.Sha1);
                    updates++;
                }
            }

            UpdateStatus = updates > 0
                ? string.Format(Localizer.Get("Msg_UpdatesFoundFmt"), updates)
                : Localizer.Get("Msg_NoUpdates");

            await ScanForMissingDependenciesAsync(byHash.Keys, ct);
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(Localizer.Get("Msg_ModErrorFmt"), ex.Message);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private bool CanCheckUpdates => !IsCheckingUpdates;

    partial void OnIsCheckingUpdatesChanged(bool value) => CheckUpdatesCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Looks for library mods that the installed ones need and that nobody installed.
    /// </summary>
    /// <remarks>
    /// Runs off the hashes the update check already computed, so it costs one extra request. It
    /// exists for the servers that were built before the installer started pulling dependencies in:
    /// those fail at start-up with the loader listing what is missing, and there is nothing in the
    /// app that would otherwise say so.
    /// </remarks>
    private async Task ScanForMissingDependenciesAsync(IEnumerable<string> sha1Hashes, CancellationToken ct)
    {
        ClearMissingDependencies();

        // What each installed jar *is* — the update endpoint answers what could replace it, and the
        // dependencies that matter are the ones declared by the file actually on disk.
        var installed = await _modrinthService.GetVersionsByHashAsync(sha1Hashes, ct);
        if (installed.Count == 0) return;

        // Kept for the export, which needs exactly this and would otherwise ask for it again.
        foreach (var (hash, version) in installed) _projectIdByHash[hash] = version.ProjectId;

        var roots = installed.Values.ToList();
        var installedIds = roots.Select(r => r.ProjectId).ToList();

        var plan = await _dependencies.ResolveMissingAsync(
            roots, _config.Type, _config.GameVersion, installedIds, ct);

        // And what the jars ask for themselves, which is how a mod whose Modrinth page lists no
        // dependencies at all still turns out to need fabric-api.
        var known = installedIds.Concat(plan.Install.Select(i => i.ProjectId)).ToList();
        var fromJars = await _dependencies.ResolveByModIdAsync(
            InstalledMods.SelectMany(m => ModDependencyService.DeclaredModIds(m.FilePath)),
            _config.Type, _config.GameVersion, known, ct);

        ShowMissingDependencies(new ModDependencyService.Plan(
            plan.Install.Concat(fromJars.Install).ToList(), plan.Unresolved));
    }

    /// <summary>Puts a resolved plan on screen. Separated from the scan so it can be shown a plan.</summary>
    internal void ShowMissingDependencies(ModDependencyService.Plan plan)
    {
        if (plan.Install.Count == 0)
        {
            ClearMissingDependencies();
            return;
        }

        _missingDependencies = plan.Install;
        MissingDependencyText = string.Format(Localizer.Get("Msg_MissingDepsFoundFmt"),
            plan.Install.Count, string.Join(", ", plan.Install.Select(d => d.Label)));
        OnPropertyChanged(nameof(HasMissingDependencies));
    }

    private void ClearMissingDependencies()
    {
        _missingDependencies = Array.Empty<ModDependencyService.Needed>();
        MissingDependencyText = null;
        OnPropertyChanged(nameof(HasMissingDependencies));
    }

    /// <summary>
    /// Fetches the mods or plugins named by <paramref name="ids"/>, for the check before a start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ids come from the jars, and a mod id is not a store id: <c>cristallib</c> is published on
    /// Modrinth as <c>cristel-lib</c>, and there is no rule that maps one to the other. So anything
    /// the store does not recognise is skipped rather than reported as an error — the caller checks
    /// again afterwards and says what is still missing, which is the honest answer.
    /// </para>
    /// <para>
    /// Progress goes to the caller's log rather than to this tab's status line: the user pressed
    /// Start and is looking at the server's console, not at the Mods tab.
    /// </para>
    /// </remarks>
    public async Task InstallMissingAsync(IEnumerable<string> ids, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var wanted = ids.ToList();
        if (wanted.Count == 0) return;

        log?.Report(Localizer.Get("Msg_ResolvingDependencies"));

        var plan = await _dependencies.ResolveByModIdAsync(
            wanted, _config.Type, _config.GameVersion,
            await InstalledProjectIdsAsync(ct), ct);

        if (plan.Install.Count == 0)
        {
            log?.Report(Localizer.Get("Msg_DepsNoneResolved"));
            return;
        }

        var folder = Path.Combine(_config.FolderPath, ContentFolder);
        Directory.CreateDirectory(folder);

        foreach (var needed in plan.Install)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report(string.Format(Localizer.Get("Msg_DownloadingMod"), needed.Label));
            await DownloadDependencyAsync(needed, folder, ct);
        }

        log?.Report(string.Format(Localizer.Get("Msg_DepsInstalledFmt"), plan.Install.Count));

        // The list backs an ItemsControl, so it has to be rebuilt on the UI thread — and this is
        // called from the start flow, which may be anywhere by the time the downloads finish.
        if (Dispatcher.UIThread.CheckAccess()) RefreshInstalledMods();
        else Dispatcher.UIThread.Post(RefreshInstalledMods);
    }

    /// <summary>Downloads the library mods the scan found missing.</summary>
    [RelayCommand]
    private async Task InstallMissingDependencies(CancellationToken ct)
    {
        if (_missingDependencies.Count == 0 || IsInstallingDependencies) return;

        IsInstallingDependencies = true;
        try
        {
            var folder = Path.Combine(_config.FolderPath, ContentFolder);
            Directory.CreateDirectory(folder);

            foreach (var needed in _missingDependencies)
            {
                UpdateStatus = string.Format(Localizer.Get("Msg_DownloadingMod"), needed.Label);
                await DownloadDependencyAsync(needed, folder, ct);
            }

            UpdateStatus = string.Format(Localizer.Get("Msg_DepsInstalledFmt"), _missingDependencies.Count);
            ClearMissingDependencies();
            RefreshInstalledMods();
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(Localizer.Get("Msg_ModErrorFmt"), ex.Message);
        }
        finally
        {
            IsInstallingDependencies = false;
        }
    }

    /// <summary>
    /// The project id of every installed jar Modrinth recognises.
    /// </summary>
    /// <remarks>
    /// This is what makes "already installed" a real answer rather than a filename comparison.
    /// Modrinth's dependencies carry no version range, so a project that is present satisfies them;
    /// installing a second copy under a different file name is what produces the loader's
    /// "duplicate mod" failure, which is worse than the one being fixed.
    /// </remarks>
    private async Task<List<string>> InstalledProjectIdsAsync(CancellationToken ct)
    {
        var folder = Path.Combine(_config.FolderPath, ContentFolder);
        if (!Directory.Exists(folder)) return new List<string>();

        var hashes = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder)
                     .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            try { hashes.Add(await _hashes.Sha1Async(file, ct)); }
            catch { /* unreadable or locked: it simply does not count as installed */ }
        }

        var known = await _modrinthService.GetVersionsByHashAsync(hashes, ct);
        return known.Values.Select(v => v.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Installs the required dependencies of <paramref name="version"/> that are not there yet.
    /// </summary>
    /// <returns>
    /// How many were installed, and any that could not be resolved at all. Zero and empty is the
    /// normal answer for a plugin or for a library mod that depends on nothing.
    /// </returns>
    private async Task<(int Installed, IReadOnlyList<string> Unresolved)> InstallDependenciesAsync(
        VersionResult version, string jarPath, string folder, CancellationToken ct = default)
    {
        var declared = ModDependencyService.DeclaredModIds(jarPath);
        var hasModrinthDeps = version.Dependencies.Any(d =>
            string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase));

        // A plugin, or a mod that genuinely needs nothing: no request, and no hash of the folder.
        if (!hasModrinthDeps && declared.Count == 0) return (0, Array.Empty<string>());

        Notify(Localizer.Get("Msg_ResolvingDependencies"), failed: false, transient: true);

        var installedIds = await InstalledProjectIdsAsync(ct);
        var plan = await _dependencies.ResolveMissingAsync(
            new[] { version }, _config.Type, _config.GameVersion, installedIds, ct);

        var installed = 0;
        foreach (var needed in plan.Install)
        {
            Notify(string.Format(Localizer.Get("Msg_DownloadingMod"), needed.Label),
                failed: false, transient: true);
            await DownloadDependencyAsync(needed, folder, ct);
            installed++;
        }

        // Now what the jars themselves ask for, which Modrinth does not always know: Explorify lists
        // no dependencies there and its own metadata requires fabric-api. Rounds rather than one
        // pass, because a library pulled in this way can declare its own.
        var known = new List<string>(installedIds);
        known.AddRange(plan.Install.Select(i => i.ProjectId));
        var pending = new List<string>(declared);

        for (var round = 0; round < 3 && pending.Count > 0; round++)
        {
            var extra = await _dependencies.ResolveByModIdAsync(
                pending, _config.Type, _config.GameVersion, known, ct);
            if (extra.Install.Count == 0) break;

            pending = new List<string>();
            foreach (var needed in extra.Install)
            {
                Notify(string.Format(Localizer.Get("Msg_DownloadingMod"), needed.Label),
                    failed: false, transient: true);
                var written = AtomicDownload.PathIn(folder, needed.File.Filename);
                await DownloadDependencyAsync(needed, folder, ct);

                known.Add(needed.ProjectId);
                installed++;
                pending.AddRange(ModDependencyService.DeclaredModIds(written));
            }
        }

        return (installed, plan.Unresolved);
    }

    /// <summary>One dependency onto disk, verified, with the same path safety as any other install.</summary>
    private Task DownloadDependencyAsync(ModDependencyService.Needed needed, string folder, CancellationToken ct)
    {
        var path = AtomicDownload.PathIn(folder, needed.File.Filename);
        return _modrinthService.DownloadModAsync(needed.File.Url, path,
            needed.File.Hashes?.Sha512, needed.File.Hashes?.Sha1, ct: ct);
    }

    /// <summary>Downloads the newer version flagged by <see cref="CheckUpdates"/> and replaces the old jar.</summary>
    [RelayCommand]
    private async Task UpdateMod(ModItem? mod)
    {
        if (mod?.Update is null || mod.IsUpdating) return;

        mod.IsUpdating = true;
        try
        {
            var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);
            Directory.CreateDirectory(modsFolder);
            var wasDisabled = !mod.IsEnabled;

            // Download+verify the new jar under its own (enabled) name first, so a failure never
            // destroys the currently installed one.
            var enabledPath = Path.Combine(modsFolder, mod.Update.FileName);
            await _modrinthService.DownloadModAsync(mod.Update.Url, enabledPath, mod.Update.Sha512, mod.Update.Sha1);

            // Remove the previous jar when the new version has a different file name.
            var sameFile = string.Equals(
                Path.GetFullPath(mod.FilePath), Path.GetFullPath(enabledPath), StringComparison.OrdinalIgnoreCase);
            if (!sameFile)
            {
                try
                {
                    File.Delete(mod.FilePath);
                }
                catch
                {
                    // Old jar in use (server running): keeping both would load two versions of the
                    // mod. Roll the new one back and tell the user to stop the server first.
                    try { File.Delete(enabledPath); } catch { /* best-effort */ }
                    UpdateStatus = Localizer.Get("Msg_UpdateNeedsStop");
                    return;
                }
            }

            // Preserve the enabled/disabled state the mod had.
            if (wasDisabled)
            {
                var disabledPath = enabledPath + ".disabled";
                try { if (File.Exists(disabledPath)) File.Delete(disabledPath); } catch { /* best-effort */ }
                File.Move(enabledPath, disabledPath);
            }

            UpdateStatus = Localizer.Get("Msg_ModUpdated");
            RefreshInstalledMods();
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(Localizer.Get("Msg_UpdateErrorFmt"), ex.Message);
        }
        finally
        {
            mod.IsUpdating = false;
        }
    }

    [RelayCommand]
    private void ToggleMod(ModItem? mod)
    {
        if (mod is null) return;
        var newExt = mod.IsEnabled ? ".disabled" : "";
        var newFile = mod.FilePath.Replace(".jar.disabled", ".jar") + newExt;

        try { File.Move(mod.FilePath, newFile); }
        catch { /* ignore; the refresh below resyncs the toggle with the real file state */ }
        RefreshInstalledMods();
    }

    [RelayCommand]
    private async Task DeleteMod(ModItem? mod)
    {
        if (mod is null) return;
        try
        {
            File.Delete(mod.FilePath);
        }
        catch (Exception ex)
        {
            // Typically the file is locked because the server is running.
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_ModDeleteError"), ex.Message), ContentTabTitle);
        }
        RefreshInstalledMods();
    }

    /// <summary>Asks where to put the pack, and builds it there.</summary>
    /// <remarks>
    /// Everything except the file picker lives in <see cref="BuildModpackAsync"/>, which takes a
    /// path and therefore runs without a window. What is left here is the dozen lines that cannot
    /// be tested at all, rather than the whole export being untestable because it begins with a
    /// dialog.
    /// </remarks>
    [RelayCommand]
    private async Task ExportModpack()
    {
        var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);
        if (!Directory.Exists(modsFolder)) return;

        var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = Localizer.Get("Export_Modpack"),
            DefaultExtension = "zip",
            SuggestedFileName = $"{_config.Name}-{ContentFolder}.zip",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("ZIP Archive") { Patterns = new[] { "*.zip" } } }
        });

        if (file == null) return;

        try
        {
            var result = await BuildModpackAsync(file.Path.LocalPath, includeEverything: false, CancellationToken.None);
            _lastExportPath = file.Path.LocalPath;
            _lastExportLeftSomethingOut = result.Excluded.Count > 0;
            ExportNotice = NoticeFor(result);
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_ExportError"), ex.Message), Localizer.Get("Export_Modpack"));
        }
    }

    /// <summary>What one export put into the pack, and what it left out.</summary>
    /// <param name="Included">File names of the jars written, in the order they were written.</param>
    /// <param name="Excluded">File names left out as server-only.</param>
    /// <param name="AskedTheStore">False when the pack was built on the jars alone.</param>
    internal sealed record ModpackResult(
        IReadOnlyList<string> Included, IReadOnlyList<string> Excluded, bool AskedTheStore);

    /// <summary>
    /// Builds the pack at <paramref name="destination"/>, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// Takes a path rather than opening a picker, which is the whole point: this is the part worth
    /// testing, and until now none of it was reachable without a main window.
    /// </remarks>
    /// <param name="destination">Where to write the zip.</param>
    /// <param name="includeEverything">Skip the reasoning and pack the folder as it stands.</param>
    /// <param name="ct">Cancels the build.</param>
    internal async Task<ModpackResult> BuildModpackAsync(
        string destination, bool includeEverything, CancellationToken ct)
    {
        // Plugins never go anywhere near a player's machine, so there is nothing to work out: the
        // pack is the folder. The button is hidden on those servers, and this is the same answer
        // arrived at without relying on that.
        var everything = includeEverything || IsPluginBased;
        var sides = everything ? EmptySides : await StoreSidesAsync(JarsInContentFolder(), ct);

        return await BuildModpackWithSidesAsync(destination, sides, includeEverything, ct);
    }

    /// <summary>Every enabled jar in this server's content folder, in a stable order.</summary>
    private List<string> JarsInContentFolder()
    {
        var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);

        return Directory.Exists(modsFolder)
            ? Directory.EnumerateFiles(modsFolder, "*.jar")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
    }

    /// <summary>
    /// The half that decides and writes, with whatever the store had to say already in hand.
    /// </summary>
    /// <remarks>
    /// Split from the fetching so the decision can be tested against real jars on disk and a real
    /// zip coming out, without a test ever reaching api.modrinth.com — which would make the result
    /// depend on a third party's uptime and on data anyone can edit.
    /// </remarks>
    /// <param name="destination">Where to write the zip.</param>
    /// <param name="storeSides">Per file name, what the store said. Empty means it was not asked.</param>
    /// <param name="includeEverything">Skip the reasoning and pack the folder as it stands.</param>
    /// <param name="ct">Cancels the build.</param>
    internal Task<ModpackResult> BuildModpackWithSidesAsync(
        string destination,
        IReadOnlyDictionary<string, (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)> storeSides,
        bool includeEverything,
        CancellationToken ct)
    {
        var contentFolder = ContentFolder;
        var jars = JarsInContentFolder();
        var everything = includeEverything || IsPluginBased;
        var sides = storeSides;

        return Task.Run(() =>
        {
            var candidates = jars
                .Select(path =>
                {
                    var manifest = ContentManifest.Read(path);
                    return sides.TryGetValue(manifest.FileName, out var side)
                        ? new ExportSelection.Candidate(manifest, side.Client, side.Server)
                        : new ExportSelection.Candidate(manifest);
                })
                .ToList();

            var plan = ExportSelection.Decide(candidates, everything);
            var keep = plan.Included.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var instructions = string.Format(Localizer.Get("Export_InstructionsFmt"),
                _config.Name, _config.Type, _config.GameVersion, HowToPlaySteps, contentFolder);

            // The pack has to explain itself to whoever receives it, not only to whoever made it:
            // a player who counts the jars and finds three missing deserves to read why here.
            if (plan.LeftSomethingOut)
                instructions += "\n\n" + string.Format(Localizer.Get("Export_LeftOutFmt"),
                    plan.Excluded.Count, string.Join(", ", plan.Excluded));

            // The script names are not translated — see InstallScriptBuilder — so the
            // instructions, which are, have to be where the player reads what they are for.
            if (!IsPluginBased)
                instructions += "\n\n" + string.Format(Localizer.Get("Export_ScriptsFmt"),
                    InstallScriptBuilder.WindowsName, InstallScriptBuilder.UnixName);

            var texts = new List<ModpackWriter.TextFile>
            {
                // CRLF whatever built the pack: this one is opened in Notepad more often than
                // anywhere else, and every other editor reads CRLF without complaining.
                new(Localizer.Get("Export_InstructionsFile"), instructions, Newline: "\r\n")
            };

            // Plugins go on a server, by hand, by whoever runs it. Nothing to install on a
            // client, so no script — and the export button is not offered on those servers.
            if (!IsPluginBased)
                texts.AddRange(InstallScriptBuilder.Build(
                    _config.Name, _config.Type, _config.GameVersion, DateTime.Now));

            ModpackWriter.Write(
                destination, contentFolder,
                jars.Where(j => keep.Contains(Path.GetFileName(j))).ToList(),
                texts);

            // "Not degraded" rather than "a request went out": packing everything needs no store
            // answer, so it is not the reduced result the offline note is there to explain.
            return new ModpackResult(plan.Included, plan.Excluded, everything || sides.Count > 0);
        }, ct);
    }

    /// <summary>How long the store gets to answer before the pack is built without it.</summary>
    /// <remarks>
    /// A few seconds, because what the store adds is an improvement and not a requirement: without
    /// it the table falls back to what the jars themselves declare, which excludes strictly less.
    /// Nobody should watch a progress bar because Modrinth is having a slow afternoon.
    /// </remarks>
    private static readonly TimeSpan StoreLookupBudget = TimeSpan.FromSeconds(4);

    private static readonly IReadOnlyDictionary<string, (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)>
        EmptySides = new Dictionary<string, (ExportSelection.StoreSide, ExportSelection.StoreSide)>();

    /// <summary>
    /// What the store says about each jar's sides, by file name. Empty when it could not be asked.
    /// </summary>
    /// <remarks>
    /// Two hops, because a hash lookup alone does not carry the side fields: SHA-1 to project id
    /// (memoised, and usually already known from the update check), then the project itself, which
    /// the store service caches for six hours with a copy on disk.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)>>
        StoreSidesAsync(IReadOnlyList<string> jarPaths, CancellationToken ct)
    {
        var byName = new Dictionary<string, (ExportSelection.StoreSide, ExportSelection.StoreSide)>(
            StringComparer.OrdinalIgnoreCase);
        if (jarPaths.Count == 0) return byName;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(StoreLookupBudget);

        try
        {
            var hashByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in jarPaths)
            {
                try { hashByName[Path.GetFileName(path)] = await _hashes.Sha1Async(path, budget.Token); }
                catch { /* unreadable or locked: that jar simply gets no store answer */ }
            }

            var unknown = hashByName.Values.Where(h => !_projectIdByHash.ContainsKey(h)).ToList();
            if (unknown.Count > 0)
                foreach (var (hash, version) in await _modrinthService.GetVersionsByHashAsync(unknown, budget.Token))
                    _projectIdByHash[hash] = version.ProjectId;

            var ids = hashByName.Values
                .Select(h => _projectIdByHash.TryGetValue(h, out var id) ? id : null)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ids.Count == 0) return byName;

            var projects = await _modrinthService.GetProjectsAsync(ids, budget.Token);
            if (projects is null) return byName;

            // Grouped rather than ToDictionary: a duplicate id in the response would throw, and
            // taking the store down with an export is not a trade worth making.
            var byId = projects
                .Where(project => !string.IsNullOrEmpty(project.Id))
                .GroupBy(project => project.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var (name, hash) in hashByName)
                if (_projectIdByHash.TryGetValue(hash, out var id) && byId.TryGetValue(id, out var project))
                    byName[name] = (StoreSideOf(project.ClientSide), StoreSideOf(project.ServerSide));
        }
        catch
        {
            // Offline, out of time, or Modrinth having a bad day. The table falls back to the jars
            // alone, which excludes fewer of them, and the notice says the pack was built that way.
        }

        return byName;
    }

    /// <summary>Modrinth's word for how well a side is supported, as the table understands it.</summary>
    private static ExportSelection.StoreSide StoreSideOf(string? value) => value?.ToLowerInvariant() switch
    {
        "required" => ExportSelection.StoreSide.Required,
        "optional" => ExportSelection.StoreSide.Optional,
        "unsupported" => ExportSelection.StoreSide.Unsupported,
        _ => ExportSelection.StoreSide.Unknown
    };

    // --- The notice the export leaves behind ---

    /// <summary>What the last export did, shown under the installed list until dismissed.</summary>
    /// <remarks>
    /// Deliberately after the fact and not a dialog beforehand. A dialog charges a click to the
    /// normal case — the one where the detection is right — in order to serve the rare one, and
    /// per-mod checkboxes are precision nobody needs when including one jar too many costs a few
    /// megabytes. Naming the excluded files is what makes this actionable instead of unsettling.
    /// </remarks>
    [ObservableProperty]
    private string? _exportNotice;

    public bool HasExportNotice => !string.IsNullOrEmpty(ExportNotice);

    partial void OnExportNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasExportNotice));
        IncludeExcludedAnywayCommand.NotifyCanExecuteChanged();
    }

    private string? _lastExportPath;
    private bool _lastExportLeftSomethingOut;

    /// <summary>True while there is something to put back, which is what the button hangs off.</summary>
    public bool CanIncludeExcludedAnyway => _lastExportLeftSomethingOut && _lastExportPath is not null;

    /// <summary>The sentence the notice shows for one finished export.</summary>
    internal static string NoticeFor(ModpackResult result)
    {
        var text = result.Excluded.Count == 0
            ? string.Format(Localizer.Get("Export_NoticeAllFmt"), result.Included.Count)
            : string.Format(Localizer.Get("Export_NoticeFmt"),
                result.Included.Count, result.Excluded.Count, string.Join(", ", result.Excluded));

        // Said out loud rather than hidden: the pack does come out different without a connection,
        // and a difference the user can see is one they can decide about.
        if (!result.AskedTheStore) text += " " + Localizer.Get("Export_NoticeOffline");
        return text;
    }

    [RelayCommand]
    private void DismissExportNotice() => ExportNotice = null;

    /// <summary>Rebuilds the same pack with nothing left out, over the same file.</summary>
    /// <remarks>
    /// One click, no picker and no dialog. If a detection is wrong the cost of undoing it has to be
    /// smaller than the cost of checking it was right.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanIncludeExcludedAnyway))]
    private async Task IncludeExcludedAnyway()
    {
        if (_lastExportPath is not { } path) return;

        try
        {
            var result = await BuildModpackAsync(path, includeEverything: true, CancellationToken.None);
            _lastExportLeftSomethingOut = false;
            ExportNotice = NoticeFor(result);
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_ExportError"), ex.Message), Localizer.Get("Export_Modpack"));
        }
    }

    /// <summary>Loads the first page of mods the first time the Mods tab is shown.</summary>
    public void EnsureLoaded()
    {
        if (_hasLoadedOnce) return;
        _ = LoadPageAsync(append: false, CancellationToken.None);
    }

    [RelayCommand]
    private Task Search(CancellationToken ct) => LoadPageAsync(append: false, ct);

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMore(CancellationToken ct) => LoadPageAsync(append: true, ct);

    partial void OnSelectedSortIndexChanged(int value)
    {
        if (_hasLoadedOnce) _ = LoadPageAsync(append: false, CancellationToken.None);
    }

    partial void OnCanLoadMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Loads a page of results for the current loader+version+query+sort. When <paramref name="append"/>
    /// is false the list is reset (new search/sort); when true the next page is appended.
    /// With an empty query Modrinth returns the top mods, so the browser works without searching.
    /// </summary>
    private async Task LoadPageAsync(bool append, CancellationToken ct)
    {
        if (_config.Type == ServerType.Vanilla || string.IsNullOrEmpty(_config.GameVersion))
        {
            SearchStatus = Localizer.Get("Msg_ModBrowserNeedsLoader");
            SetResults(Array.Empty<ModrinthProjectViewModel>(), append: false);
            CanLoadMore = false;
            return;
        }

        // Every new search supersedes the one before it. Without this, two quick clicks on two
        // chips race: both responses land on the same list, the paging offset is advanced twice
        // and "load more" silently skips a page.
        CancellationTokenSource? mine = null;
        if (!append)
        {
            _reload?.Cancel();
            _reload?.Dispose();
            mine = _reload = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = mine.Token;
            _offset = 0;
        }

        IsSearching = true;
        _hasLoadedOnce = true;
        var index = SelectedSortIndex == 1 ? "downloads" : "relevance";

        // Facet-backed categories stack into separate groups, which Modrinth ANDs. The ones with
        // no facet contribute their words to the query instead (only ever one of those at a time).
        var facets = SelectedTags.SelectMany(t => t.Definition.Facets).ToList();
        var query = SearchQuery ?? string.Empty;
        foreach (var loose in SelectedTags.Where(t => t.Definition.Facets.Count == 0))
            if (loose.Definition.Query is { Length: > 0 } words)
                query = string.IsNullOrWhiteSpace(query) ? words : query + " " + words;

        try
        {
            var response = await _modrinthService.SearchModsAsync(
                query, _config.Type, _config.GameVersion, index, _offset, PageSize,
                facets.Count > 0 ? facets : null, ct);

            // A newer search started while this one was in flight: its results are the ones that
            // match what the interface is showing, so drop these.
            if (ct.IsCancellationRequested || (mine is not null && !ReferenceEquals(_reload, mine)))
                return;

            if (response != null)
            {
                var page = response.Hits.Select(hit => new ModrinthProjectViewModel(hit, this));
                SetResults(page, append);

                _totalHits = response.TotalHits;
                _offset += response.Hits.Count;
                CanLoadMore = response.Hits.Count > 0 && SearchResults.Count < _totalHits;

                SearchStatus = SearchResults.Count > 0
                    ? string.Format(Localizer.Get("Msg_ModsFoundFmt"), _totalHits)
                    : Localizer.Get("Msg_NoModsFound");
            }
            else if (!append)
            {
                // Keep whatever was on screen: an empty panel says less than the old results plus
                // an explanation of why they didn't refresh.
                SearchStatus = Localizer.Get("Msg_NoModsFound");
                CanLoadMore = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search; the one that replaced it owns the UI now.
        }
        catch (Exception ex)
        {
            SearchStatus = string.Format(Localizer.Get("Msg_ModErrorFmt"), ex.Message);
        }
        finally
        {
            // Only the search still in charge may clear the spinner.
            if (append || mine is null || ReferenceEquals(_reload, mine)) IsSearching = false;
        }
    }

    /// <summary>
    /// Puts a page of results on screen: appended for "load more", swapped in one operation for
    /// everything else.
    /// <para>
    /// The swap matters. Emptying the list and refilling it after the response arrives leaves the
    /// panel blank for the length of a network round trip, which reads as the view jumping. One
    /// replacement means the old results stay put until the new ones are ready.
    /// </para>
    /// <para>
    /// The two branches are also what drives the scroll position: a replacement raises a single
    /// Reset, which <c>ResetScrollBehavior</c> takes as "these are different results" and sends the
    /// list back to the top; appending raises Add, which leaves the user where they were reading.
    /// </para>
    /// </summary>
    private void SetResults(IEnumerable<ModrinthProjectViewModel> page, bool append)
    {
        if (append) foreach (var item in page) SearchResults.Add(item);
        else SearchResults.ReplaceAll(page);
    }

    // --- Install feedback ---
    //
    // Installing needs a surface of its own. SearchStatus is only drawn when the result list is
    // EMPTY, which is precisely when you cannot be installing anything, and the details page never
    // showed it at all — so a checksum that didn't match, a dropped connection or a jar locked by a
    // running server all failed in complete silence.

    /// <summary>What the last install said, shown as a banner over the store and the details page.</summary>
    [ObservableProperty]
    private string? _installNotice;

    /// <summary>True when the notice reports a failure, which draws it in red rather than green.</summary>
    [ObservableProperty]
    private bool _installFailed;

    public bool HasInstallNotice => !string.IsNullOrWhiteSpace(InstallNotice);

    partial void OnInstallNoticeChanged(string? value) => OnPropertyChanged(nameof(HasInstallNotice));

    /// <summary>Clears a success notice on its own; failures stay until read.</summary>
    private DispatcherTimer? _noticeTimer;

    [RelayCommand]
    private void DismissInstallNotice()
    {
        _noticeTimer?.Stop();
        InstallNotice = null;
    }

    /// <summary>
    /// Shows <paramref name="message"/> in the install banner. A success disappears by itself after
    /// a few seconds; a failure does not — an error nobody read is the bug this exists to fix.
    /// </summary>
    private void Notify(string message, bool failed, bool transient = false)
    {
        _noticeTimer?.Stop();
        InstallFailed = failed;
        InstallNotice = message;

        if (failed || transient) return;

        _noticeTimer ??= CreateNoticeTimer();
        _noticeTimer.Start();
    }

    private DispatcherTimer CreateNoticeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!InstallFailed) InstallNotice = null;
        };
        return timer;
    }

    /// <summary>
    /// Installs a project. <paramref name="chosen"/> installs that exact version (the details page
    /// offers the last few); without it the newest compatible one is resolved, as before.
    /// This is the only install path in the app — the details page routes through it too.
    /// </summary>
    public async Task InstallModAsync(string projectId, VersionResult? chosen = null)
    {
        IsSearching = true;
        // transient: progress, not an outcome — it must not time out while the download runs.
        Notify(Localizer.Get("Msg_ResolvingVersion"), failed: false, transient: true);

        try
        {
            var version = chosen
                ?? await _modrinthService.GetLatestProjectVersionAsync(projectId, _config.Type, _config.GameVersion);
            if (version == null || version.Files.Count == 0)
            {
                Notify(Localizer.Get("Msg_NoCompatibleVersion"), failed: true);
                return;
            }

            var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.First();

            var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);
            Directory.CreateDirectory(modsFolder);
            // The file name comes from the Modrinth API, so the join goes through the one
            // place that keeps it inside the folder.
            var destPath = AtomicDownload.PathIn(modsFolder, file.Filename);

            // If the same mod is already present but disabled, remove the disabled copy first so we
            // don't end up with two files (a new enabled one + an old disabled one) that then clash
            // when toggling. The download below overwrites any enabled copy of the same name.
            var disabledPath = destPath + ".disabled";
            try { if (File.Exists(disabledPath)) File.Delete(disabledPath); }
            catch { /* best-effort */ }

            Notify(string.Format(Localizer.Get("Msg_DownloadingMod"), file.Filename),
                failed: false, transient: true);

            // Mods are third-party jars chosen by the user: verify against Modrinth's own checksum.
            await _modrinthService.DownloadModAsync(file.Url, destPath, file.Hashes?.Sha512, file.Hashes?.Sha1);

            // The libraries it needs, in the same click. Almost every Fabric mod declares at least
            // fabric-api, and a mod installed without them is a server that refuses to start with a
            // list of names the user never chose and has no way to act on from here.
            var (extra, unresolved) = await InstallDependenciesAsync(version, destPath, modsFolder);
            RefreshInstalledMods();

            // A dependency with no build for this Minecraft version is not an install failure — the
            // mod is on disk — but it is the reason the server will not start, so it stays on screen
            // instead of fading away with the success message.
            if (unresolved.Count > 0)
                Notify(string.Format(Localizer.Get("Msg_DepUnresolvedFmt"), string.Join(", ", unresolved)),
                    failed: true);
            else
                Notify(extra == 0
                        ? Localizer.Get("Msg_ModInstalled")
                        : string.Format(Localizer.Get("Msg_ModInstalledWithDepsFmt"), extra),
                    failed: false);
        }
        catch (Exception ex)
        {
            Notify(string.Format(Localizer.Get("Msg_InstallErrorFmt"), ex.Message), failed: true);
        }
        finally
        {
            IsSearching = false;
        }
    }
}

/// <summary>
/// One chip in the "what is filtering right now" row. Some of them are the server's own loader and
/// version, which are not the user's to remove; the rest are categories, which are.
/// </summary>
public class ActiveFilterViewModel
{
    public string Label { get; }

    public IBrush Brush { get; }

    public string Tip { get; }

    /// <summary>The tag behind the chip, or null for the fixed loader/version chips.</summary>
    public StoreTagViewModel? Tag { get; }

    public bool CanRemove => Tag is not null;

    public ActiveFilterViewModel(string label, IBrush brush, string tip)
    {
        Label = label;
        Brush = brush;
        Tip = tip;
    }

    public ActiveFilterViewModel(StoreTagViewModel tag)
    {
        Tag = tag;
        Label = tag.Label;
        Brush = tag.Brush;
        Tip = tag.Label;
    }
}

/// <summary>One card in the browser: a store item, its tags, and the two buttons it offers.</summary>
public partial class ModrinthProjectViewModel : ObservableObject
{
    private readonly ServerModsViewModel _parent;
    private readonly StoreItem _item;

    public string Id => _item.Id;
    public string Title => _item.Title;
    public string Author => _item.Author;
    public long Downloads => _item.Downloads;
    public string DownloadsText { get; }
    public string? IconUrl => _item.IconUrl;

    /// <summary>What the project does, in the user's language.</summary>
    public string Description { get; }

    /// <summary>
    /// The author's own line, shown small underneath. It is the only English left on a card, and
    /// only when the catalogue has nothing for this project — dropping it entirely would trade a
    /// wrong language for less information.
    /// </summary>
    public string Tagline { get; }

    public bool HasTagline => !string.IsNullOrEmpty(Tagline);

    /// <summary>The few tags that fit on a card, most important first.</summary>
    public IReadOnlyList<StoreTagViewModel> Tags { get; }

    public bool HasTags => Tags.Count > 0;

    /// <summary>True when players have to install it too — the one caveat worth showing up front.</summary>
    public bool NeedsClient => _item.NeedsClient;

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Mod icon, loaded asynchronously through the shared image cache (best-effort).</summary>
    [ObservableProperty]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    public ModrinthProjectViewModel(ProjectResult project, ServerModsViewModel parent)
        : this(StoreItem.From(project), parent)
    {
    }

    public ModrinthProjectViewModel(StoreItem item, ServerModsViewModel parent)
    {
        _parent = parent;
        _item = item;
        DownloadsText = ModDetailsViewModel.FormatCount(item.Downloads);

        var blurb = StoreSummaryService.Shared.Blurb(item);
        Description = blurb.Summary;
        Tagline = blurb.Tagline;

        // Three fits one line on a card. The install side isn't among them: the card shows that as
        // its own badge next to the download count.
        Tags = StoreTagService.Shared.Classify(item, max: 3, onlyKind: StoreTagService.TopicKind)
            .Select(t => new StoreTagViewModel(t))
            .ToList();

        _ = LoadIconAsync(item.IconUrl);
    }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    private async Task LoadIconAsync(string? url)
    {
        // Size- and content-type-guarded, cached in memory and on disk (see ImageCache): a huge or
        // mislabeled icon_url can't balloon memory, and scrolling back up costs no request.
        var bitmap = await ImageCache.GetAsync(url);
        if (bitmap is null) return;
        if (Dispatcher.UIThread.CheckAccess()) Icon = bitmap;
        else Dispatcher.UIThread.Post(() => Icon = bitmap);
    }

    /// <summary>Opens the full details page for this project.</summary>
    [RelayCommand]
    private void Open() => _parent.ShowDetails(_item);

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        await _parent.InstallModAsync(Id);
        IsInstalling = false;
    }
}
