using Avalonia.Controls;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// The backups panel of a server, bound to <see cref="ServerBackupsViewModel"/>.
/// </summary>
public partial class ServerBackupsView : UserControl
{
    public ServerBackupsView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ServerBackupsViewModel)?.EnsureLoaded();
    }
}
