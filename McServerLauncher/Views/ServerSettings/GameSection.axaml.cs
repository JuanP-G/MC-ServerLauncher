using Avalonia.Controls;
using McServerLauncher.Models;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>How a game on this server plays: mode, difficulty, players and the gameplay switches.</summary>
public partial class GameSection : UserControl, IServerSettingsSection
{
    private readonly PropertyBindings _bindings;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public GameSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public GameSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _bindings = new PropertyBindings(draft);
        _bindings.Choice(GamemodeBox, "gamemode", "survival");
        _bindings.Choice(DifficultyBox, "difficulty", "easy");
        _bindings.Number(MaxPlayersBox, "max-players", 20);
        _bindings.Toggle(PvpToggle, "pvp", true);
        _bindings.Toggle(HardcoreToggle, "hardcore", false);
        _bindings.Toggle(AllowFlightToggle, "allow-flight", false);
        _bindings.Toggle(CommandBlockToggle, "enable-command-block", false);
        _bindings.Number(SpawnProtectionBox, "spawn-protection", 16);
        Reload();
    }

    /// <inheritdoc />
    public void Reload() => _bindings.Load();

    /// <inheritdoc />
    public void ShowError(string? message) { }
}
