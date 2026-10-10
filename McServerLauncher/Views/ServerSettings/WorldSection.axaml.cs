using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>The world: its seed, and the distances and structures that shape it.</summary>
public partial class WorldSection : UserControl, IServerSettingsSection
{
    private readonly ServerSettingsDraft _draft;
    private readonly PropertyBindings _bindings;
    private SeedInfo _seed = SeedInfo.None;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public WorldSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public WorldSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        _bindings = new PropertyBindings(draft);
        _bindings.Number(ViewDistanceBox, "view-distance", 10);
        _bindings.Number(SimulationDistanceBox, "simulation-distance", 10);
        _bindings.Toggle(GenerateStructuresToggle, "generate-structures", true);
        Reload();
    }

    /// <inheritdoc />
    public void Reload()
    {
        _bindings.Load();
        ShowSeed();
    }

    /// <inheritdoc />
    public void ShowError(string? message) { }

    /// <summary>
    /// The world's seed, shown and not edited.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose. <c>level-seed</c> only matters when a world is generated, and a server
    /// here keeps its world through every change of type or version, so a seed box would promise a
    /// change that never happens. The seed is chosen once, when the server is created.
    /// </remarks>
    private void ShowSeed()
    {
        var config = _draft.Live;
        _seed = WorldSeed.Read(config.FolderPath, config.LastKnownSeed);

        SeedText.Text = _seed.Seed?.ToString(CultureInfo.InvariantCulture) ?? "—";
        SeedNote.Text = Localizer.Get(_seed.Source switch
        {
            SeedSource.World => "Cfg_SeedFromWorld",
            SeedSource.Console => "Cfg_SeedFromConsole",
            SeedSource.Pending => "Cfg_SeedPending",
            _ => "Cfg_SeedUnknownHint"
        });

        CopySeedButton.IsEnabled = SeedMapButton.IsEnabled = _seed.Seed is not null;
    }

    private async void CopySeed_Click(object? sender, RoutedEventArgs e)
    {
        if (_seed.Seed is not { } seed || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(seed.ToString(CultureInfo.InvariantCulture)); }
        catch { /* clipboard busy */ }
    }

    private void SeedMap_Click(object? sender, RoutedEventArgs e)
    {
        if (_seed.Seed is { } seed) BrowserLauncher.Open(SeedMapLink.For(seed, _draft.Live.GameVersion));
    }
}
