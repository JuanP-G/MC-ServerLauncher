using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

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

    /// <summary>True for a mod that only runs on the client: it goes to the players' pack.</summary>
    public bool IsClientOnly => _item.Side == InstallSide.ClientOnly && !_parent.IsPluginBased;

    /// <summary>What the install button says: where the mod is going, when it is not the server.</summary>
    public string InstallLabel => Localizer.Get(IsClientOnly ? "Store_AddToPlayerPack" : "Install");

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Mod icon, loaded asynchronously through the shared image cache (best-effort).</summary>
    [ObservableProperty]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    public ModrinthProjectViewModel(ProjectResult project, ServerModsViewModel parent)
        : this(StoreItem.From(project, parent.ProjectTypeName), parent)
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
        await _parent.InstallModAsync(Id, toPlayerPack: IsClientOnly);
        IsInstalling = false;
    }
}
