using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

public partial class ServerViewModel
{
    /// <summary>What the Discord presence says of this server; null unless it is fully up.</summary>
    internal DiscordServer? DiscordSnapshot() =>
        State != ServerState.Running
            ? null
            : new DiscordServer(Name, Config.Type, Config.GameVersion,
                _process.PlayerTrackingReliable ? ConnectedPlayers.Count : null,
                _maxPlayers, _process.StartedAtUtc);
}
