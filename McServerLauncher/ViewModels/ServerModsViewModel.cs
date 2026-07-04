using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using McServerLauncher.Services;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

public partial class ServerModsViewModel : ObservableObject
{
    private readonly ServerConfig _config;
    private readonly ModrinthService _modrinthService = new();
    
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

    public ObservableCollection<ModrinthProjectViewModel> SearchResults { get; } = new();

    // --- Plugins vs mods (Paper uses plugins in plugins/; the loaders use mods in mods/) ---

    private bool IsPluginBased => _config.Type == ServerType.Paper;

    /// <summary>Folder where content is installed: "plugins" for Paper, "mods" otherwise.</summary>
    private string ContentFolder => IsPluginBased ? "plugins" : "mods";

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
    public IBrush FilterTypeBrush => _config.Type switch
    {
        ServerType.Vanilla => BrushTypeVanilla,
        ServerType.Fabric => BrushTypeFabric,
        ServerType.Forge => BrushTypeForge,
        ServerType.Paper => BrushTypePaper,
        _ => BrushTypeUnknown
    };

    // Same palette as ServerViewModel's type badges; immutable so they can be shared safely.
    private static readonly IBrush BrushTypeVanilla = new ImmutableSolidColorBrush(Color.Parse("#6E9E52"));
    private static readonly IBrush BrushTypeFabric = new ImmutableSolidColorBrush(Color.Parse("#B58D5A"));
    private static readonly IBrush BrushTypeForge = new ImmutableSolidColorBrush(Color.Parse("#5A8AB5"));
    private static readonly IBrush BrushTypePaper = new ImmutableSolidColorBrush(Color.Parse("#C0563E"));
    private static readonly IBrush BrushTypeUnknown = new ImmutableSolidColorBrush(Color.Parse("#6E7681"));

    // --- "How to play" instructions (depend on the server type) ---

    public string HowToPlayTitle => Localizer.Get("HowToPlay_Title");

    /// <summary>Instructions to play: install the loader client (mods) or just join (plugins).</summary>
    public string HowToPlaySteps => _config.Type switch
    {
        ServerType.Fabric => string.Format(Localizer.Get("HowToPlay_FabricFmt"), _config.GameVersion),
        ServerType.Forge => string.Format(Localizer.Get("HowToPlay_ForgeFmt"), _config.GameVersion),
        ServerType.Paper => string.Format(Localizer.Get("HowToPlay_PaperFmt"), _config.GameVersion),
        _ => string.Empty
    };

    public ServerModsViewModel(ServerConfig config)
    {
        _config = config;
        RefreshInstalledMods();
    }

