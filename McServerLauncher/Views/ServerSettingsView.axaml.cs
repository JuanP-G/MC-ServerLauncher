using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Views;

/// <summary>
/// A server's settings, in the window: every page of them behind one index, saved together from a
/// bar that only shows while something is unsaved.
/// </summary>
/// <remarks>
/// <para>
/// It replaces three dialogs that overlapped — the edit dialog, the <c>server.properties</c> editor
/// and the card's appearance editor — two of which had the name and two the MOTD. Each page is a
/// control of its own under <c>Views/ServerSettings</c>; they all edit one
/// <see cref="ServerSettingsDraft"/>, and nothing reaches the server until Save.
/// </para>
/// <para>
/// The pages are made the first time they are shown. Most of them cost nothing, but the type page
/// reaches the network for the list of versions, and a page nobody opens should not.
/// </para>
/// <para>
/// Leaving with something unsaved is not allowed, and is not asked about in a window either:
/// <see cref="TryLeave"/> refuses and makes the bar say why. Saving or discarding is one click away
/// on that same bar.
/// </para>
/// </remarks>
public partial class ServerSettingsView : UserControl
{
    /// <summary>Below this width the index turns into a strip of chips above the page.</summary>
    private const double NarrowWidth = 640;

    private sealed record PageInfo(ServerSettingsPage Page, Symbol Icon, string Title, string Hint, string? Group);

    private static readonly PageInfo[] Pages =
    [
        new(ServerSettingsPage.Appearance, Symbol.Image, "Sset_Appearance", "Sset_AppearanceHint", null),
        new(ServerSettingsPage.Game, Symbol.Games, "Sset_Game", "Sset_GameHint", "Sset_GroupGame"),
        new(ServerSettingsPage.World, Symbol.Globe, "Sset_World", "Sset_WorldHint", null),
        new(ServerSettingsPage.Loader, Symbol.Box, "Sset_Loader", "Sset_LoaderHint", "Sset_GroupServer"),
        new(ServerSettingsPage.Performance, Symbol.Gauge, "Sset_Performance", "Sset_PerformanceHint", null),
        new(ServerSettingsPage.Network, Symbol.PlugConnected, "Sset_Network", "Sset_NetworkHint", null),
        new(ServerSettingsPage.Crossplay, Symbol.Phone, "Sset_Crossplay", "Sset_CrossplayHint", null),
        new(ServerSettingsPage.Power, Symbol.Power, "Sset_Power", "Sset_PowerHint", "Sset_GroupAuto"),
        new(ServerSettingsPage.Backups, Symbol.Archive, "Sset_Backups", "Sset_BackupsHint", null),
        new(ServerSettingsPage.Notifications, Symbol.Alert, "Sset_Notifications", "Sset_NotificationsHint", null),
        new(ServerSettingsPage.Advanced, Symbol.Wrench, "Sset_Advanced", "Sset_AdvancedHint", ""),
    ];

    private readonly ServerSettingsDraft _draft;
    private readonly Dictionary<ServerSettingsPage, Control> _sections = new();
    private readonly Dictionary<ServerSettingsPage, (Button Nav, ToggleButton Chip, Control Dot, Control ChipDot)> _items = new();
    private bool _nudged;
    private string? _problem;

    /// <summary>The server these are the settings of.</summary>
    public ServerViewModel Server { get; }

    /// <summary>The page on screen.</summary>
    public ServerSettingsPage Page { get; private set; }

    /// <summary>True while something on any page is unsaved.</summary>
    public bool IsDirty => _draft.IsDirty;

    /// <summary>Raised when the back button is pressed with nothing unsaved.</summary>
    public event Action? Closed;

    /// <summary>Raised after a save, with what it changed.</summary>
    public event Action<ServerSettingsSaved>? Saved;

    /// <summary>Raised once a loader install has changed the live config and the disk.</summary>
    public event Action? LoaderInstalled;

    /// <summary>Raised once this server's player history has been forgotten.</summary>
    public event Action? HistoryCleared;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public ServerSettingsView() : this(new ServerViewModel(new ServerConfig()), ServerSettingsPage.Game) { }

