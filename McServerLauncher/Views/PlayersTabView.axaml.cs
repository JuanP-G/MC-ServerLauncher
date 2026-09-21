using Avalonia.Controls;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// The Players tab: who is connected, everyone the server has ever seen, and the op, whitelist and
/// ban lists. A player's profile replaces all of it while it is open.
/// </summary>
/// <remarks>
/// It lives in its own file so that the tab can say when it is on screen. The history list is the
/// only unbounded one here — a server with years of logs knows hundreds of names — and rebuilding it
/// for a tab nobody is looking at is work with no reader. <see cref="PlayerHistoryViewModel.EnsureLoaded"/>
/// is the same bargain <see cref="ServerBackupsView"/> already strikes with its list of backups.
/// </remarks>
public partial class PlayersTabView : UserControl
{
    public PlayersTabView()
    {
        InitializeComponent();
        Loaded += (_, _) => Ensure();
        // The tab is one control shared by every server: selecting another one swaps the data
        // context under it rather than building a new view, so Loaded alone would fire once.
        DataContextChanged += (_, _) => Ensure();
    }

    private void Ensure()
    {
        if (IsLoaded && DataContext is ServerViewModel server)
            server.History.EnsureLoaded();
    }
}
