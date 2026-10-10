using Avalonia.Controls;
using McServerLauncher.Models;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>Starting the server when someone joins, and stopping it when nobody is left.</summary>
/// <remarks>Bound to the draft's copy of the config, so there is nothing to reload by hand.</remarks>
public partial class PowerSection : UserControl, IServerSettingsSection
{
    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public PowerSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public PowerSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        DataContext = draft.Config;
    }

    /// <inheritdoc />
    public void Reload() { }

    /// <inheritdoc />
    public void ShowError(string? message) { }
}
