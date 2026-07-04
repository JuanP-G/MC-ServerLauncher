using Avalonia.Controls;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

public partial class ServerBackupsView : UserControl
{
    public ServerBackupsView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ServerBackupsViewModel)?.EnsureLoaded();
    }
}