    [RelayCommand]
    private void RefreshInstalledMods()
    {
        InstalledMods.Clear();
        // The rebuilt items carry no update flag, so drop any stale "N updates available" text.
        UpdateStatus = string.Empty;
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
                    var sha1 = await DownloadVerifier.ComputeHashAsync(mod.FilePath, HashAlgorithmName.SHA1, ct);
                    byHash[sha1] = mod;
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

        var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var tempMods = Path.Combine(tempFolder, ContentFolder);
            Directory.CreateDirectory(tempMods);

            foreach (var modFile in Directory.EnumerateFiles(modsFolder, "*.jar"))
            {
                File.Copy(modFile, Path.Combine(tempMods, Path.GetFileName(modFile)));
            }

            var instrPath = Path.Combine(tempFolder, Localizer.Get("Export_InstructionsFile"));
            var instructions = string.Format(Localizer.Get("Export_InstructionsFmt"),
                _config.Name, _config.Type, _config.GameVersion, HowToPlaySteps, ContentFolder);
            File.WriteAllText(instrPath, instructions);

            if (File.Exists(file.Path.LocalPath)) File.Delete(file.Path.LocalPath);
            System.IO.Compression.ZipFile.CreateFromDirectory(tempFolder, file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_ExportError"), ex.Message), Localizer.Get("Export_Modpack"));
        }
        finally
        {
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); }
            catch { /* best-effort cleanup */ }
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
            SearchResults.Clear();
            CanLoadMore = false;
            return;
        }

        if (!append)
        {
            _offset = 0;
            SearchResults.Clear();
            SearchStatus = Localizer.Get("Msg_SearchingModrinth");
        }

        IsSearching = true;
        _hasLoadedOnce = true;
        var index = SelectedSortIndex == 1 ? "downloads" : "relevance";

        try
        {
            var response = await _modrinthService.SearchModsAsync(
                SearchQuery ?? string.Empty, _config.Type, _config.GameVersion, index, _offset, PageSize, ct);

            if (response != null)
            {
                foreach (var hit in response.Hits)
                    SearchResults.Add(new ModrinthProjectViewModel(hit, this));

                _totalHits = response.TotalHits;
                _offset += response.Hits.Count;
                CanLoadMore = response.Hits.Count > 0 && SearchResults.Count < _totalHits;

                SearchStatus = SearchResults.Count > 0
                    ? string.Format(Localizer.Get("Msg_ModsFoundFmt"), _totalHits)
                    : Localizer.Get("Msg_NoModsFound");
            }
            else if (!append)
            {
                SearchStatus = Localizer.Get("Msg_NoModsFound");
                CanLoadMore = false;
            }
        }
        catch (Exception ex)
        {
            SearchStatus = string.Format(Localizer.Get("Msg_ModErrorFmt"), ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    public async Task InstallModAsync(string projectId)
    {
        IsSearching = true;
        SearchStatus = Localizer.Get("Msg_ResolvingVersion");

        try
        {
            var version = await _modrinthService.GetLatestProjectVersionAsync(projectId, _config.Type, _config.GameVersion);
            if (version == null || version.Files.Count == 0)
            {
                SearchStatus = Localizer.Get("Msg_NoCompatibleVersion");
                return;
            }

            var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.First();

            var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);
            Directory.CreateDirectory(modsFolder);
            // The filename comes from the Modrinth API: keep only the name so a malicious
            // value (e.g. "..\\x.jar") can never write outside the mods folder.
            var destPath = Path.Combine(modsFolder, Path.GetFileName(file.Filename));

            // If the same mod is already present but disabled, remove the disabled copy first so we
            // don't end up with two files (a new enabled one + an old disabled one) that then clash
            // when toggling. The download below overwrites any enabled copy of the same name.
            var disabledPath = destPath + ".disabled";
            try { if (File.Exists(disabledPath)) File.Delete(disabledPath); }
            catch { /* best-effort */ }

            SearchStatus = string.Format(Localizer.Get("Msg_DownloadingMod"), file.Filename);

            // Mods are third-party jars chosen by the user: verify against Modrinth's own checksum.
            await _modrinthService.DownloadModAsync(file.Url, destPath, file.Hashes?.Sha512, file.Hashes?.Sha1);

            SearchStatus = Localizer.Get("Msg_ModInstalled");
            RefreshInstalledMods();
        }
        catch (Exception ex)
        {
            SearchStatus = string.Format(Localizer.Get("Msg_InstallErrorFmt"), ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }
}

public partial class ModrinthProjectViewModel : ObservableObject
{
    private static readonly HttpClient IconHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ServerModsViewModel _parent;

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Author { get; }
    public int Downloads { get; }
    public string DownloadsText { get; }
    public string? IconUrl { get; }

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Mod icon, loaded asynchronously from <see cref="IconUrl"/> (best-effort).</summary>
    [ObservableProperty]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    public ModrinthProjectViewModel(ProjectResult project, ServerModsViewModel parent)
    {
        _parent = parent;
        Id = project.ProjectId;
        Title = project.Title;
        Description = project.Description;
        Author = project.Author;
        Downloads = project.Downloads;
        DownloadsText = FormatDownloads(project.Downloads);
        IconUrl = project.IconUrl;

        if (!string.IsNullOrEmpty(IconUrl))
            _ = LoadIconAsync(IconUrl);
    }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    private async Task LoadIconAsync(string url)
    {
        try
        {
            var bytes = await IconHttp.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            if (Dispatcher.UIThread.CheckAccess()) Icon = bmp;
            else Dispatcher.UIThread.Post(() => Icon = bmp);
        }
        catch
        {
            // Best-effort: webp/svg or network failures simply leave the placeholder.
        }
    }

    /// <summary>Compact download count, e.g. 1234 -> "1.2K", 3500000 -> "3.5M".</summary>
    private static string FormatDownloads(int downloads) => downloads switch
    {
        >= 1_000_000 => (downloads / 1_000_000.0).ToString("0.#") + "M",
        >= 1_000 => (downloads / 1_000.0).ToString("0.#") + "K",
        _ => downloads.ToString()
    };

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        await _parent.InstallModAsync(Id);
        IsInstalling = false;
    }
}
