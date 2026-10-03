using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

public partial class ServerConfigDialog : Window
{
    private readonly ServerPropertiesService _service = new();
    private readonly ServerConfig _config;
    private readonly Func<int, string?> _bedrockPortOwner;

    /// <summary>True once Save changed the Bedrock port, so the caller persists servers.json.</summary>
    public bool BedrockPortChanged { get; private set; }

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public ServerConfigDialog() : this(new ServerConfig()) { }

    /// <param name="config">The server being configured.</param>
    /// <param name="bedrockPortOwner">
    /// The name of the other server already on a Bedrock port, or null when none is. Lets the dialog
    /// refuse a port by saying whose it is, instead of letting two servers end up behind one tunnel.
    /// </param>
    public ServerConfigDialog(ServerConfig config, Func<int, string?>? bedrockPortOwner = null)
    {
        InitializeComponent();
        _config = config;
        _bedrockPortOwner = bedrockPortOwner ?? (_ => null);
        HeaderText.Text = string.Format(Localizer.Get("Cfg_HeaderFmt"), config.Name);
        Load();

        // Only with crossplay: without Geyser there is no Bedrock port to change.
        var bedrock = CrossplayService.EffectiveBedrockPort(config);
        BedrockPortCard.IsVisible = bedrock is not null;
        BedrockPortBox.Value = bedrock ?? CrossplayService.DefaultBedrockPort;
        BedrockPortBox.ValueChanged += (_, _) => BedrockPortWarning.IsVisible = false;
    }

    /// <summary>
    /// Applies a new Bedrock port, or says why not. Returns false when the port was refused.
    /// </summary>
    /// <remarks>
    /// Written to servers.json and to Geyser's own config together: the port Geyser binds and the
    /// one the app thinks the server has must never disagree. The public port in Geyser's config is
    /// left for the next tunnel refresh to fill in, because the tunnel for the new port may not
    /// exist yet.
    /// </remarks>
    internal bool TryApplyBedrockPort()
    {
        if (!BedrockPortCard.IsVisible) return true;

        var wanted = (int)(BedrockPortBox.Value ?? CrossplayService.DefaultBedrockPort);
        if (wanted == CrossplayService.EffectiveBedrockPort(_config)) return true;

        if (wanted < 1024 || wanted > 65535)
        {
            BedrockPortWarning.Text = Localizer.Get("Cfg_BedrockPortRange");
            BedrockPortWarning.IsVisible = true;
            BedrockPortCard.BringIntoView();
            return false;
        }

        if (_bedrockPortOwner(wanted) is { } owner)
        {
            BedrockPortWarning.Text = string.Format(Localizer.Get("Cfg_BedrockPortTakenFmt"), owner);
            BedrockPortWarning.IsVisible = true;
            BedrockPortCard.BringIntoView();
            return false;
        }

        _config.BedrockPort = wanted;
        new CrossplayService().WriteConfig(_config, null);
        BedrockPortChanged = true;
        return true;
    }

    private void Load()
    {
        var p = _service.Read(_config.PropertiesPath);

        MotdBox.Text = Get(p, "motd", "A Minecraft Server");
        SelectByTag(GamemodeBox, Get(p, "gamemode", "survival"));
        SelectByTag(DifficultyBox, Get(p, "difficulty", "easy"));
        MaxPlayersBox.Value = GetInt(p, "max-players", 20);

        PvpToggle.IsChecked = GetBool(p, "pvp", true);
        HardcoreToggle.IsChecked = GetBool(p, "hardcore", false);
        AllowFlightToggle.IsChecked = GetBool(p, "allow-flight", false);
        CommandBlockToggle.IsChecked = GetBool(p, "enable-command-block", false);
        SpawnProtectionBox.Value = GetInt(p, "spawn-protection", 16);

        ViewDistanceBox.Value = GetInt(p, "view-distance", 10);
        SimulationDistanceBox.Value = GetInt(p, "simulation-distance", 10);
        GenerateStructuresToggle.IsChecked = GetBool(p, "generate-structures", true);

        PortBox.Value = GetInt(p, "server-port", 25565);
        OnlineModeToggle.IsChecked = GetBool(p, "online-mode", true);
        WhitelistToggle.IsChecked = GetBool(p, "white-list", false);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["motd"] = MotdBox.Text?.Trim() ?? string.Empty,
            ["gamemode"] = TagOf(GamemodeBox) ?? "survival",
            ["difficulty"] = TagOf(DifficultyBox) ?? "easy",
            ["max-players"] = ((int)(MaxPlayersBox.Value ?? 20m)).ToString(),
            ["pvp"] = B(PvpToggle),
            ["hardcore"] = B(HardcoreToggle),
            ["allow-flight"] = B(AllowFlightToggle),
            ["enable-command-block"] = B(CommandBlockToggle),
            ["spawn-protection"] = ((int)(SpawnProtectionBox.Value ?? 16m)).ToString(),
            ["view-distance"] = ((int)(ViewDistanceBox.Value ?? 10m)).ToString(),
            ["simulation-distance"] = ((int)(SimulationDistanceBox.Value ?? 10m)).ToString(),
            ["generate-structures"] = B(GenerateStructuresToggle),
            ["server-port"] = ((int)(PortBox.Value ?? 25565m)).ToString(),
            ["online-mode"] = B(OnlineModeToggle),
            ["white-list"] = B(WhitelistToggle),
        };

        try
        {
            if (!TryApplyBedrockPort()) return;
            _service.Update(_config.PropertiesPath, changes);
            Close(true);
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_ConfigSaveError"), ex.Message),
                Localizer.Get("Cfg_Title"), this);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    // --- Helpers ---

    private static string Get(IDictionary<string, string> p, string key, string def)
        => p.TryGetValue(key, out var v) ? v : def;

    private static int GetInt(IDictionary<string, string> p, string key, int def)
        => p.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;

    private static bool GetBool(IDictionary<string, string> p, string key, bool def)
        => p.TryGetValue(key, out var v) ? v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) : def;

    private static string B(ToggleSwitch t) => t.IsChecked == true ? "true" : "false";

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && (item.Tag as string) == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }
}
