using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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

    // --- "How to play" instructions (depend on the server type) ---

    public string HowToPlayTitle => Localizer.Get("HowToPlay_Title");

    /// <summary>Step-by-step instructions to install the matching loader client and add the mods.</summary>
    public string HowToPlaySteps => _config.Type switch
    {
        ServerType.Fabric => string.Format(Localizer.Get("HowToPlay_FabricFmt"), _config.GameVersion),
        ServerType.Forge => string.Format(Localizer.Get("HowToPlay_ForgeFmt"), _config.GameVersion),
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
        var modsFolder = Path.Combine(_config.FolderPath, "mods");
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
    private void DeleteMod(ModItem? mod)
    {
        if (mod is null) return;
        try
        {
            File.Delete(mod.FilePath);
            RefreshInstalledMods();
        }
        catch { }
    }

    [RelayCommand]
    private async Task ExportModpack()
    {
        var modsFolder = Path.Combine(_config.FolderPath, "mods");
        if (!Directory.Exists(modsFolder)) return;

        var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = Localizer.Get("Export_Modpack"),
            DefaultExtension = "zip",
            SuggestedFileName = $"{_config.Name}-Modpack.zip",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("ZIP Archive") { Patterns = new[] { "*.zip" } } }
        });

        if (file == null) return;

        try
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);
            var tempMods = Path.Combine(tempFolder, "mods");
            Directory.CreateDirectory(tempMods);

            foreach (var modFile in Directory.EnumerateFiles(modsFolder, "*.jar"))
            {
                File.Copy(modFile, Path.Combine(tempMods, Path.GetFileName(modFile)));
            }

            var instrPath = Path.Combine(tempFolder, "INSTRUCCIONES.txt");
            var instructions = string.Format(Localizer.Get("Export_InstructionsFmt"),
                _config.Name, _config.Type, _config.GameVersion, HowToPlaySteps);
            File.WriteAllText(instrPath, instructions);

            if (File.Exists(file.Path.LocalPath)) File.Delete(file.Path.LocalPath);
            System.IO.Compression.ZipFile.CreateFromDirectory(tempFolder, file.Path.LocalPath);
            Directory.Delete(tempFolder, true);
        }
        catch { }
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

            var modsFolder = Path.Combine(_config.FolderPath, "mods");
            Directory.CreateDirectory(modsFolder);
            var destPath = Path.Combine(modsFolder, file.Filename);

            SearchStatus = string.Format(Localizer.Get("Msg_DownloadingMod"), file.Filename);

            await _modrinthService.DownloadModAsync(file.Url, destPath);

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
