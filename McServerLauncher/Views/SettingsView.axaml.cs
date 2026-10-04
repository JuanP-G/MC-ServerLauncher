using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>The settings screen. Everything it does is in <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // The notification switches bind straight to the settings model, which has no change events
        // of its own. A switch flipped anywhere on the screen is the signal to save; posted, so the
        // binding has written the new value back before the save reads it.
        AddHandler(ToggleButton.IsCheckedChangedEvent, (_, _) =>
            Dispatcher.UIThread.Post(() => (DataContext as SettingsViewModel)?.Persist()),
            RoutingStrategies.Bubble);
    }
}