    /// <param name="server">The server to configure.</param>
    /// <param name="page">The page to open on.</param>
    /// <param name="bedrockPortOwner">Whose is a Bedrock port, when another server already has it.</param>
    public ServerSettingsView(ServerViewModel server, ServerSettingsPage page, Func<int, string?>? bedrockPortOwner = null)
    {
        InitializeComponent();
        Server = server;
        DataContext = server;
        _draft = new ServerSettingsDraft(server.Config, bedrockPortOwner);

        BuildIndex();
        _draft.Changed += () =>
        {
            // Whatever stopped the last save may be what was just changed.
            _problem = null;
            Refresh();
        };
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);

        Show(page);
        Refresh();
    }

    /// <summary>The draft the pages edit. For tests, and for nothing else.</summary>
    internal ServerSettingsDraft Draft => _draft;

    /// <summary>The control behind <paramref name="page"/>, made now if it has not been shown yet.</summary>
    internal Control Section(ServerSettingsPage page)
    {
        if (_sections.TryGetValue(page, out var made)) return made;

        Control section = page switch
        {
            ServerSettingsPage.Appearance => new AppearanceSection(_draft, Server.IsRunning),
            ServerSettingsPage.Game => new GameSection(_draft),
            ServerSettingsPage.World => new WorldSection(_draft),
            ServerSettingsPage.Loader => Loader(),
            ServerSettingsPage.Performance => new PerformanceSection(_draft),
            ServerSettingsPage.Network => new NetworkSection(_draft, Server.ConnectionTest),
            ServerSettingsPage.Crossplay => new CrossplaySection(_draft),
            ServerSettingsPage.Power => new PowerSection(_draft),
            ServerSettingsPage.Backups => new BackupsSection(_draft),
            ServerSettingsPage.Notifications => new NotificationsSection(_draft),
            ServerSettingsPage.Advanced => Advanced(),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        _sections[page] = section;
        return section;
    }

    private LoaderSection Loader()
    {
        var loader = new LoaderSection(_draft);
        loader.Installed += () =>
        {
            // The install wrote these into the live config; the page takes them as saved, and
            // every page that depends on the type looks again.
            _draft.Adopt(LoaderSection.InstalledFields);
            ReloadSections();
            LoaderInstalled?.Invoke();
        };
        return loader;
    }

    private AdvancedSection Advanced()
    {
        var advanced = new AdvancedSection(_draft);
        advanced.HistoryCleared += () => HistoryCleared?.Invoke();
        return advanced;
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>Puts <paramref name="page"/> on screen.</summary>
    public void Show(ServerSettingsPage page)
    {
        Page = page;
        var info = Pages.First(p => p.Page == page);
        PageTitle.Text = Localizer.Get(info.Title);
        PageHint.Text = Localizer.Get(info.Hint);
        SectionHost.Content = Section(page);
        PageScroller.Offset = default;

        foreach (var (p, item) in _items)
        {
            item.Nav.Classes.Set("on", p == page);
            item.Chip.IsChecked = p == page;
        }
        if (_items.TryGetValue(page, out var current)) current.Chip.BringIntoView();
    }

    /// <summary>
    /// True when the page can be left; otherwise the bar says it cannot, and why.
    /// </summary>
    public bool TryLeave()
    {
        if (!IsDirty) return true;
        Nudge();
        return false;
    }

    /// <summary>Makes the bar say that the unsaved changes are what is in the way.</summary>
    public void Nudge()
    {
        _nudged = true;
        Refresh();
    }

    private void BuildIndex()
    {
        foreach (var info in Pages)
        {
            if (info.Group is not null)
                PageItems.Children.Add(new TextBlock
                {
                    Classes = { "group" },
                    Text = info.Group.Length > 0 ? Localizer.Get(info.Group) : string.Empty,
                    IsVisible = info.Group.Length > 0,
                });
            // The last page stands alone: a gap above it instead of a heading.
            if (info.Group == string.Empty)
                PageItems.Children.Add(new Border { Height = 10 });

            var dot = Dot();
            var nav = new Button
            {
                Classes = { "page" },
                Content = Row(info, dot, chip: false),
                [ToolTip.TipProperty] = Localizer.Get(info.Hint),
            };
            nav.Click += (_, _) => Show(info.Page);
            PageItems.Children.Add(nav);

            var chipDot = Dot();
            var chip = new ToggleButton { Classes = { "chip" }, Content = Row(info, chipDot, chip: true) };
            chip.Click += (_, _) => Show(info.Page);
            ChipItems.Children.Add(chip);

            _items[info.Page] = (nav, chip, dot, chipDot);
        }

        // After the items: the marker follows the children the host has when it is given it.
        Marker.Host = PageItems;
    }

    private static Control Row(PageInfo info, Control dot, bool chip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = chip ? 5 : 8 };
        if (!chip) row.Children.Add(new SymbolIcon { Symbol = info.Icon, FontSize = 16 });
        row.Children.Add(new TextBlock { Text = Localizer.Get(info.Title), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(dot);
        return row;
    }

    // Says a page has something unsaved, beside its name.
    private Control Dot()
    {
        var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        dot.Bind(Shape.FillProperty, this.GetResourceObservable("SemanticWarn"));
        return dot;
    }

    private void ApplyWidth(double width)
    {
        var narrow = width < NarrowWidth;
        Classes.Set("narrow", narrow);
        Index.IsVisible = !narrow;
        ChipStrip.IsVisible = narrow;
    }

    // ---------------------------------------------------------------- the bar

    private void Refresh()
    {
        var dirty = _draft.IsDirty;
        if (!dirty) _nudged = false;

        foreach (var (page, item) in _items)
            item.Dot.IsVisible = item.ChipDot.IsVisible = _draft.IsPageDirty(page);

        SaveBar.IsVisible = dirty;
        SaveBar.Classes.Set("nudged", _nudged);
        SaveBarText.Text = Localizer.Get(_nudged ? "Sset_LeaveBlocked" : "Sset_Unsaved");

        var hint = _problem ?? (Server.IsRunning ? Localizer.Get("Sset_UnsavedRestart") : null);
        SaveBarHint.Text = hint;
        SaveBarHint.IsVisible = hint is not null;
        SaveBarHint.Classes.Set("error", _problem is not null);
    }

    private void ShowBarProblem(string? text)
    {
        _problem = text;
        Refresh();
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Save();

    /// <summary>Checks every page and writes what changed. Returns false when nothing could be saved.</summary>
    internal bool Save()
    {
        foreach (var section in _sections.Values.OfType<IServerSettingsSection>()) section.ShowError(null);
        ShowBarProblem(null);

        var problems = _draft.Validate();
        if (problems.Count > 0)
        {
            foreach (var (page, message) in problems)
                ((IServerSettingsSection)Section(page)).ShowError(message);
            Show(problems[0].Page);
            ShowBarProblem(Localizer.Get("Sset_FixErrors"));
            return false;
        }

        ServerSettingsSaved saved;
        try
        {
            saved = _draft.Apply();
        }
        catch (Exception ex)
        {
            ShowBarProblem(string.Format(Localizer.Get("Msg_ConfigSaveError"), ex.Message));
            return false;
        }

        ReloadSections();
        Saved?.Invoke(saved);
        return true;
    }

    private void Discard_Click(object? sender, RoutedEventArgs e) => Discard();

    /// <summary>Puts every page back the way it was saved.</summary>
    internal void Discard()
    {
        _draft.Revert();
        foreach (var section in _sections.Values.OfType<IServerSettingsSection>()) section.ShowError(null);
        ShowBarProblem(null);
        ReloadSections();
    }

    private void ReloadSections()
    {
        foreach (var section in _sections.Values.OfType<IServerSettingsSection>()) section.Reload();
        Refresh();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (TryLeave()) Closed?.Invoke();
    }
}
