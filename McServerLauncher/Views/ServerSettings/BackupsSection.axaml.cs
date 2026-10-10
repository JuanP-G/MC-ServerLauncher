using Avalonia.Controls;
using McServerLauncher.Models;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>When copies of the world are made, and how many of each are kept.</summary>
/// <remarks>Bound to the draft's copy of the config, so there is nothing to reload by hand.</remarks>
public partial class BackupsSection : UserControl, IServerSettingsSection
{
    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public BackupsSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public BackupsSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        DataContext = draft.Config;
    }

    /// <inheritdoc />
    public void Reload() { }

    /// <inheritdoc />
    public void ShowError(string? message) { }
}
