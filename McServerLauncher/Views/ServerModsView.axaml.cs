using Avalonia.Controls;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

public partial class ServerModsView : UserControl
{
    public ServerModsView()
    {
        InitializeComponent();
        // Browse the top mods for this loader+version as soon as the view appears,
        // so results show up without the user having to search first.
        Loaded += (_, _) => (DataContext as ServerModsViewModel)?.EnsureLoaded();
    }
}
