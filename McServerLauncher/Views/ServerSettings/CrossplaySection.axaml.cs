using Avalonia.Controls;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>
/// Who else can play: Bedrock players, other Java versions, and Bedrock on a modded server.
/// </summary>
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

        _ = RefreshFirewallAsync();
    }

    // --- Windows Firewall ---

    private readonly WindowsFirewallService _firewall = new();
    private FirewallState _firewallState = FirewallState.Unavailable;
    private bool _firewallBusy;

    /// <summary>The port the card is about: the saved one, and none when crossplay is not saved as on.</summary>
    private int? FirewallPort => CrossplayService.EffectiveBedrockPort(_draft.Live);

    /// <summary>Reads the firewall off the UI thread and shows what it says.</summary>
    private async Task RefreshFirewallAsync()
    {
        var port = FirewallPort;
        if (!WindowsFirewallService.IsSupported || port is not { } p)
        {
            FirewallCard.IsVisible = false;
            return;
        }

        var state = await Task.Run(() => _firewall.GetState(p));
        // The page may have moved on while Windows answered (another type, crossplay switched off).
        if (FirewallPort != p) return;

        _firewallState = state;
        ShowFirewall(p);
    }

    private void ShowFirewall(int port)
    {
        FirewallCard.IsVisible = _firewallState != FirewallState.Unavailable;
        if (!FirewallCard.IsVisible) return;

        FirewallStatus.Text = Localizer.Format(_firewallState switch
        {
            FirewallState.Open => "Firewall_StatusOpenFmt",
            FirewallState.OpenElsewhere => "Firewall_StatusElsewhereFmt",
            _ => "Firewall_StatusClosedFmt"
        }, port);
        FirewallHint.Text = Localizer.Format("Firewall_HintFmt", port);
        FirewallHint.IsVisible = _firewallState == FirewallState.Closed;

        // Nothing to press when somebody else's rule already does the job: removing it is not ours.
        FirewallButton.IsVisible = _firewallState != FirewallState.OpenElsewhere;
        FirewallButton.IsEnabled = !_firewallBusy;
        FirewallButton.Content = Localizer.Get(_firewallBusy ? "Firewall_Working"
            : _firewallState == FirewallState.Open ? "Firewall_Remove" : "Firewall_Allow");
    }

    private async void Firewall_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_firewallBusy || FirewallPort is not { } port) return;

        _firewallBusy = true;
        FirewallNote.IsVisible = false;
        ShowFirewall(port);

        var outcome = _firewallState == FirewallState.Open
            ? await _firewall.RemoveAsync(port)
            : await _firewall.AllowAsync(port);

        _firewallBusy = false;
        if (outcome != FirewallOutcome.Done)
        {
            FirewallNote.Text = Localizer.Get(outcome == FirewallOutcome.Cancelled
                ? "Firewall_Cancelled" : "Firewall_Failed");
            FirewallNote.IsVisible = true;
        }
        await RefreshFirewallAsync();
    }

    /// <inheritdoc />
    public void ShowError(string? message) { }
}
