using Avalonia.Controls;
using McServerLauncher.Models;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>This server's own notifications, in place of the app's.</summary>
/// <remarks>Bound to the draft's copy of the config, so there is nothing to reload by hand.</remarks>
public partial class NotificationsSection : UserControl, IServerSettingsSection
{
    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public NotificationsSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public NotificationsSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        DataContext = draft.Config;
    }

    /// <inheritdoc />
    public void Reload() { }

    /// <inheritdoc />
    public void ShowError(string? message) { }
}
