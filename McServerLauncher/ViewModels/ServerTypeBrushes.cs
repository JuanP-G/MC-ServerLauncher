using Avalonia.Media;
using Avalonia.Media.Immutable;
using McServerLauncher.Models;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Single source of truth for the per-type badge colors (EFI-6): the server list's type badge
/// (ServerViewModel) and the mods browser's filter chip (ServerModsViewModel) used to keep two
/// separate copies of this palette, so adding a server type meant updating both. Immutable
/// brushes, safe to share across controls and threads.
/// </summary>
public static class ServerTypeBrushes
{
    private static readonly IBrush Vanilla = Make("#6E9E52");
    private static readonly IBrush Fabric = Make("#B58D5A");
    private static readonly IBrush Forge = Make("#5A8AB5");
    private static readonly IBrush Paper = Make("#C0563E");
    private static readonly IBrush Unknown = Make("#6E7681");

    /// <summary>Badge color for a server type; unknown/future types fall back to gray.</summary>
    public static IBrush For(ServerType type) => type switch
    {
        ServerType.Vanilla => Vanilla,
        ServerType.Fabric => Fabric,
        ServerType.Forge => Forge,
        ServerType.Paper => Paper,
        _ => Unknown
    };

    private static IBrush Make(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
