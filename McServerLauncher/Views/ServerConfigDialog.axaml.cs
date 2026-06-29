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

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public ServerConfigDialog() : this(new ServerConfig()) { }

    public ServerConfigDialog(ServerConfig config)
    {
        InitializeComponent();
        _config = config;
        HeaderText.Text = string.Format(Localizer.Get("Cfg_HeaderFmt"), config.Name);
        Load();
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
