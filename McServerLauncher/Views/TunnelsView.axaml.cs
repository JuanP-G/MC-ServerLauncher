using Avalonia.Controls;

namespace McServerLauncher.Views;

/// <summary>
/// The tunnels section: the Playit connection, the agent, and the addresses.
/// </summary>
/// <remarks>
/// Pure markup over the view model. It exists as a section rather than a block inside the server
/// detail because a tunnel belongs to the account, not to a server — and putting it in the middle
/// of the server pane cost three rows of height whether or not there was a tunnel to show.
/// </remarks>
public partial class TunnelsView : UserControl
{
    public TunnelsView() => InitializeComponent();
}
