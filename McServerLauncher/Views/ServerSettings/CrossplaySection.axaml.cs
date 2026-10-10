using Avalonia.Controls;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>Who else can play: Bedrock players, other Java versions, and Bedrock on a modded server.</summary>
/// <remarks>
/// The switches the type cannot do are greyed out with why. Switching them off is the draft's, not
/// this page's (<see cref="ServerSettingsDraft"/> coerces to the type when it opens and after a
/// loader install), so that what the boxes show and what Save writes cannot disagree.
/// </remarks>
public partial class CrossplaySection : UserControl, IServerSettingsSection
{
    private readonly ServerSettingsDraft _draft;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public CrossplaySection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public CrossplaySection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        DataContext = draft.Config;
        Reload();
    }

    /// <inheritdoc />
    /// <remarks>Also after a loader install: the type is what every line here depends on.</remarks>
    public void Reload()
    {
        var type = _draft.Live.Type;

        var supported = CrossplayService.CanEnable(type);
        CrossplayCheck.IsEnabled = supported;
        CrossplayHint.IsVisible = supported;
        CrossplayWhyNot.IsVisible = !supported;
        if (!supported)
            CrossplayWhyNot.Text = Localizer.Get(type == ServerType.Vanilla
                ? "Crossplay_UnsupportedVanilla"
                : "Crossplay_Unsupported");
        var caveat = CrossplayService.CaveatKey(type);
        CrossplayModdedNote.IsVisible = supported && caveat is not null;
        if (caveat is not null) CrossplayModdedNote.Text = Localizer.Get(caveat);

        var multiVersion = MultiVersionService.CanEnable(type);
        MultiVersionCheck.IsEnabled = multiVersion;
        MultiVersionHint.IsVisible = multiVersion;
        MultiVersionWhyNot.IsVisible = !multiVersion;

        var modContent = HydraulicService.CanEnable(type);
        HydraulicCheck.IsEnabled = modContent;
        HydraulicHint.IsVisible = modContent;
        HydraulicWhyNot.IsVisible = !modContent;
    }

    /// <inheritdoc />
    public void ShowError(string? message) { }
}
