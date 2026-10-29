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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;
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

    /// <summary>Finds and downloads the libraries the content needs; see <see cref="ModDependencyInstaller"/>.</summary>
    private readonly ModDependencyInstaller _dependencyInstaller;

    /// <summary>Checks for newer versions and replaces jars with them.</summary>
    private readonly ModUpdater _updater;

    /// <summary>Installs one project from the store; see <see cref="ModInstaller"/>.</summary>
    private readonly ModInstaller _installer;

    /// <summary>Builds the zip a server's owner hands to their players.</summary>
    private readonly ModpackExporter _exporter;

    /// <summary>What the store says each jar is, shared by the scans, the installs and the export.</summary>
    private readonly InstalledContentIndex _index;

    /// <summary>Cancelled when the app closes: downloads and store questions stop with it.</summary>
    private CancellationTokenSource _lifetime = new();


    // --- Local Mods State ---

    public ObservableCollection<ModItem> InstalledMods { get; } = new();

    /// <summary>
    /// What the installed list shows: <see cref="InstalledMods"/> through <see cref="InstalledFilter"/>.
    /// </summary>
    /// <remarks>
    /// A copy rather than a filter on <see cref="InstalledMods"/> itself, because that one is also
    /// what counts pending updates and what "update all" walks. Filtering it in place would make a
    /// search box quietly hide updates from both.
    /// </remarks>
    public BulkObservableCollection<ModItem> VisibleInstalledMods { get; } = new();

    /// <summary>The words typed in the installed list's search box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledFiltered))]
    private string _installedFilter = string.Empty;

    public bool IsInstalledFiltered => !string.IsNullOrWhiteSpace(InstalledFilter);

    /// <summary>Something is installed, but the search leaves none of it.</summary>
    public bool HasNoMatches => InstalledMods.Count > 0 && VisibleInstalledMods.Count == 0;

    /// <summary>"12", or "3 of 12" while searching.</summary>
    public string InstalledCountText => IsInstalledFiltered
        ? string.Format(Localizer.Get("Mods_InstalledCountFmt"), VisibleInstalledMods.Count,
            InstalledMods.Count)
        : InstalledMods.Count.ToString(System.Globalization.CultureInfo.CurrentUICulture);

    partial void OnInstalledFilterChanged(string value) => RebuildVisibleInstalled();

    private void RebuildVisibleInstalled()
    {
        var words = InstalledModFilter.Words(InstalledFilter);
        VisibleInstalledMods.ReplaceAll(InstalledMods.Where(m
            => InstalledModFilter.Matches(m.FileName, words)));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(InstalledCountText));
    }

    // --- The players' pack ---

    /// <summary>
    /// The client-only mods added for this server's players: Sodium, MaLiLib, a minimap. The server
    /// never loads them (see <see cref="ClientPack"/>); the export puts them in the pack.
    /// </summary>
    public ObservableCollection<ModItem> ClientPackMods { get; } = new();

    public bool HasClientPack => ClientPackMods.Count > 0;

    /// <summary>"Players' pack (3)".</summary>
    public string ClientPackTitleText => string.Format(Localizer.Get("PlayerPack_TitleFmt"),
        ClientPackMods.Count);

    private void RefreshClientPack()
    {
        ClientPackMods.Clear();
        if (!IsPluginBased)
            foreach (var file in ClientPack.Jars(_config))
                ClientPackMods.Add(new ModItem(file, Path.GetFileName(file), isEnabled: true));

        OnPropertyChanged(nameof(HasClientPack));
        OnPropertyChanged(nameof(ClientPackTitleText));
    }

    /// <summary>
    /// Takes a jar out of the pack. Not asked first, unlike deleting from the server: it is a copy of
    /// something the store has, and adding it back is one click.
    /// </summary>
    [RelayCommand]
    private void RemoveFromClientPack(ModItem? mod)
    {
        if (mod is null) return;
        try { File.Delete(mod.FilePath); }
        catch (Exception ex)
        {
            Notify(string.Format(Localizer.Get("Msg_ModDeleteError"), ErrorMessages.Describe(ex)),
                failed: true);
        }
        RefreshClientPack();
    }

    // --- Marketplace State ---

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchStatus = string.Empty;

    // --- Which side of the game the results run on ---

    /// <summary>
    /// The three ways to cut the browser by where a project runs: everything, what can run on the
    /// server, and the client-only mods that go in the players' pack.
    /// </summary>
    public IReadOnlyList<string> SideOptions { get; } = new[]
    {
        Localizer.Get("Side_All"),
        Localizer.Get("Side_Server"),
        Localizer.Get("Side_ClientOnly"),
    };

    /// <summary>Index into <see cref="SideOptions"/>. Everything by default: nothing is hidden.</summary>
    [ObservableProperty]
    private int _selectedSideIndex;

    /// <summary>
    /// Plugins have no client side, so the choice does not exist on a Paper server and the control
    /// is not drawn.
    /// </summary>
    public bool ShowsSideFilter => !IsPluginBased;

    private StoreSideFilter SideFilter => IsPluginBased ? StoreSideFilter.ServerSide
        : SelectedSideIndex switch
        {
            1 => StoreSideFilter.ServerSide,
            2 => StoreSideFilter.ClientOnly,
            _ => StoreSideFilter.All
        };

    partial void OnSelectedSideIndexChanged(int value)
    {
        RebuildActiveFilters();
        if (_hasLoadedOnce) _ = LoadPageAsync(append: false, CancellationToken.None);
    }

    // --- Installed mods update-check state ---

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    /// <summary>The newer versions the last update check found; see <see cref="PendingUpdateStore"/>.</summary>
    private readonly PendingUpdateStore _pendingUpdates;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCleanupCommand))]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelectedCleanup))]
    private bool _isUpdatingAll;

    /// <summary>How many rows are offering an update right now.</summary>
    public int PendingUpdateCount => InstalledMods.Count(m => m.Update is not null);

    public bool HasPendingUpdates => PendingUpdateCount > 0;

    /// <summary>The label of the button that updates them all, with how many there are.</summary>
    public string UpdateAllText => string.Format(Localizer.Get("Mods_UpdateAllFmt"), PendingUpdateCount);

    private void NotifyPendingUpdatesChanged()
    {
        OnPropertyChanged(nameof(PendingUpdateCount));
        OnPropertyChanged(nameof(HasPendingUpdates));
        OnPropertyChanged(nameof(UpdateAllText));
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The key a jar's pending update is kept under: its path with any .disabled removed.</summary>
    internal static string PendingKey(string filePath) => PendingUpdateStore.KeyFor(filePath);

    /// <summary>Starts a fresh set of pending updates, for the server as it is now.</summary>
    internal void BeginPendingUpdates() => _pendingUpdates.Begin();

    /// <summary>Remembers a newer version for a jar, as the check saw it.</summary>
    internal void RememberUpdate(string filePath, ModUpdateInfo info) => _pendingUpdates.Remember(filePath, info);

    /// <summary>The update still waiting for this jar, or null.</summary>
    internal ModUpdateInfo? PendingUpdateFor(string filePath) => _pendingUpdates.For(filePath);

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

    private bool _hasLoadedOnce;

    /// <summary>Asks the store for pages and keeps track of which search counts; see <see cref="StoreBrowser"/>.</summary>
    private readonly StoreBrowser _browser;

    /// <summary>
    /// The results, flat. Replaced in one go rather than emptied and refilled: a list that sits
    /// empty across a network round trip makes the whole panel blink out and back.
    /// </summary>
    public BulkObservableCollection<ModrinthProjectViewModel> SearchResults { get; } = new();

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
        // Only when it narrows the results: "everything" is the absence of a filter, not a chip.
        if (ShowsSideFilter && SelectedSideIndex > 0)
            ActiveFilters.Add(new ActiveFilterViewModel(SideOptions[SelectedSideIndex], VersionChipBrush,
                SideOptions[SelectedSideIndex]));
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

        // Cut short and replaced, never left cancelled: a failed app update brings these same view
        // models back, and they must still be able to install.
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        _cleanupScan?.Cancel();

        _browser.CancelPending();

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

    /// <summary>
    /// What this server runs, "mod" or "plugin": the tag catalogue's chip filter, and what every
    /// <see cref="StoreItem"/> shown here is stamped with. Modrinth itself calls a plugin a "mod".
    /// </summary>
    internal string ProjectTypeName => IsPluginBased ? "plugin" : "mod";

    /// <summary>Folder where content is installed: "plugins" for Paper, "mods" otherwise.</summary>
    private string ContentFolder => ServerTypeCatalog.ContentFolder(_config.Type);

    // Labels shown in the view, adapted to mods vs plugins.
    public string ContentTabTitle => Localizer.Get(IsPluginBased ? "Plugins" : "Mods");
    public string BrowseTitle => Localizer.Get(IsPluginBased ? "Browse_Plugins" : "Browse_Mods");
    public string InstalledTitle => Localizer.Get(IsPluginBased ? "Installed_Plugins" : "Installed_Mods");
    public string SearchPlaceholder => Localizer.Get(IsPluginBased ? "SearchPlugins_Placeholder"
        : "SearchMods_Placeholder");
    public string NoInstalledText => Localizer.Get(IsPluginBased ? "No_Installed_Plugins"
        : "No_Installed_Mods");
    public string InstalledFilterPlaceholder => Localizer.Get(IsPluginBased ? "Plugins_InstalledFilter"
        : "Mods_InstalledFilter");

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
        _pendingUpdates = new PendingUpdateStore(config);
        _index = new InstalledContentIndex(_modrinthService);
        // Shares the HTTP client and the store cache with everything else the panel asks for.
        _dependencyInstaller = new ModDependencyInstaller(config, _modrinthService,
            new ModDependencyService(_modrinthService), _index);
        _updater = new ModUpdater(config, _modrinthService, _index, _pendingUpdates, CarryDisabledDate);
        _installer = new ModInstaller(config, _modrinthService, _index, _dependencyInstaller);
        _browser = new StoreBrowser(config, _modrinthService);
        _exporter = new ModpackExporter(config, _index, _modrinthService, () => HowToPlaySteps);
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
        // Same scan, same staleness: the missing-library offer names jars found last time round,
        // and after a refresh those may be installed, deleted, or for the family this no longer is.
        ClearMissingDependencies();
        var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);
        if (Directory.Exists(modsFolder))
        {
            // Old copies an install could not finish deleting, because the server still held them.
            ContentReplacement.SweepLeftovers(modsFolder);

            // Include disabled mods (.jar.disabled) so they can be re-enabled from the app.
            var files = InstalledContentIndex.ContentFiles(modsFolder)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            _index.Manifests.ForgetAllBut(files);

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var isEnabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var display = ContentReplacement.DisplayName(file);

                // Rebuilt rows get back the update the last check found for them. This is the whole
                // fix: the list is rebuilt after every update, and used to forget every other one.
                InstalledMods.Add(new ModItem(file, display, isEnabled) { Update = PendingUpdateFor(file) });
            }
        }

        // The line under the list follows what is actually still pending, instead of being blanked
        // because the rows had lost track of it.
        UpdateStatus = PendingUpdateCount > 0
            ? string.Format(Localizer.Get("Msg_UpdatesFoundFmt"), PendingUpdateCount)
            : string.Empty;
        NotifyPendingUpdatesChanged();

        // After the scan, so a search typed before a rescan still applies to what is there now.
        RebuildVisibleInstalled();
        RefreshClientPack();

        // Whatever changed the folder may have changed what can be cleaned up.
        RescanCleanup();
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
            var check = await _updater.CheckAsync(InstalledMods, ct);
            if (!check.CouldAsk)
            {
                UpdateStatus = UpdateStatusText(couldAsk: false, updates: 0);
                return;
            }

            UpdateStatus = UpdateStatusText(couldAsk: true, check.Updates);
            NotifyPendingUpdatesChanged();

            await ScanForMissingDependenciesAsync(check.Hashes, ct);
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(Localizer.Get("Msg_ModErrorFmt"), ErrorMessages.Describe(ex));
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private bool CanCheckUpdates => !IsCheckingUpdates && !IsUpdatingAll;

    /// <summary>The line the update check leaves under the list; see <see cref="ModUpdater.StatusText"/>.</summary>
    internal static string UpdateStatusText(bool couldAsk, int updates) =>
        ModUpdater.StatusText(couldAsk, updates);

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

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
        var plan = await _dependencyInstaller.FindMissingAsync(sha1Hashes,
            InstalledMods.Select(m => m.FilePath).ToList(), ct);
        if (plan is not null) ShowMissingDependencies(plan);
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
        if (!await _dependencyInstaller.InstallByIdAsync(ids, log, ct)) return;

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
            // Inline, not posted: the line below must not be overtaken by a late "downloading".
            await _dependencyInstaller.InstallListedAsync(_missingDependencies,
                new InlineProgress<string>(message => UpdateStatus = message), ct);

            UpdateStatus = string.Format(Localizer.Get("Msg_DepsInstalledFmt"), _missingDependencies.Count);
            ClearMissingDependencies();
            RefreshInstalledMods();
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(Localizer.Get("Msg_ModErrorFmt"), ErrorMessages.Describe(ex));
        }
        finally
        {
            IsInstallingDependencies = false;
        }
    }

    /// <summary>The ids the enabled jars in a folder answer to; see <see cref="InstalledContentIndex"/>.</summary>
    internal static HashSet<string> ModIdsProvidedIn(string folder) =>
        InstalledContentIndex.ModIdsProvidedIn(folder);

    /// <summary>Whether a jar is the main file of its version; see <see cref="InstalledContentIndex"/>.</summary>
    internal static bool IsPrimaryFile(VersionResult version, string sha1) =>
        InstalledContentIndex.IsPrimaryFile(version, sha1);

    private Task<Dictionary<string, string>> ProjectByPathAsync(string folder, CancellationToken ct = default) =>
        _index.ProjectByPathAsync(folder, ct);

    private Task<List<string>> InstalledProjectIdsAsync(CancellationToken ct, string? alsoFolder = null) =>
        _index.InstalledProjectIdsAsync(ContentManifest.FolderOf(_config), alsoFolder, ct);

    /// <summary>
    /// Downloads the newer version flagged by <see cref="CheckUpdates"/> and replaces the old jar.
    /// </summary>
    [RelayCommand]
    private async Task UpdateMod(ModItem? mod)
    {
        // While "update all" is running it owns the list: a second update started from a row would
        // race it for the same folder.
        if (mod?.Update is null || mod.IsUpdating || IsUpdatingAll) return;

        var result = await _updater.UpdateOneAsync(mod, _lifetime.Token);
        RefreshInstalledMods();

        // Set after the refresh, which rewrites the line with what is still pending — before, the
        // refresh wiped "updated" the instant it was written, so it was never seen.
        UpdateStatus = result.Outcome switch
        {
            ContentReplacement.UpdateOutcome.Updated when PendingUpdateCount > 0 =>
                string.Format(Localizer.Get("Msg_ModUpdatedRemainingFmt"), PendingUpdateCount),
            ContentReplacement.UpdateOutcome.Updated => Localizer.Get("Msg_ModUpdated"),
            ContentReplacement.UpdateOutcome.NeedsStop => Localizer.Get("Msg_UpdateNeedsStop"),
            _ => string.Format(Localizer.Get("Msg_UpdateErrorFmt"), result.Error)
        };

        // Only "updated". The old version and any other copy going with it is what updating means,
        // not news: listing the files deleted read as if the app had done something unusual.
    }

    /// <summary>Updates every mod the last check found a newer version for; see <see cref="ModUpdater.UpdateAllAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAll()
    {
        var queue = InstalledMods.Where(m => m.Update is not null && !m.IsUpdating).ToList();
        if (queue.Count == 0) return;

        IsUpdatingAll = true;
        ModUpdater.AllResult result;
        try
        {
            // Inline, not posted: the summary below must not be overtaken by a late "updating 3 of 5".
            result = await _updater.UpdateAllAsync(queue,
                new InlineProgress<string>(message => UpdateStatus = message), _lifetime.Token);
        }
        finally
        {
            IsUpdatingAll = false;
            RefreshInstalledMods();
        }

        UpdateStatus = ModUpdater.AllSummary(result);
    }

    private bool CanUpdateAll => HasPendingUpdates && !IsUpdatingAll && !IsCheckingUpdates;

    /// <summary>The line "update all" leaves behind; see <see cref="ModUpdater.AllSummary"/>.</summary>
    internal static string UpdateAllSummary(int updated, IReadOnlyList<string> failed, bool stoppedByServer) =>
        ModUpdater.AllSummary(new ModUpdater.AllResult(updated, failed, stoppedByServer));

    [RelayCommand]
    private void ToggleMod(ModItem? mod)
    {
        if (mod is null) return;
        var newExt = mod.IsEnabled ? ".disabled" : "";
        var newFile = mod.FilePath.Replace(".jar.disabled", ".jar") + newExt;

        try
        {
            File.Move(mod.FilePath, newFile);
            RecordToggle(newFile, nowEnabled: !mod.IsEnabled);
        }
        catch { /* ignore; the refresh below resyncs the toggle with the real file state */ }
        RefreshInstalledMods();
    }

    [RelayCommand]
    private async Task DeleteMod(ModItem? mod)
    {
        if (mod is null) return;

        // Asked first, like deleting a backup: the file is gone for good, not to the recycle bin,
        // and the button sits one row from the switch that only disables a mod.
        if (!await Dialogs.Current.ConfirmAsync(
                string.Format(Localizer.Get("Mods_ConfirmDeleteFmt"), mod.FileName), ContentTabTitle))
            return;

        try
        {
            File.Delete(mod.FilePath);
        }
        catch (Exception ex)
        {
            // Typically the file is locked because the server is running.
            await Dialogs.Current.ShowAsync(
                string.Format(Localizer.Get("Msg_ModDeleteError"),
                    ErrorMessages.Describe(ex)), ContentTabTitle);
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

        var top = AppWindows.Main;
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
            var result = await BuildModpackAsync(file.Path.LocalPath, includeEverything: false,
                CancellationToken.None);
            _lastExportPath = file.Path.LocalPath;
            _lastExportLeftSomethingOut = result.Excluded.Count > 0;
            ExportNotice = ModpackExporter.NoticeFor(result);
        }
        catch (Exception ex)
        {
            await Dialogs.Current.ShowAsync(
                string.Format(Localizer.Get("Msg_ExportError"),
                    ErrorMessages.Describe(ex)), Localizer.Get("Export_Modpack"));
        }
    }

    /// <summary>Builds the pack at <paramref name="destination"/>; see <see cref="ModpackExporter"/>.</summary>
    internal Task<ModpackResult> BuildModpackAsync(string destination, bool includeEverything, CancellationToken ct) =>
        _exporter.BuildAsync(destination, includeEverything, ct);

    /// <summary>The half of the export that decides and writes, with the store's answers in hand.</summary>
    internal Task<ModpackResult> BuildModpackWithSidesAsync(
        string destination,
        IReadOnlyDictionary<string, (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)> storeSides,
        bool includeEverything,
        ClientLoaderInstall.Plan? loader,
        CancellationToken ct) =>
        _exporter.BuildWithSidesAsync(destination, storeSides, includeEverything, loader, ct);

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
            ExportNotice = ModpackExporter.NoticeFor(result);
        }
        catch (Exception ex)
        {
            await Dialogs.Current.ShowAsync(
                string.Format(Localizer.Get("Msg_ExportError"),
                    ErrorMessages.Describe(ex)), Localizer.Get("Export_Modpack"));
        }
    }

    /// <summary>Loads the first page of mods the first time the Mods tab is shown.</summary>
    public void EnsureLoaded()
    {
        // Every time the tab is shown, not just the first: what was off for 29 days yesterday may
        // be worth suggesting today.
        ArmCleanup();

        // The catalogue that tags and describes the results is a file downloaded at most once a
        // day. Asked for here, behind the first search, so that nothing waits for it: cards built
        // before it arrives use the last copy on disk, and the next ones use the new one.
        _ = StoreCatalogService.Shared.RefreshAsync(_lifetime.Token);

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
        if (!_browser.CanSearch)
        {
            SearchStatus = Localizer.Get("Msg_ModBrowserNeedsLoader");
            SetResults(Array.Empty<ModrinthProjectViewModel>(), append: false);
            CanLoadMore = false;
            return;
        }

        IsSearching = true;
        _hasLoadedOnce = true;

        var result = await _browser.LoadAsync(SearchQuery, SelectedTags.Select(t => t.Definition).ToList(),
            SelectedSortIndex, SideFilter, append, ct);
        try
        {
            ShowSearchResult(result, append);
        }
        finally
        {
            // Only the search still in charge may clear the spinner.
            if (result.StillInCharge) IsSearching = false;
        }
    }

    /// <summary>Draws what a load found: the page, or the line that says why there is none.</summary>
    private void ShowSearchResult(StoreBrowser.LoadResult result, bool append)
    {
        switch (result.Kind)
        {
            case StoreBrowser.Kind.Page:
                var response = result.Response!;
                SetResults(response.Hits.Select(hit => new ModrinthProjectViewModel(hit, this)), append);
                CanLoadMore = response.Hits.Count > 0 && SearchResults.Count < response.TotalHits;
                SearchStatus = SearchResults.Count > 0
                    ? string.Format(Localizer.Get("Msg_ModsFoundFmt"), response.TotalHits)
                    : Localizer.Get("Msg_NoModsFound");
                break;

            case StoreBrowser.Kind.NoAnswer when !append:
                // Keep whatever was on screen: an empty panel says less than the old results plus
                // an explanation of why they didn't refresh.
                SearchStatus = Localizer.Get("Msg_NoModsFound");
                CanLoadMore = false;
                break;

            case StoreBrowser.Kind.Failed:
                SearchStatus = string.Format(Localizer.Get("Msg_ModErrorFmt"), result.Error);
                break;
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

    /// <summary>The newest build of a project for this server's loader and version, or null when there is none.</summary>
    /// <remarks>For the Performance page, which only offers what can actually be installed.</remarks>
    internal async Task<VersionResult?> FindCompatibleAsync(string slug, CancellationToken ct = default)
    {
        try
        {
            return await _modrinthService.GetLatestProjectVersionAsync(slug, _config.Type, _config.GameVersion, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // offline: nothing can be offered, and nothing is claimed
        }
    }

    /// <summary>
    /// The ids the installed jars answer to and their file names, for recognising what is already there.
    /// </summary>
    internal (HashSet<string> Ids, List<string> Files) InstalledContent()
    {
        var folder = Path.Combine(_config.FolderPath, ContentFolder);
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.jar").Select(Path.GetFileName).OfType<string>().ToList()
            : [];
        return (ModIdsProvidedIn(folder), files);
    }

    /// <summary>
    /// Installs a project. <paramref name="chosen"/> installs that exact version (the details page
    /// offers the last few); without it the newest compatible one is resolved, as before.
    /// This is the only install path in the app — the details page routes through it too.
    /// </summary>
    /// <param name="projectId">The project to install.</param>
    /// <param name="chosen">One exact version, or null for the newest compatible one.</param>
    /// <param name="toPlayerPack">
    /// Put it in the players' pack instead of the server's folder: for the client-only mods, which
    /// do nothing on a server. Everything else about the install is the same.
    /// </param>
    /// <returns>True when the mod ended up installed.</returns>
    public async Task<bool> InstallModAsync(string projectId, VersionResult? chosen = null,
        bool toPlayerPack = false)
    {
        // A plugin server has no players' pack: its content is the folder.
        toPlayerPack &= !IsPluginBased;

        IsSearching = true;
        try
        {
            // transient: progress, not an outcome — it must not time out while the download runs.
            var result = await _installer.InstallAsync(projectId, chosen, toPlayerPack,
                new InlineProgress<string>(message => Notify(message, failed: false, transient: true)),
                _lifetime.Token);
            return ShowInstallResult(result, toPlayerPack);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Puts what an install did in the banner, and brings the list up to date with the folder.</summary>
    /// <returns>True when the mod ended up installed.</returns>
    private bool ShowInstallResult(ModInstaller.Result result, bool toPlayerPack)
    {
        switch (result.Outcome)
        {
            case ModInstaller.Outcome.NoCompatibleVersion:
                Notify(Localizer.Get("Msg_NoCompatibleVersion"), failed: true);
                return false;

            case ModInstaller.Outcome.InUse:
                // An install, not an update: say which copy is in the way, not "before updating".
                RefreshInstalledMods();
                Notify(string.Format(Localizer.Get("Msg_ContentInUseFmt"), result.BlockingCopy), failed: true);
                return false;

            case ModInstaller.Outcome.Failed:
                Notify(string.Format(Localizer.Get("Msg_InstallErrorFmt"), result.Error), failed: true);
                return false;
        }

        RefreshInstalledMods();

        // A dependency with no build for this Minecraft version is not an install failure — the
        // mod is on disk — but it is the reason the server will not start, so it stays on screen
        // instead of fading away with the success message.
        if (result.UnresolvedLibraries is { Count: > 0 } unresolved)
            Notify(string.Format(Localizer.Get("Msg_DepUnresolvedFmt"), string.Join(", ", unresolved)),
                failed: true);
        else
            Notify(result.Libraries == 0
                    ? Localizer.Get(toPlayerPack ? "Msg_AddedToPlayerPack" : "Msg_ModInstalled")
                    : string.Format(Localizer.Get(toPlayerPack ? "Msg_AddedToPlayerPackWithDepsFmt"
                        : "Msg_ModInstalledWithDepsFmt"), result.Libraries),
                failed: false);
        return true;
    }
}
