using Avalonia.Controls;

namespace McServerLauncher.Views;

/// <summary>
/// The players tab: connected, operators, whitelist, banned and known players.
/// </summary>
/// <remarks>
/// Pure markup over the view model — everything it does goes through commands, so it has no
/// code-behind of its own. Split out of <see cref="MainWindow"/> for the same reason as
/// <see cref="ServerConsoleView"/>: 143 lines of it were sitting in the middle of the window.
/// </remarks>
public partial class ServerPlayersView : UserControl
{
    public ServerPlayersView() => InitializeComponent();
}
